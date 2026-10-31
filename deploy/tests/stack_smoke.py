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
throwaway environment file from this host's address; `secrets init`; `check`; `up`; then the assertions
below. Unless --keep is given it removes the project and its volumes and the work folder at the end.

The report names checks, outcomes, classes and counts. It never contains a secret value, a host
address or a path under the work folder. Since #178 Caddy keeps no source allow-list on a published
route (the NAS firewall is the control there), so every host-origin check is conclusive whatever
source address Caddy sees; the two source-address `info` lines remain as the record of what it sees.
The 8443 refusals of the admin paths are sent with `curl --path-as-is` (no client-side normalisation)
and must answer 403 with an empty body, which shows the 403 is Caddy's own (G4-178-01).

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
ADMIN_PORT = guards.ADMIN_PORT
# G4-178-01 (and S-178-02): forms of the admin paths sent to the id host on 8443, byte for byte. Each is
# 403 with an empty body: the admin-path refusal or the catch-all, never Keycloak's own answer.
ADMIN_VARIANT_PATHS = (
    "/Admin/", "/%61dmin/", "//admin/", "/realms//master/", "/realms%2fmaster/", "/realms/decisya/../master/",
    "/realms/decisya/%2e%2e/master/", "/realms/decisya%2f..%2fmaster/", "/realms/decisya/..;/master/",
    "/resources/../admin/", "/resources/%2e%2e/admin/", "/admin;x/", "/realms/decisya/%252e%252e/master/",
)
# On 8444 from the host: dot-segment forms that would step into the decisya realm.
ADMIN_LISTENER_VARIANT_PATHS = ("/realms/master/../decisya/", "/admin/%2e%2e/realms/decisya/")
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


def curl_status(host: str, path: str, bind: str, cacert: Path, method: str = "GET", body_file: Path | None = None,
                port: int = PORT, path_as_is: bool = False) -> int | None:
    argv = ["curl", "-sS", "--max-time", "20", "-X", method, "-o", str(body_file or os.devnull), "-w", "%{http_code}",
            "--cacert", str(cacert), "--resolve", "%s:%d:%s" % (host, port, bind)]
    if path_as_is:
        argv.append("--path-as-is")
    proc = sh(argv + ["https://%s:%d%s" % (host, port, path)], timeout=60)
    try:
        return int(proc.stdout.strip()[-3:])
    except ValueError:
        return None


def refusal_probe(host: str, path: str, bind: str, cacert: Path, work: Path, port: int) -> tuple:
    """(status, body size in bytes) of one request sent as written (`--path-as-is`). A 403 with an empty
    body is Caddy's own; Keycloak's 403 would carry a page."""
    body = work / "probe-body"
    body.unlink(missing_ok=True)
    status = curl_status(host, path, bind, cacert, body_file=body, port=port, path_as_is=True)
    return status, (body.stat().st_size if body.is_file() else -1)


def client_status(runner, caddy_image: str, network: str, url: str, post: bool = False, host_header: str | None = None) -> int | None:
    """One-shot client on a stack network (the pinned Caddy image, removed afterwards). This is a routing
    check: certificate trust is proven by the trust integration test, so verification is off here."""
    argv = [runner.docker, "run", "--rm", "--network", network, "--entrypoint", "wget", caddy_image,
            "--no-check-certificate", "-S", "-T", "20", "-O", "/dev/null"]
    if post:
        argv += ["--post-data", "smoke=1"]
    if host_header:
        argv += ["--header", "Host: %s" % host_header]
    proc = sh(argv + [url], timeout=120)
    found = re.findall(r"HTTP/\d(?:\.\d)? (\d{3})", proc.stderr + proc.stdout)
    return int(found[-1]) if found else None


