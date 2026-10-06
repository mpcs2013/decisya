#!/usr/bin/env python3
"""Clean-clone stack smoke on a Linux Docker host (issue #120, architecture note D10 item 12).

NOT part of CI and not a unit test: Marco (or G5) runs it on the host, and the report is recorded in
docs/ai/pipeline/120.md. It proves the Done-when for #120: from a fresh clone, the documented steps
start the full stack with only the prepared secrets and local configuration. It also records the
first-start checks the design deferred (Caddy under `cap_drop: ALL`, the key ring's writability,
whether published ports keep the client's source address) and the idle resource figures #132 needs.

    python deploy/tests/stack_smoke.py --workdir /srv/scratch/decisya-smoke --images-txt /path/to/images.txt

What it does, in order: refuse a host that already runs a `decisya` Compose project; `git clone` the
current branch into the work folder; publish with the built AppHost; `stackctl.py assemble`; write a
throwaway environment file from this host's address (documentation-range workstation, so this client is
never the workstation); `secrets init`; `check`; `up`; then the assertions below. Unless --keep is
given it removes the project and its volumes and the work folder at the end.

The report names checks, outcomes, classes and counts. It never contains a secret value, a host
address or a path under the work folder. "Inconclusive" means the host-origin LAN checks could not be
judged because published ports do not keep this client's source address on this host: the design then
leaves the admin paths unrouted and never widens an allow-list to a bridge subnet (ADR-0016 R9.8), so
run the listed LAN checks from a second machine instead.

Unreleased branch: the release images do not exist yet. Build or `docker load` the three release images
so their digests resolve locally, write an images.txt with those `ghcr.io/mpcs2013/decisya-<name>@sha256:`
references, pass it with `--images-source unreleased`, and the report records that.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import os
import platform
import re
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "deploy" / "compose"))

import stackctl  # noqa: E402
import stackguards as guards  # noqa: E402

PORT = guards.HTTPS_PORT
WORKSTATION = "192.0.2.99"  # RFC 5737: this client is never the workstation
HOSTS = {"DECISYA_APP_HOST": "app.p0.home.arpa", "DECISYA_ID_HOST": "id.p0.home.arpa", "DECISYA_API_HOST": "api.p0.home.arpa"}
HEALTHY = ("postgres", "redis", "keycloak", "caddy", "api", "bff")


class Report:
    def __init__(self):
        self.items: list = []

    def add(self, status: str, name: str, detail: str = "") -> None:
        self.items.append({"status": status, "name": name, "detail": detail})
        print("[%s] %s%s" % (status, name, (": " + detail) if detail else ""), flush=True)

    def ok(self, name, detail=""):
        self.add("PASS", name, detail)

    def fail(self, name, detail=""):
        self.add("FAIL", name, detail)

    def info(self, name, detail=""):
        self.add("INFO", name, detail)

    def inconclusive(self, name, detail=""):
        self.add("INCONCLUSIVE", name, detail)

    def check(self, condition: bool, name: str, detail: str = "") -> bool:
        (self.ok if condition else self.fail)(name, detail)
        return condition

    @property
    def failed(self) -> bool:
        return any(i["status"] == "FAIL" for i in self.items)


def sh(argv, cwd=None, timeout=900, input_text=None):
    return subprocess.run(argv, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=timeout, input=input_text)


def host_address() -> str:
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("192.0.2.1", 9))  # no packet is sent: this only picks the route's source address
        return probe.getsockname()[0]
    finally:
        probe.close()


def classify(address: str, bind: str, subnets: list) -> str:
    if address == bind:
        return "the host's own address"
    try:
        ip = ipaddress.ip_address(address)
    except ValueError:
        return "unparsable"
    for _, cidr in subnets:
        net = ipaddress.ip_network(cidr, strict=False)
        if ip.version == net.version and ip in net:
            return "a Docker network address"
    return "another address"


def container_states(runner) -> dict:
    listing = runner.compose(["ps", "-aq"])
    ids = listing.stdout.split()
    if not ids:
        return {}
    proc = runner.run([runner.docker, "inspect", *ids])
    states = {}
    for container in json.loads(proc.stdout):
        labels = (container.get("Config") or {}).get("Labels") or {}
        state = container.get("State") or {}
        states[labels.get("com.docker.compose.service", "?")] = {
            "status": state.get("Status"), "health": (state.get("Health") or {}).get("Status"), "exit": state.get("ExitCode"),
        }
    return states


def wait_for_stack(runner, seconds: int) -> dict:
    deadline = time.monotonic() + seconds
    states: dict = {}
    while time.monotonic() < deadline:
        states = container_states(runner)
        ready = (all(states.get(s, {}).get("health") == "healthy" for s in HEALTHY)
                 and states.get("otel-collector", {}).get("status") == "running"
                 and states.get("migrator", {}).get("status") == "exited")
        if ready:
            break
        time.sleep(5)
    return states


def curl_status(host: str, path: str, bind: str, cacert: Path, method: str = "GET", body_file: Path | None = None) -> int | None:
    argv = ["curl", "-sS", "--max-time", "20", "-X", method, "-o", str(body_file or os.devnull), "-w", "%{http_code}",
            "--cacert", str(cacert), "--resolve", "%s:%d:%s" % (host, PORT, bind), "https://%s:%d%s" % (host, PORT, path)]
    proc = sh(argv, timeout=60)
    try:
        return int(proc.stdout.strip()[-3:])
    except ValueError:
        return None


def client_status(runner, caddy_image: str, network: str, url: str, post: bool = False) -> int | None:
    """One-shot client on a stack network (the pinned Caddy image, removed afterwards). This is a routing
    check: certificate trust is proven by the trust integration test, so verification is off here."""
    argv = [runner.docker, "run", "--rm", "--network", network, "--entrypoint", "wget", caddy_image,
            "--no-check-certificate", "-S", "-T", "20", "-O", "/dev/null"]
    if post:
        argv += ["--post-data", "smoke=1"]
    proc = sh(argv + [url], timeout=120)
    found = re.findall(r"HTTP/\d(?:\.\d)? (\d{3})", proc.stderr + proc.stdout)
    return int(found[-1]) if found else None


def secret_needles(stack: Path) -> dict:
    needles: dict = {}
    for path in (stack / "secrets").iterdir():
        text = path.read_text(encoding="utf-8")
        if len(text) >= 16:
            needles[text] = path.name
        match = re.search(r"[Pp]assword=([A-Za-z0-9]+)$", text)
        if match:
            needles[match.group(1)] = path.name
        for digest in re.findall(r"#([0-9a-f]{64})", text):
            needles[digest] = path.name
    return needles


def scan_for_secrets(runner, stack: Path, report: Report, label: str) -> None:
    needles = secret_needles(stack)
    ids = runner.compose(["ps", "-aq"]).stdout.split()
    haystacks = {
        "docker inspect": runner.run([runner.docker, "inspect", *ids]).stdout if ids else "",
        "docker compose config": runner.compose(["config", "--no-path-resolution"]).stdout,
        "container logs": runner.compose(["logs", "--no-color"]).stdout,
    }
    found = sorted({(name, where) for value, name in needles.items() for where, text in haystacks.items() if value in text})
    report.check(len(needles) >= 10 and not found, "no secret value in inspect, config or logs (%s)" % label,
                 ", ".join("%s in %s" % f for f in found) if found else "%d values searched" % len(needles))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--workdir", required=True, help="a scratch folder outside every Git tree; it is created and removed")
    parser.add_argument("--images-txt", required=True, help="the release's images.txt (digests of api, bff, migrator)")
    parser.add_argument("--images-source", choices=("released", "unreleased"), default="released")
    parser.add_argument("--branch", help="the branch to clone (default: the current one)")
    parser.add_argument("--bind-address", help="a private IPv4 address of this host (default: the address of the default route)")
    parser.add_argument("--timeout", type=int, default=300, help="seconds the stack gets to come up (the F1 bound)")
    parser.add_argument("--keep", action="store_true", help="leave the project and the work folder for inspection")
    parser.add_argument("--allow-non-linux", action="store_true")
    parser.add_argument("--report", help="write the report as JSON to this file")
    args = parser.parse_args()
    report = Report()
    work = Path(args.workdir).resolve()
    stack = work / "stack"
    try:
        return run_smoke(args, report, work, stack)
    finally:
        if not args.keep:
            teardown(work, stack)
        if args.report:
            Path(args.report).write_text(json.dumps({"images_source": args.images_source, "items": report.items}, indent=2), encoding="utf-8")
        print("\nsmoke: %d PASS, %d FAIL, %d INCONCLUSIVE, %d INFO" % tuple(
            sum(1 for i in report.items if i["status"] == s) for s in ("PASS", "FAIL", "INCONCLUSIVE", "INFO")))


def teardown(work: Path, stack: Path) -> None:
    if (stack / stackctl.GENERATED_NAME).is_file() and (stack / stackctl.ENV_NAME).is_file():
        stackctl.Runner(stack).compose(["down", "--volumes", "--remove-orphans"], timeout=300)
    shutil.rmtree(work, ignore_errors=True)


def run_smoke(args, report: Report, work: Path, stack: Path) -> int:
    # ---- preconditions
    if platform.system() != "Linux" and not args.allow_non_linux:
        report.fail("a Linux Docker host", "this script targets the NAS-like case; pass --allow-non-linux to try anyway")
        return 1
    for tool in ("docker", "git", "dotnet", "curl"):
        if not shutil.which(tool):
            report.fail("tool %s is installed" % tool)
            return 1
    if sh(["docker", "info"], timeout=120).returncode != 0:
        report.fail("the Docker daemon answers")
        return 1
    for listing in (["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=%s" % guards.STACK_NAME],
                    ["docker", "volume", "ls", "-q", "--filter", "label=com.docker.compose.project=%s" % guards.STACK_NAME]):
        if sh(listing).stdout.strip():
            report.fail("no `%s` Compose project exists on this host" % guards.STACK_NAME,
                        "refusing: this script ends with `down --volumes` and would destroy it")
            return 1
    if work.exists() and any(work.iterdir()):
        report.fail("the work folder is empty or new")
        return 1
    if not Path(args.images_txt).is_file():
        report.fail("images.txt exists")
        return 1
    work.mkdir(parents=True, exist_ok=True)
    report.info("images source", args.images_source)

    # ---- clone, publish, assemble
    branch = args.branch or sh(["git", "rev-parse", "--abbrev-ref", "HEAD"], cwd=REPO).stdout.strip()
    if sh(["git", "status", "--porcelain"], cwd=REPO).stdout.strip():
        report.info("uncommitted changes", "are NOT in the clone; commit them first if they matter")
    clone = work / "clone"
    if not report.check(sh(["git", "clone", "--quiet", "--no-hardlinks", "--branch", branch, str(REPO), str(clone)]).returncode == 0, "fresh clone of the branch"):
        return 1
    build = sh(["dotnet", "build", "src/Decisya.AppHost/Decisya.AppHost.csproj", "--configuration", "Release", "-warnaserror"], cwd=clone, timeout=1800)
    if not report.check(build.returncode == 0, "AppHost builds from the clone", "exit %d" % build.returncode):
        return 1
    dlls = list((clone / "artifacts" / "bin" / "Decisya.AppHost").rglob("Decisya.AppHost.dll"))
    if not report.check(len(dlls) >= 1, "AppHost build output found"):
        return 1
    published = work / "publish"
    published.mkdir()
    publish = sh(["dotnet", str(dlls[0]), "--operation", "publish", "--step", "publish", "--output-path", str(published)], cwd=clone, timeout=600)
    report.check(publish.returncode == 0 and sorted(p.name for p in published.iterdir()) == ["docker-compose.yaml"],
                 "publish writes only docker-compose.yaml", "exit %d" % publish.returncode)
    clone_ctl = [sys.executable, str(clone / "deploy" / "compose" / "stackctl.py")]
    assemble = sh(clone_ctl + ["assemble", "--stack", str(stack), "--published", str(published / "docker-compose.yaml")], cwd=clone)
    if not report.check(assemble.returncode == 0, "stackctl assemble (published equals committed)", "exit %d" % assemble.returncode):
        return 1

    # ---- throwaway environment, images, secrets, check
    bind = args.bind_address or host_address()
    shutil.copyfile(args.images_txt, stack / stackctl.IMAGES_NAME)
    refs = guards.parse_images_txt((stack / stackctl.IMAGES_NAME).read_text(encoding="utf-8"))
    values = dict(HOSTS, DECISYA_BIND_ADDRESS=bind, DECISYA_HTTPS_PORT=str(PORT), DECISYA_LAN_SUBNET=bind + "/32",
                  DECISYA_WORKSTATION_ADDRESS=WORKSTATION)
    for key, service in guards.IMAGE_KEYS.items():
        values[key] = refs.get(service, "")
    (stack / stackctl.ENV_NAME).write_text("".join("%s=%s\n" % (k, values[k]) for k in guards.ENV_KEYS), encoding="utf-8")
    report.check(sh(clone_ctl + ["secrets", "init", "--stack", str(stack)], cwd=clone).returncode == 0, "stackctl secrets init")
    check = sh(clone_ctl + ["check", "--stack", str(stack)], cwd=clone, timeout=900)
    if not report.check(check.returncode == 0, "stackctl check", "exit %d: %s" % (check.returncode, "; ".join(l[9:] for l in check.stdout.splitlines() if l.startswith("PROBLEM:"))[:600])):
        return 1

    # ---- up
    runner = stackctl.Runner(stack)
    up = sh(clone_ctl + ["up", "--stack", str(stack)], cwd=clone, timeout=1800)
    report.check(up.returncode == 0, "stackctl up (caddy, export-root, the rest, verify)", "exit %d" % up.returncode)
    states = wait_for_stack(runner, args.timeout)
    for service in HEALTHY:
        report.check(states.get(service, {}).get("health") == "healthy", "service %s is healthy" % service, str(states.get(service, {}).get("status")))
    report.check(states.get("otel-collector", {}).get("status") == "running", "service otel-collector is running")
    report.check(states.get("migrator", {}).get("status") == "exited" and states.get("migrator", {}).get("exit") == 0,
                 "the migrator exited 0 within the F1 bound (%d s)" % args.timeout)
    cacert = stack / "trust" / "caddy-root.crt"
    report.check(cacert.is_file() and not stackctl.root_problems(cacert), "the exported root is one certificate and no key")
    subnets = stackctl.Runner(stack).docker_subnets()

    # ---- host-origin checks
    body = work / "body.html"
    spa = curl_status(HOSTS["DECISYA_APP_HOST"], "/", bind, cacert, body_file=body)
    if spa == 200:
        text = body.read_text(encoding="utf-8", errors="replace").lower() if body.is_file() else ""
        report.check("<html" in text or "<!doctype html" in text, "SPA index.html on the app host", "200")
        capabilities = curl_status(HOSTS["DECISYA_APP_HOST"], "/api/capabilities", bind, cacert)
        report.check(capabilities == 401, "/api/capabilities without a session is 401", str(capabilities))
        realm = curl_status(HOSTS["DECISYA_ID_HOST"], "/realms/decisya/", bind, cacert)
        report.check(realm in (200, 302, 404), "the decisya realm path reaches Keycloak (404 until #121)", str(realm))
    else:
        report.inconclusive("LAN checks from the host (SPA 200, /api/capabilities 401, realm path)",
                            "the app host answered %s: this client's source address is not the LAN address Caddy sees; run these from a second machine" % spa)
    for path in ("/admin/", "/realms/master/"):
        status = curl_status(HOSTS["DECISYA_ID_HOST"], path, bind, cacert)
        report.check(status == 403, "%s on the id host is 403 from a non-workstation source" % path, str(status))
    for port in (80, 8080, 8081, 9000, 6379, 5432, 4317, 4318, 8444):
        refused = True
        try:
            socket.create_connection((bind, port), timeout=3).close()
            refused = False
        except OSError:
            pass
        report.check(refused, "port %d on the bind address is refused" % port)
    for port in (8081, 9000):
        refused = True
        try:
            socket.create_connection(("127.0.0.1", port), timeout=3).close()
            refused = False
        except OSError:
            pass
        report.check(refused, "nothing listens on %d from the host" % port)

    # ---- the source-address question
    log = runner.compose(["logs", "--no-color", "--no-log-prefix", "caddy"]).stdout
    seen = []
    for line in log.splitlines():
        try:
            request = (json.loads(line).get("request") or {})
        except ValueError:
            continue
        address = request.get("remote_ip") or request.get("client_ip")
        if address:
            seen.append(classify(address, bind, subnets))
    report.info("source address Caddy sees for host-origin requests", ", ".join(sorted(set(seen))) or "no access-log entry")
    report.info("published ports keep the client's address", "yes" if seen and set(seen) == {"the host's own address"} else "no or not shown on this path")

    # ---- back-channel routing, from one-shot clients on the stack's own networks
    caddy_image = guards_image(runner, "caddy")
    api = HOSTS["DECISYA_API_HOST"], HOSTS["DECISYA_ID_HOST"], HOSTS["DECISYA_APP_HOST"]
    base = "https://%s:%d" % ("%s", PORT)
    status = client_status(runner, caddy_image, "decisya_backchannel", base % api[0] + "/api/capabilities")
    report.check(status == 401, "back channel: the api host answers /api/capabilities with 401 from the Api", str(status))
    status = client_status(runner, caddy_image, "decisya_backchannel", base % api[1] + "/realms/decisya/")
    report.check(status in (200, 302, 404), "back channel: /realms/decisya/ is answered by Keycloak", str(status))
    for path in ("/realms/master/", "/admin/"):
        status = client_status(runner, caddy_image, "decisya_backchannel", base % api[1] + path)
        report.check(status == 403, "back channel: %s on the id host is 403" % path, str(status))
    status = client_status(runner, caddy_image, "decisya_idp", base % api[2] + "/bff/backchannel-logout", post=True)
    report.check(status is not None and status not in (403, 502, 503, 504), "idp network: POST /bff/backchannel-logout reaches the BFF", str(status))
    status = client_status(runner, caddy_image, "decisya_idp", base % api[2] + "/bff/backchannel-logout")
    report.check(status == 403, "idp network: any other method on the logout path is 403", str(status))

    # ---- first-start checks recorded for the manifest
    status = sh(["docker", "inspect", "--format", "{{.State.Status}} {{json .HostConfig.CapDrop}}", container_id(runner, "caddy")]).stdout.strip()
    report.info("caddy under cap_drop ALL", status)
    capabilities = runner.compose(["exec", "-T", "caddy", "cat", "/proc/1/status"]).stdout
    report.info("caddy PID 1", "; ".join(l.replace("\t", " ") for l in capabilities.splitlines() if l.split(":")[0] in ("Uid", "CapEff", "CapBnd", "NoNewPrivs")))
    probe = sh([runner.docker, "run", "--rm", "--user", "1654:1654", "-v", "decisya_bff-keyring:/k", "--entrypoint", "sh", caddy_image,
                "-c", "ls -ldn /k | cut -d' ' -f1,3,4; ls -1A /k | wc -l; touch /k/.smoke-probe && rm /k/.smoke-probe && echo writable"])
    report.info("bff key ring, as uid 1654", " | ".join(probe.stdout.split("\n")[:3]).strip())
    report.check("writable" in probe.stdout, "UID 1654 can write the key-ring volume")

    # ---- no secret value anywhere, then again after stopping and starting the stores (S-120-05)
    scan_for_secrets(runner, stack, report, "after a healthy start")
    for service in ("redis", "postgres"):
        runner.compose(["stop", service], timeout=120)
        time.sleep(5)
        runner.compose(["start", service], timeout=120)
    wait_for_stack(runner, 120)
    scan_for_secrets(runner, stack, report, "after stopping and starting redis and postgres")

    # ---- idle figures for #132, and the final guard
    stats = sh(["docker", "stats", "--no-stream", "--format", "{{.Name}} {{.MemUsage}} {{.CPUPerc}}"]).stdout
    for line in stats.splitlines():
        if line.startswith("decisya-"):
            report.info("idle", line)
    verify = sh(clone_ctl + ["verify", "--stack", str(stack)], cwd=clone)
    report.check(verify.returncode == 0, "stackctl verify (docker inspect guards) after the run")
    return 1 if report.failed else 0


def container_id(runner, service: str) -> str:
    return runner.compose(["ps", "-aq", service]).stdout.strip()


def guards_image(runner, service: str) -> str:
    return str((runner.config_json().get("services") or {}).get(service, {}).get("image", ""))


if __name__ == "__main__":
    sys.exit(main())