def secret_needles(stack: Path) -> dict:
    needles: dict = {}
    for path in (stack / "secrets").iterdir():
        text = path.read_text(encoding="utf-8")
        if len(text) >= 16:
            needles[text] = path.name
        # #122 (S-122-07): the key ring's certificate is a long base64 value, so a log line that holds only a
        # piece of it must be found too: three 32-character windows besides the whole value. The password
        # files are already covered by the whole-value rule above (an empty previous pair has nothing to find).
        if path.name in (guards.DP_CERT, guards.DP_PREVIOUS) and len(text) >= 96:
            for start in (len(text) // 4, len(text) // 2, len(text) - 40):
                needles[text[start:start + 32]] = path.name
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
    target = sh(["dotnet", "msbuild", "src/Decisya.AppHost/Decisya.AppHost.csproj", "-getProperty:TargetPath", "-p:Configuration=Release"], cwd=clone)
    dlls = [Path(target.stdout.strip())] if target.returncode == 0 and target.stdout.strip() else []
    if not report.check(len(dlls) == 1 and dlls[0].is_file(), "AppHost build output found"):
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
    values = dict(HOSTS, DECISYA_BIND_ADDRESS=bind, DECISYA_HTTPS_PORT=str(PORT), DECISYA_ADMIN_PORT=str(ADMIN_PORT))
    for key, service in guards.IMAGE_KEYS.items():
        values[key] = refs.get(service, "")
    (stack / stackctl.ENV_NAME).write_text("".join("%s=%s\n" % (k, values[k]) for k in guards.ENV_KEYS), encoding="utf-8")
    report.check(sh(clone_ctl + ["secrets", "init", "--stack", str(stack)], cwd=clone).returncode == 0, "stackctl secrets init")
    # #222 (ADR-0022 item 4): `check` and `up` refuse without a fresh green scan of exactly these images. The
    # smoke runs the scan and reports its result; while a non-excepted finding exists (the Keycloak case, O2) it
    # stops here, as the stack would. There is no bypass.
    scan = sh(clone_ctl + ["scan", "--stack", str(stack)], cwd=clone, timeout=3600)
    scan_detail = "; ".join(l.split(": ", 1)[1] for l in scan.stdout.splitlines() if l.startswith(("FINDING: ", "UNTRUSTED: ")) and ": " in l)[:600]
    if not report.check(scan.returncode == 0, "stackctl scan (a fresh green result, the gate of check and up)", "exit %d: %s" % (scan.returncode, scan_detail)):
        return 1
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
    # No source allow-list stands between this client and a published route (#178): these are conclusive.
    id_host = HOSTS["DECISYA_ID_HOST"]
    spa = curl_status(HOSTS["DECISYA_APP_HOST"], "/", bind, cacert, body_file=body)
    if report.check(spa == 200, "SPA answers on the app host, port 8443", str(spa)):
        text = body.read_text(encoding="utf-8", errors="replace").lower() if body.is_file() else ""
        report.check("<html" in text or "<!doctype html" in text, "SPA index.html on the app host", "200")
    capabilities = curl_status(HOSTS["DECISYA_APP_HOST"], "/api/capabilities", bind, cacert)
    report.check(capabilities == 401, "/api/capabilities without a session is 401", str(capabilities))
    realm = curl_status(id_host, "/realms/decisya/", bind, cacert)
    report.check(realm == 200, "the decisya realm is imported and answers on the id host (#121)", str(realm))
    # The admin paths are refused on 8443, whoever asks (G2 D1 route 2).
    for path in ("/admin/", "/realms/master/"):
        status = curl_status(id_host, path, bind, cacert)
        report.check(status == 403, "%s on the id host, port 8443, is 403" % path, str(status))
    # G4-178-01 and S-178-02: every variant, as written, is 403 with an empty body.
    for path in ADMIN_VARIANT_PATHS:
        status, size = refusal_probe(id_host, path, bind, cacert, work, PORT)
        report.check(status == 403 and size == 0, "variant %s on the id host, port 8443, is 403 with an empty body" % path,
                     "status %s, %d bytes" % (status, size))
    # The admin listener (8444): Keycloak answers the console, and nothing of the decisya realm is routed.
    console = curl_status(id_host, "/admin/", bind, cacert, port=ADMIN_PORT)
    report.check(console is not None and console not in (403, 502, 503, 504), "/admin/ on the id host, port 8444, is answered by Keycloak", str(console))
    realm_on_admin = curl_status(id_host, "/realms/decisya/", bind, cacert, port=ADMIN_PORT)
    report.check(realm_on_admin == 403, "/realms/decisya/ on the id host, port 8444, is 403", str(realm_on_admin))
    for path in ADMIN_LISTENER_VARIANT_PATHS:
        status, size = refusal_probe(id_host, path, bind, cacert, work, ADMIN_PORT)
        report.check(status == 403 and size == 0, "variant %s on the id host, port 8444, is 403 with an empty body" % path,
                     "status %s, %d bytes" % (status, size))
    # D3 (unverified until this check has run): the console page served on 8444 names the 8444 origin, never 8443.
    page = work / "console.html"
    shell = curl_status(id_host, "/admin/master/console/", bind, cacert, body_file=page, port=ADMIN_PORT)
    html = page.read_text(encoding="utf-8", errors="replace").replace("\\", "") if page.is_file() else ""
    report.check(shell == 200 and ("%s:%d" % (id_host, ADMIN_PORT)) in html and ("%s:%d" % (id_host, PORT)) not in html,
                 "the console page on port 8444 names the 8444 origin and never the 8443 origin (KC_HOSTNAME_ADMIN)", "status %s" % shell)
    for port in (80, 8080, 8081, 9000, 6379, 5432, 4317, 4318):
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
    report.check(status == 200, "back channel: /realms/decisya/ is answered by Keycloak with the imported realm (#121)", str(status))
    for path in ("/realms/master/", "/admin/"):
        status = client_status(runner, caddy_image, "decisya_backchannel", base % api[1] + path)
        report.check(status == 403, "back channel: %s on the id host is 403" % path, str(status))
    # T178-07: a container never uses the published admin route. The id host is an alias on the
    # back-channel network only; on the idp network the app host's alias is used with the id host's Host header.
    admin_url = "https://%s:%d/admin/"
    status = client_status(runner, caddy_image, "decisya_backchannel", admin_url % (api[1], ADMIN_PORT))
    report.check(status == 403, "back channel: /admin/ on port 8444 is 403 (a container never uses the published route)", str(status))
    status = client_status(runner, caddy_image, "decisya_idp", admin_url % (api[2], ADMIN_PORT), host_header=api[1])
    report.check(status == 403, "idp network: /admin/ on port 8444 is 403 (a container never uses the published route)", str(status))
    status = client_status(runner, caddy_image, "decisya_idp", base % api[2] + "/", host_header=None)
    report.check(status == 403, "idp network: the app host's root is 403 (the published app route is not for containers)", str(status))
    status = client_status(runner, caddy_image, "decisya_idp", base % api[2] + "/bff/backchannel-logout", post=True)
    report.check(status is not None and status not in (403, 502, 503, 504), "idp network: POST /bff/backchannel-logout reaches the BFF", str(status))
    status = client_status(runner, caddy_image, "decisya_idp", base % api[2] + "/bff/backchannel-logout")
    report.check(status == 403, "idp network: any other method on the logout path is 403", str(status))

    # ---- first-start checks recorded for the manifest
    status = sh(["docker", "inspect", "--format", "{{.State.Status}} {{json .HostConfig.CapDrop}}", container_id(runner, "caddy")]).stdout.strip()
    report.info("caddy under cap_drop ALL", status)
    capabilities = runner.compose(["exec", "-T", "caddy", "cat", "/proc/1/status"]).stdout
    report.info("caddy PID 1", "; ".join(l.replace("\t", " ") for l in capabilities.splitlines() if l.split(":")[0] in ("Uid", "CapEff", "CapBnd", "NoNewPrivs")))
    # #122 (S-122-06, Story 8): the old probe wrote to the volume as uid 1654 with no hardening. The read-only
    # tool replaces it: no network, read-only root, every capability dropped, the volume mounted read-only.
    # `up` has already run `keyring prepare`, so owner and modes are the contract, and the BFF has written
    # its first key. Counts only; no name or content is read out.
    probe = sh(stackctl.keyring_verify_argv(stackctl.pinned_caddy_image(REPO), docker=runner.docker), timeout=180)
    facts, reason = guards.parse_keyring_report(probe.stdout) if probe.returncode == 0 else (None, "exit %d" % probe.returncode)
    report.check(facts is not None, "key-ring check runs with --network none, --read-only, --cap-drop ALL", reason or "")
    if facts is not None:
        report.info("bff key ring", ", ".join("%s=%d" % (k, facts[k]) for k in guards.KEYRING_REPORT_KEYS))
        report.check(facts["wrong_owner"] == 0 and facts["wrong_mode"] == 0, "key ring: owner 1654, folders 0700, files 0600 (keyring prepare)")
        report.check(facts["xml_failing"] == 0 and facts["refused_entries"] == 0, "key ring: no plaintext key and no special entry")
        report.check(facts["xml_ok"] >= 1, "key ring: the BFF has written a certificate-wrapped key")

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

    # ---- the rate limiter behind the edge (#122, G2 D8, G3 G4-122-01 b). Last on purpose: it spends this
    # client's `login` budget for a minute.
    rate_limit_checks(runner, report, bind, cacert)
    return 1 if report.failed else 0


# Documentation-range addresses only (RFC 5737): they are invented, never a real client.
FORGED_FORWARDED_FOR = ["203.0.113.%d" % n for n in range(1, 12)]


def parse_curl_probe(text: str) -> tuple:
    """(status, response headers) from `curl -D - -w "\\n%{http_code}"` output. Header names are lower-cased."""
    stripped = text.rstrip()
    status = None
    if stripped[-3:].isdigit():
        status = int(stripped[-3:])
    headers: dict = {}
    for line in text.splitlines():
        name, separator, value = line.partition(":")
        if separator and re.fullmatch(r"[A-Za-z0-9-]+", name):
            headers[name.lower()] = value.strip()
    return status, headers


def curl_probe(host: str, path: str, bind: str, cacert: Path, headers=()) -> tuple:
    argv = ["curl", "-sS", "--max-time", "20", "-o", os.devnull, "-D", "-", "-w", "\n%{http_code}", "--cacert", str(cacert),
            "--resolve", "%s:%d:%s" % (host, PORT, bind)]
    for header in headers:
        argv += ["-H", header]
    proc = sh(argv + ["https://%s:%d%s" % (host, PORT, path)], timeout=60)
    return parse_curl_probe(proc.stdout)


def ratelimit_log_verdict(log_text: str) -> dict:
    """Counts of the BFF's `ratelimit.rejected` events (event id 1820) in its JSON log, by their message's
    ending. Only counts leave this function: no address, no session, no line."""
    messages = []
    for line in log_text.splitlines():
        try:
            entry = json.loads(line.strip())
        except ValueError:
            continue
        if isinstance(entry, dict) and entry.get("event_id") == 1820:
            messages.append(str(entry.get("message") or "").rstrip("."))
    return {
        "rejected": len(messages),
        "login_ip": sum(1 for m in messages if m.endswith("(login, ip)")),
        "login_unknown": sum(1 for m in messages if m.endswith("(login, unknown)")),
    }


def retry_after_ok(value) -> bool:
    """A whole number of seconds from 1 to 60 (the login window is 60 s)."""
    return isinstance(value, str) and re.fullmatch(r"[0-9]{1,3}", value) is not None and 1 <= int(value) <= 60


def rate_limit_checks(runner, report: Report, bind: str, cacert: Path) -> None:
    host = HOSTS["DECISYA_APP_HOST"]
    results = [curl_probe(host, "/bff/login", bind, cacert, ["X-Forwarded-For: %s" % forged]) for forged in FORGED_FORWARDED_FOR]
    statuses = [status for status, _ in results]
    last_status, last_headers = results[-1]
    # One check proves both the overwrite (the forged values opened no new bucket) and the pass-through.
    report.check(all(s is not None and s != 429 for s in statuses[:-1]), "rate limit: the first 10 GET /bff/login are not refused, whatever X-Forwarded-For says")
    report.check(last_status == 429, "rate limit: the 11th GET /bff/login is 429 (Caddy replaced the forged X-Forwarded-For)", str(last_status))
    report.check(retry_after_ok(last_headers.get("retry-after")), "rate limit: the 429 reaches the client with a whole-second Retry-After")
    report.check("application/problem+json" in last_headers.get("content-type", ""), "rate limit: the 429 body is a ProblemDetails",
                 last_headers.get("content-type", ""))
    time.sleep(2)
    log = runner.compose(["logs", "--no-color", "--no-log-prefix", "bff"]).stdout
    verdict = ratelimit_log_verdict(log)
    # G4-122-01 (b): one `ratelimit.rejected`, and its partition is the client's address. `unknown` would mean
    # the BFF did not trust Caddy's X-Forwarded-For (Decisya:Edge:TrustedProxies), so every caller shares one bucket.
    report.check(verdict["rejected"] == 1 and verdict["login_ip"] == 1, "rate limit: exactly one ratelimit.rejected (event 1820) ends (login, ip)",
                 "events=%d ip=%d" % (verdict["rejected"], verdict["login_ip"]))
    report.check(verdict["login_unknown"] == 0, "rate limit: no (login, unknown): the BFF trusts the edge's X-Forwarded-For",
                 "unknown=%d" % verdict["login_unknown"])


def container_id(runner, service: str) -> str:
    return runner.compose(["ps", "-aq", service]).stdout.strip()


def guards_image(runner, service: str) -> str:
    return str((runner.config_json().get("services") or {}).get(service, {}).get("image", ""))


if __name__ == "__main__":
    sys.exit(main())
