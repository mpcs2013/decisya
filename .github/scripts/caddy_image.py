#!/usr/bin/env python3
"""Self-built Caddy edge image: inputs, buildlist, build, inspect, push, sign, verify (issue #188, ADR-0021).

Stdlib only. It reuses the helpers of release_images.py (archive reading, gitleaks, cosign, identity flags,
negative-case classifier) without changing them, and the ADR-0015 grype policy of image_scan.py without
changing it either. Every tool (syft, cosign, gitleaks, grype) is a digest-pinned image run with an argv list
and no shell. Tool pins: .github/release/tools.Dockerfile and .github/image-scan/Dockerfile. Build inputs:
deploy/caddy/image/.

Subcommands (caddy-image.yml calls exactly these):
  inputs         static checks of the build inputs, no Docker, no network (G3 M-1 a to e)
  buildlist      the Go checks in the pinned builder container: go.mod `go` line against Caddy's, and our build
                 list against the upstream build list of the same Caddy version (ADR-0021 item 3, G3 S-1)
  build          docker build of the image and docker save to <RUNNER_TEMP>/caddy/out/caddy.tar, no push
  inspect        canary, config and file checks, per-layer gitleaks, version binding, caddy list-modules against
                 modules.txt, grype with strict D-2 (no ref, no tag state, no exceptions), syft SBOM and its
                 evidence (Caddy, stdlib, BusyBox); writes the archive config digest and SHA-256 outputs
  push           verify the downloaded archive against those outputs, docker load, push, check the config digest
  sign           cosign sign and attest, by digest
  verify         positive, attestation and negative verification
  verify-command print the exact consumer command for a pinned reference (the runbook uses this output)
  pins           parse and print the Dockerfile pins

Everything derived from tools, images or archives is printed between ::stop-commands:: markers with control
characters escaped. Outputs are written to $GITHUB_OUTPUT only after the markers are closed.
Exit codes: 0 pass, 1 failure (fail closed), 2 usage or configuration error.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shlex
import shutil
import sys
import tarfile
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import release_images as ri  # noqa: E402
from release_images import ReleaseError, log, run_checked, tail  # noqa: E402

ROOT = ri.ROOT
INPUT_DIR = ROOT / "deploy" / "caddy" / "image"
DOCKERFILE = INPUT_DIR / "Dockerfile"
GO_MOD = INPUT_DIR / "go.mod"
GO_SUM = INPUT_DIR / "go.sum"
MAIN_GO = INPUT_DIR / "main.go"
MODULES_TXT = INPUT_DIR / "modules.txt"

IMAGE_NAME = "decisya-caddy"
REMOTE = f"{ri.REGISTRY}/{ri.OWNER}/{IMAGE_NAME}"
# The one verification identity of this image. Never a regular expression. release.yml's identity must not verify it.
IDENTITY = f"https://github.com/{ri.REPO}/.github/workflows/caddy-image.yml@{ri.WORKFLOW_REF}"
SOURCE_URL = f"https://github.com/{ri.REPO}"

CADDY_MODULE = "github.com/caddyserver/caddy/v2"
MAIN_IMPORTS = {"github.com/caddyserver/caddy/v2/cmd", "github.com/caddyserver/caddy/v2/modules/standard"}
# The image contract (docs/architecture/self-built-caddy.md): the union of what the stack uses. Nothing else is linked.
APPLETS = ("[", "cat", "chmod", "chown", "echo", "find", "grep", "nc", "printf", "rm", "sh", "test", "tr", "wc", "wget")
RELEASE_FLAGS = "-trimpath -buildvcs=false -tags nobadger,nomysql,nopgx"
LDFLAGS = '-ldflags "-s -w -buildid="'
# ADR-0021 item 2, asserted against the Dockerfile ENV and passed to every builder container run.
GO_ENV = {
    "CGO_ENABLED": "0", "GOOS": "linux", "GOARCH": "amd64", "GOAMD64": "v1", "GOTOOLCHAIN": "local",
    "GOFLAGS": "-mod=readonly", "GOWORK": "off", "GOPROXY": "https://proxy.golang.org", "GOSUMDB": "sum.golang.org",
    "GOPRIVATE": "", "GONOPROXY": "", "GONOSUMDB": "", "GOINSECURE": "",
}
RUNTIME_ENV = {"XDG_CONFIG_HOME": "/config", "XDG_DATA_HOME": "/data"}
# SSL_CERT_FILE comes from the pinned distroless base, not from our Dockerfile. Exactly this key with exactly this
# value is allowed in the image Env (Marco, 2026-10-23, option a); any other value or key fails.
# PATH is pinned too (G6 F-1): a base bump that reorders it could put another directory ahead of the BusyBox links.
BASE_ENV = {
    "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "SSL_CERT_FILE": "/etc/ssl/certs/ca-certificates.crt",
}
# Root symlinks of the merged-/usr base. When present they must point exactly here (G6 F-1), otherwise canonical()
# would map names onto a directory the image does not really use and hide a hijack of /bin.
ROOT_LINKS = {"bin": "usr/bin", "sbin": "usr/sbin", "lib": "usr/lib", "lib64": "usr/lib64"}
ENV_ALLOW = ("PATH", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "SSL_CERT_FILE")
# Regular files that carry an exec bit in gcr.io/distroless/static-debian13 at the pinned digest (found by the
# container proofs, Marco's decision 2026-10-23, option a). They are data files, not programs, and sit outside every
# PATH directory. Any other executable, or a change to this set after a base bump, fails the build.
BASE_EXEC_FILES = (
    "etc/ssl/certs/ca-certificates.crt",
    "etc/update-motd.d/10-uname",
    "usr/lib/os-release",
    "usr/share/doc/ca-certificates/copyright",
)
RUNTIME_CMD = ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
INPUT_FILES = ("Dockerfile", "main.go", "go.mod", "go.sum", "modules.txt", "README.md")
STAGES = ("builder", "busybox", "runtime")
PATH_DIRS = ("usr/local/sbin", "usr/local/bin", "usr/sbin", "usr/bin")
PUSH_TAG_RE = re.compile(r"^(\d+\.\d+\.\d+)-([0-9a-f]{40})$")
DRY_TAG_RE = re.compile(r"^dryrun-([0-9a-f]{12})$")
OVERRIDE_RE = re.compile(r"^// indirect; override ((?:GO-\d{4}-\d{4,}|CVE-\d{4}-\d{4,})(?:,(?:GO-\d{4}-\d{4,}|CVE-\d{4}-\d{4,}))*) #\d+$")
GO_FROM_RE = re.compile(r"^(\d+\.\d+\.\d+)-[a-z0-9.]+$")
BUSYBOX_FROM_RE = re.compile(r"^(\d+\.\d+\.\d+)-musl$")
PARSER_DIRECTIVE_RE = re.compile(r"^\s*#\s*(syntax|escape)\s*=", re.I | re.M)
ZERO_DIGEST_RE = re.compile(r"@sha256:0{64}$")
SECTION_RE = re.compile(r"^@@(LIST|GRAPH|GOVER)$")


# --------------------------------------------------------------------------- small helpers

def vtuple(version: str) -> tuple:
    """Orderable form of a Go or semver version. A release sorts above a pre-release of the same numbers."""
    m = re.match(r"^v?(\d+)\.(\d+)(?:\.(\d+))?(.*)$", version.strip())
    if not m:
        raise ReleaseError(f"not a version: {version!r}")
    rest = m.group(4)
    release = 1 if (not rest or rest.startswith("+")) else 0
    return (int(m.group(1)), int(m.group(2)), int(m.group(3) or 0), release, "" if release else rest)


def summary(lines: list[str]) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as fh:
        for line in lines:
            fh.write(ri.neutralize(line) + "\n")


def work_dir() -> Path:
    return ri.runner_temp() / "caddy"


def out_dir() -> Path:
    return work_dir() / "out"


def in_dir() -> Path:
    return ri.runner_temp() / "caddy-in"


def ids() -> list[str]:
    getuid = getattr(os, "getuid", None)
    getgid = getattr(os, "getgid", None)
    return ["--user", f"{getuid() if getuid else 0}:{getgid() if getgid else 0}"]


def go_env_args() -> list[str]:
    argv: list[str] = []
    for key, value in GO_ENV.items():
        argv += ["-e", f"{key}={value}"]
    return argv + ["-e", "HOME=/tmp", "-e", "GOCACHE=/tmp/gocache"]


# --------------------------------------------------------------------------- Dockerfile

def instructions(text: str) -> list[tuple[int, str, str]]:
    """(stage index, KEYWORD, arguments) per instruction; continuation lines joined, comments dropped."""
    out: list[tuple[int, str, str]] = []
    stage, buf = -1, ""
    for raw in text.splitlines():
        line = raw.rstrip()
        if line.lstrip().startswith("#") or (not buf and not line.strip()):
            continue
        if line.endswith("\\"):
            buf += line[:-1] + " "
            continue
        full = (buf + line).strip()
        buf = ""
        keyword, _, rest = full.partition(" ")
        keyword = keyword.upper()
        if keyword == "FROM":
            stage += 1
        out.append((stage, keyword, rest.strip()))
    return out


def env_pairs(rest: str) -> dict[str, str]:
    pairs: dict[str, str] = {}
    for token in shlex.split(rest):
        key, sep, value = token.partition("=")
        if not sep:
            raise ReleaseError(f"Dockerfile: ENV token {token!r} is not KEY=value")
        pairs[key] = value
    return pairs


def parse_dockerfile() -> dict:
    """The three pins plus every structural rule of ADR-0021 items 2 and 4 and G3 M-1 (a, b, c, e)."""
    text = DOCKERFILE.read_text(encoding="utf-8")
    problems: list[str] = []
    if PARSER_DIRECTIVE_RE.search(text):
        problems.append("Dockerfile has a parser directive (syntax or escape): BuildKit's built-in frontend must be used (M-1 c)")
    ins = instructions(text)
    froms = [(s, a) for s, k, a in ins if k == "FROM"]
    refs: dict[str, str] = {}
    for _, args in froms:
        m = re.match(r"^(\S+)\s+AS\s+(\S+)$", args)
        if not m:
            problems.append(f"Dockerfile: FROM line {args!r} must be '<tag@digest> AS <stage>'")
            continue
        ref, alias = m.groups()
        if not ri.REF_RE.match(ref):
            problems.append(f"Dockerfile: {alias} is not a <registry>/<image>:<tag>@sha256:<64 hex> reference")
        elif ZERO_DIGEST_RE.search(ref):
            problems.append(f"Dockerfile: {alias} has an unresolved all-zero digest (resolve it two ways first)")
        refs[alias] = ref
    if tuple(refs) != STAGES or len(froms) != 3:
        problems.append(f"Dockerfile: expected exactly three FROM stages {list(STAGES)}, found {list(refs)}")
        raise ReleaseError("; ".join(problems))

    versions: dict[str, str] = {}
    g = GO_FROM_RE.match(refs["builder"].split("@")[0].rsplit(":", 1)[-1])
    if not refs["builder"].startswith("docker.io/library/golang:") or not g:
        problems.append("Dockerfile: builder must be docker.io/library/golang:<x.y.z>-<variant>")
    else:
        versions["go"] = g.group(1)
    b = BUSYBOX_FROM_RE.match(refs["busybox"].split("@")[0].rsplit(":", 1)[-1])
    if not refs["busybox"].startswith("docker.io/library/busybox:") or not b:
        problems.append("Dockerfile: busybox must be docker.io/library/busybox:<x.y.z>-musl")
    else:
        versions["busybox"] = b.group(1)
    runtime_name = refs["runtime"].split("@")[0]
    if runtime_name != "gcr.io/distroless/static-debian13:latest":
        problems.append("Dockerfile: runtime must be the plain root gcr.io/distroless/static-debian13:latest (not debug, nonroot or another base; M-1 e)")

    by_stage: dict[int, list[tuple[str, str]]] = {0: [], 1: [], 2: []}
    for s, k, a in ins:
        if s not in by_stage:
            raise ReleaseError(f"Dockerfile: instruction {k} before the first FROM is not allowed")
        by_stage[s].append((k, a))
    if any(k == "ADD" for _, k, _ in ins):
        problems.append("Dockerfile: ADD is not allowed (M-1 b)")

    builder = by_stage[0]
    env: dict[str, str] = {}
    for k, a in builder:
        if k == "ENV":
            env.update(env_pairs(a))
    if env != GO_ENV:
        problems.append(f"Dockerfile: builder ENV differs from the fixed Go environment (ADR-0021 item 2); got {sorted(env)}")
    copies = [a for k, a in builder if k == "COPY"]
    if copies != ["go.mod go.sum main.go ./"]:
        problems.append("Dockerfile: the builder must copy exactly 'go.mod go.sum main.go ./' (M-1 b)")
    runs = [a for k, a in builder if k == "RUN"]
    compile_runs = [a for a in runs if "go build" in a]
    if len(compile_runs) != 1:
        problems.append("Dockerfile: expected exactly one RUN with go build")
    else:
        c = compile_runs[0]
        for needle in ("--network=none", RELEASE_FLAGS, LDFLAGS, "GOLANG_VERSION", "go version -m", "go list -m"):
            if needle not in c:
                problems.append(f"Dockerfile: the compile step lacks {needle!r}")
    if not any(a == "go mod download && go mod verify" for a in runs) or not any(a == "go mod tidy -diff" for a in runs):
        problems.append("Dockerfile: the module steps 'go mod download && go mod verify' and 'go mod tidy -diff' are required")

    busybox = by_stage[1]
    applets: set[str] | None = None
    for k, a in busybox:
        m = re.search(r"for applet in (.*?); do", a) if k == "RUN" else None
        if m:
            applets = set(shlex.split(m.group(1)))
    if applets != set(APPLETS):
        problems.append(f"Dockerfile: the BusyBox link set {sorted(applets or [])} differs from the image contract {sorted(APPLETS)}")

    runtime = by_stage[2]
    kinds = [k for k, _ in runtime]
    for banned in ("RUN", "ENTRYPOINT", "VOLUME", "USER", "EXPOSE", "HEALTHCHECK", "ONBUILD", "SHELL"):
        if banned in kinds:
            problems.append(f"Dockerfile: the final stage must not contain {banned} (COPY only, root, no entrypoint, no volume)")
    if [a for k, a in runtime if k == "COPY"] != ["--from=builder /out/caddy /usr/bin/caddy", "--from=busybox /out/bin/ /bin/"]:
        problems.append("Dockerfile: the final stage copies exactly /usr/bin/caddy and the BusyBox /bin tree")
    renv: dict[str, str] = {}
    for k, a in runtime:
        if k == "ENV":
            renv.update(env_pairs(a))
    if renv != RUNTIME_ENV:
        problems.append(f"Dockerfile: the final ENV must be exactly {RUNTIME_ENV}")
    if [a for k, a in runtime if k == "WORKDIR"] != ["/srv"]:
        problems.append("Dockerfile: WORKDIR must be /srv")
    cmds = [a for k, a in runtime if k == "CMD"]
    try:
        cmd_ok = len(cmds) == 1 and json.loads(cmds[0]) == RUNTIME_CMD
    except ValueError:
        cmd_ok = False
    if not cmd_ok:
        problems.append(f"Dockerfile: CMD must be {RUNTIME_CMD}")
    args = [a for k, a in runtime if k == "ARG"]
    if sorted(args) != ["BASE_DIGEST", "BASE_NAME", "BUILD_REVISION", "CADDY_VERSION"]:
        problems.append("Dockerfile: the final stage ARGs must be CADDY_VERSION, BUILD_REVISION, BASE_NAME, BASE_DIGEST")
    if problems:
        raise ReleaseError("; ".join(problems))
    return {
        "refs": refs, "go_version": versions["go"], "busybox_version": versions["busybox"],
        "base_name": runtime_name, "base_digest": "sha256:" + refs["runtime"].rsplit("@sha256:", 1)[1],
    }


# --------------------------------------------------------------------------- go.mod, main.go, directory

def parse_gomod() -> dict:
    """Directives, requirements and overrides of go.mod. `go.sum` content is never parsed here."""
    text = GO_MOD.read_text(encoding="utf-8")
    problems: list[str] = []
    module = go_line = toolchain = None
    requires: list[tuple[str, str, str]] = []  # (path, version, comment)
    block = None
    for n, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("//"):
            continue
        if block:
            if line == ")":
                block = None
                continue
            kw, rest = block, line
        else:
            kw, _, rest = line.partition(" ")
            rest = rest.strip()
            if rest == "(":
                block = kw
                if kw not in ("require",):
                    problems.append(f"go.mod:{n}: a '{kw}' block is not allowed")
                continue
        if kw == "module":
            module = rest
        elif kw == "go":
            go_line = rest
        elif kw == "toolchain":
            toolchain = rest
        elif kw == "require":
            body, _, comment = rest.partition("//")
            parts = body.split()
            if len(parts) != 2:
                problems.append(f"go.mod:{n}: unreadable requirement")
                continue
            requires.append((parts[0], parts[1], ("// " + comment.strip()) if comment else ""))
        else:
            problems.append(f"go.mod:{n}: directive '{kw}' is not allowed (replace, exclude, retract, tool and godebug are banned)")
    if not module or not go_line:
        problems.append("go.mod: module and go lines are required")
    direct = [(p, v) for p, v, c in requires if not c.startswith("// indirect")]
    if len(direct) != 1 or direct[0][0] != CADDY_MODULE:
        problems.append(f"go.mod: exactly one direct requirement, {CADDY_MODULE}; found {[p for p, _ in direct]}")
    caddy_version = direct[0][1] if direct else ""
    if not re.fullmatch(r"v\d+\.\d+\.\d+", caddy_version):
        problems.append("go.mod: the Caddy requirement must be a release tag vX.Y.Z")
    overrides: dict[str, str] = {}
    for path, version, comment in requires:
        if comment.startswith("// indirect;"):
            if not OVERRIDE_RE.match(comment):
                problems.append(f"go.mod: override of {path} must read '// indirect; override <GO or CVE id>[,<id>] #<issue>'")
            else:
                overrides[path] = version
    if problems:
        raise ReleaseError("; ".join(problems))
    return {"go": go_line, "toolchain": toolchain, "caddy": caddy_version, "caddy_plain": caddy_version[1:], "overrides": overrides}


def check_main_go() -> list[str]:
    problems: list[str] = []
    text = MAIN_GO.read_text(encoding="utf-8")
    if any(line.strip().startswith("//go:") for line in text.splitlines()):
        problems.append("main.go: no //go: directive is allowed (M-1 d)")
    if set(re.findall(r'"([^"]+)"', text)) != MAIN_IMPORTS:
        problems.append(f"main.go: must import exactly {sorted(MAIN_IMPORTS)}")
    return problems


def check_directory() -> list[str]:
    problems: list[str] = []
    for entry in sorted(INPUT_DIR.iterdir()):
        if entry.is_symlink() or entry.is_dir() or entry.name not in INPUT_FILES:
            problems.append(f"deploy/caddy/image/{entry.name}: not an allowed input (allowed files: {list(INPUT_FILES)}; no vendor, go.work or other .go file; M-1 b)")
    if not GO_SUM.is_file() or GO_SUM.stat().st_size == 0:
        problems.append("deploy/caddy/image/go.sum is missing or empty: generate it in the pinned builder container (docs/runbooks/caddy-image.md)")
    if not MODULES_TXT.is_file() or MODULES_TXT.stat().st_size == 0:
        problems.append("deploy/caddy/image/modules.txt is missing or empty: generate it from the upstream image (docs/runbooks/caddy-image.md)")
    return problems


# --------------------------------------------------------------------------- context

def context() -> dict:
    ri.check_repository()
    event = ri.env_required("RELEASE_EVENT", r"^(push|workflow_dispatch|pull_request)$")
    sha = ri.env_required("RELEASE_SHA", ri.SHA40_RE)
    gomod = parse_gomod()
    if event == "push":
        tag = f"{gomod['caddy_plain']}-{sha}"
    elif event == "workflow_dispatch":
        tag = f"dryrun-{sha[:12]}"
    else:
        tag = "pr-build"
    return {"event": event, "sha": sha, "tag": tag, "caddy_version": gomod["caddy_plain"], "gomod": gomod}


def publish_context() -> dict:
    ri.check_repository()
    event = ri.env_required("RELEASE_EVENT", r"^(push|workflow_dispatch)$")
    sha = ri.env_required("RELEASE_SHA", ri.SHA40_RE)
    tag = ri.env_required("RELEASE_TAG", r"^(\d+\.\d+\.\d+-[0-9a-f]{40}|dryrun-[0-9a-f]{12})$")
    if event == "push":
        m = PUSH_TAG_RE.match(tag)
        if not m or m.group(2) != sha:
            raise ReleaseError("a release tag must be <x.y.z>-<RELEASE_SHA>")
    elif tag != f"dryrun-{sha[:12]}":
        raise ReleaseError("a dry-run tag must be dryrun-<first 12 hex of RELEASE_SHA>")
    return {"event": event, "sha": sha, "tag": tag}


def derive_flags(tag: str, trigger: str, sha: str | None = None) -> list[str]:
    """The one code path for the verification flags (G3 M-2 d): the verify job and `verify-command` both use it.
    A push flag set takes the commit from the tag, so a tag rewritten to another genuine digest does not verify."""
    if trigger == "push":
        m = PUSH_TAG_RE.match(tag)
        if not m:
            raise ReleaseError("a pinned tag must be <x.y.z>-<40 hex build commit>; a dry-run tag never verifies as a release")
        if sha and sha != m.group(2):
            raise ReleaseError("the commit differs from the 40 hex in the tag")
        return ri.verify_flags(m.group(2), "push", IDENTITY)
    if trigger == "workflow_dispatch":
        m = DRY_TAG_RE.match(tag)
        if not m or not sha or sha[:12] != m.group(1):
            raise ReleaseError("a dry-run tag must be dryrun-<first 12 hex of the run commit>")
        return ri.verify_flags(sha, "workflow_dispatch", IDENTITY)
    raise ReleaseError("trigger must be push or workflow_dispatch")


# --------------------------------------------------------------------------- inputs

def cmd_pins(_args) -> dict:
    pins = parse_dockerfile()
    for alias, ref in pins["refs"].items():
        log(f"Dockerfile: {alias} = {ref}")
    return {}


def cmd_inputs(_args) -> dict:
    pins = parse_dockerfile()
    gomod = parse_gomod()
    problems = check_main_go() + check_directory()
    log(f"inputs: builder go {pins['go_version']}, busybox {pins['busybox_version']}, base {pins['base_name']}")
    log(f"inputs: caddy {gomod['caddy']}, go line {gomod['go']}, overrides {sorted(gomod['overrides'].items())}")
    if vtuple(gomod["go"]) > vtuple(pins["go_version"]):
        problems.append(f"go.mod: the go line {gomod['go']} is above the builder's Go {pins['go_version']}")
    if gomod["toolchain"] is not None and vtuple(gomod["toolchain"].removeprefix("go")) > vtuple(pins["go_version"]):
        problems.append(f"go.mod: the toolchain line {gomod['toolchain']} exceeds the builder's Go {pins['go_version']} (M-1 d)")
    if problems:
        for p in problems:
            log(f"PROBLEM: {p}")
        raise ReleaseError(f"{len(problems)} input check(s) failed")
    log("inputs: all static input checks passed")
    return {}


# --------------------------------------------------------------------------- buildlist (ADR-0021 item 3, S-1)

BUILDLIST_SCRIPT = (
    "set -eu\n"
    "{tidy}\n"
    "echo @@LIST\n"
    "go list -m -f '{{if not .Main}}{{.Path}} {{.Version}}{{end}}' all\n"
    "echo @@GRAPH\n"
    "go mod graph\n"
    "echo @@GOVER\n"
    "go list -m -f '{{.GoVersion}}' " + CADDY_MODULE + "\n"
)


def builder_run(builder: str, directory: Path, tidy: bool) -> dict:
    """Runs only `go mod tidy` (scratch module only), `go list -m` and `go mod graph` in the pinned builder, on a
    mount of one directory. Network is used for module download only. Never run, install, generate or test."""
    script = BUILDLIST_SCRIPT.replace("{tidy}", "go mod tidy" if tidy else "true")
    mount = f"{directory}:/src" + ("" if tidy else ":ro")
    argv = ["docker", "run", "--rm", *ids(), *go_env_args(), "-v", mount, "-w", "/src", builder, "sh", "-c", script]
    proc = ri.run(argv, timeout=1800)
    if proc.returncode != 0:
        ri.LOG.extend(tail(proc.stdout, 10) + tail(proc.stderr, 30))
        raise ReleaseError(f"builder run in {directory.name} failed (exit {proc.returncode})")
    sections: dict[str, list[str]] = {}
    current = None
    for line in proc.stdout.splitlines():
        m = SECTION_RE.match(line.strip())
        if m:
            current = m.group(1)
            sections[current] = []
        elif current and line.strip():
            sections[current].append(line.strip())
    if set(sections) != {"LIST", "GRAPH", "GOVER"} or len(sections["GOVER"]) != 1:
        raise ReleaseError(f"builder run in {directory.name}: unexpected output shape")
    modules: dict[str, str] = {}
    for line in sections["LIST"]:
        parts = line.split()
        if len(parts) != 2:
            raise ReleaseError(f"builder run in {directory.name}: unreadable module line")
        modules[parts[0]] = parts[1]
    edges = []
    for line in sections["GRAPH"]:
        parts = line.split()
        if len(parts) == 2:
            edges.append((parts[0], parts[1]))
    return {"modules": modules, "edges": edges, "go": sections["GOVER"][0]}


def compare_buildlists(ours: dict, upstream: dict, overrides: dict[str, str]) -> tuple[list[str], list[str]]:
    """Every module whose version differs from the upstream list is an annotated override or is raised by one
    (the MVS closure over our graph). A stale override (not above upstream) fails. Returns (problems, table rows)."""
    problems: list[str] = []
    rows: list[str] = ["| module | upstream | ours | status |", "| --- | --- | --- | --- |"]
    explained = {f"{p}@{ours['modules'][p]}" for p in overrides if p in ours["modules"]}
    for p, v in overrides.items():
        if ours["modules"].get(p) != v:
            problems.append(f"override {p} {v} is not the selected version ({ours['modules'].get(p)})")
    grew = True
    while grew:
        grew = False
        for src, dst in ours["edges"]:
            dst_path = dst.rsplit("@", 1)[0]
            if src in explained and dst not in explained and ours["modules"].get(dst_path) == dst.rsplit("@", 1)[-1]:
                explained.add(dst)
                grew = True
    for path in sorted(set(ours["modules"]) | set(upstream["modules"])):
        mine, theirs = ours["modules"].get(path), upstream["modules"].get(path)
        if mine == theirs:
            continue
        if mine is None:
            problems.append(f"{path} is in the upstream build list ({theirs}) but not in ours")
            rows.append(f"| {path} | {theirs} | - | missing |")
        elif f"{path}@{mine}" in explained:
            status = "override" if path in overrides else "raised by an override"
            rows.append(f"| {path} | {theirs or '-'} | {mine} | {status} |")
        else:
            problems.append(f"{path} differs from upstream ({theirs or 'absent'} vs {mine}) and is neither an annotated override nor raised by one (S-1)")
            rows.append(f"| {path} | {theirs or '-'} | {mine} | UNEXPLAINED |")
    for path, version in overrides.items():
        up = upstream["modules"].get(path)
        if up is not None and ours["modules"].get(path) is not None and vtuple(ours["modules"][path]) <= vtuple(up):
            problems.append(f"override {path} {version} is stale: upstream {up} is not below it; remove the override")
    return problems, rows


def cmd_buildlist(_args) -> dict:
    ctx = context()
    pins = parse_dockerfile()
    gomod = ctx["gomod"]
    base = work_dir() / "buildlist"
    if base.exists():
        shutil.rmtree(base)
    scratch = base / "upstream"
    scratch.mkdir(parents=True)
    scratch.chmod(0o777)
    (scratch / "go.mod").write_text(
        f"module scratch.invalid/upstream\n\ngo {gomod['go']}\n\nrequire {CADDY_MODULE} {gomod['caddy']}\n", encoding="utf-8")
    shutil.copyfile(MAIN_GO, scratch / "main.go")
    builder = pins["refs"]["builder"]
    ours = builder_run(builder, INPUT_DIR, tidy=False)
    upstream = builder_run(builder, scratch, tidy=True)
    problems, rows = compare_buildlists(ours, upstream, gomod["overrides"])
    if vtuple(gomod["go"]) < vtuple(ours["go"]):
        problems.append(f"go.mod go line {gomod['go']} is below Caddy's own go line {ours['go']} at {gomod['caddy']}")
    log(f"buildlist: caddy {gomod['caddy']} go line {ours['go']}; ours {gomod['go']}; {len(ours['modules'])} modules, {len(upstream['modules'])} upstream")
    log(f"buildlist: overrides {sorted(gomod['overrides'].items())}")
    summary(["### Caddy build list against upstream", ""] + rows + [""])
    if problems:
        for p in problems:
            log(f"PROBLEM: {p}")
        raise ReleaseError(f"{len(problems)} build list check(s) failed")
    log("buildlist: every difference from the upstream build list is an annotated override or raised by one")
    return {}


# --------------------------------------------------------------------------- build

def cmd_build(_args) -> dict:
    ctx = context()
    pins = parse_dockerfile()
    out = out_dir()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    local = f"{IMAGE_NAME}:{ctx['tag']}"
    argv = [
        "docker", "build", "--provenance=false", "--platform", "linux/amd64",
        "--build-arg", f"CADDY_VERSION={ctx['caddy_version']}",
        "--build-arg", f"BUILD_REVISION={ctx['sha']}",
        "--build-arg", f"BASE_NAME={pins['base_name']}",
        "--build-arg", f"BASE_DIGEST={pins['base_digest']}",
        "-t", local, str(INPUT_DIR),
    ]
    log(f"build: {local} from {INPUT_DIR.relative_to(ROOT).as_posix()} (commit {ctx['sha']})")
    os.environ["DOCKER_BUILDKIT"] = "1"  # the built-in BuildKit frontend (M-1 c); inherited by the docker child
    proc = run_checked(argv, "docker build", timeout=3000)
    ri.LOG.extend(tail(proc.stdout, 8) + tail(proc.stderr, 25))
    archive = out / "caddy.tar"
    run_checked(["docker", "save", "-o", str(archive), local], "docker save")
    if not archive.is_file() or archive.is_symlink():
        raise ReleaseError("no archive was written")
    log(f"build: {archive.name} {archive.stat().st_size} bytes")
    return {}


# --------------------------------------------------------------------------- inspect

def check_config(cfg: dict, ctx: dict, pins: dict) -> list[str]:
    problems: list[str] = []
    inner = cfg.get("config") if isinstance(cfg.get("config"), dict) else {}
    if cfg.get("os") != "linux" or cfg.get("architecture") != "amd64":
        problems.append(f"platform is {cfg.get('os')}/{cfg.get('architecture')}, expected linux/amd64")
    if str(inner.get("User") or "") not in ("", "0", "root", "0:0", "root:root"):
        problems.append(f"User is {inner.get('User')!r}, expected root (ADR-0021 M7)")
    env = {}
    for item in inner.get("Env") or []:
        key, _, value = str(item).partition("=")
        env[key] = value
    extra = sorted(set(env) - set(ENV_ALLOW))
    if extra:
        problems.append(f"Env keys outside the allow-list {list(ENV_ALLOW)}: {extra}")
    for key, value in {**RUNTIME_ENV, **BASE_ENV}.items():
        if env.get(key) != value:
            problems.append(f"Env {key} is {env.get(key)!r}, expected {value!r}")
    if inner.get("Entrypoint"):
        problems.append("Entrypoint must be empty")
    if inner.get("Cmd") != RUNTIME_CMD:
        problems.append(f"Cmd is {inner.get('Cmd')}, expected {RUNTIME_CMD}")
    if inner.get("WorkingDir") != "/srv":
        problems.append(f"WorkingDir is {inner.get('WorkingDir')!r}, expected '/srv'")
    if inner.get("Volumes"):
        problems.append("the image declares a VOLUME")
    labels = inner.get("Labels") or {}
    expected = {
        "org.opencontainers.image.source": SOURCE_URL,
        "org.opencontainers.image.revision": ctx["sha"],
        "org.opencontainers.image.version": ctx["caddy_version"],
        "org.opencontainers.image.base.name": pins["base_name"],
        "org.opencontainers.image.base.digest": pins["base_digest"],
    }
    for key, value in expected.items():
        if labels.get(key) != value:
            problems.append(f"label {key} is {labels.get(key)!r}, expected {value!r}")
    return problems


def canonical(name: str) -> str:
    """Debian's merged /usr: bin, sbin are the same directories as usr/bin, usr/sbin."""
    n = ri.normalize(name).rstrip("/")
    for old, new in (("bin", "usr/bin"), ("sbin", "usr/sbin")):
        if n == old or n.startswith(old + "/"):
            return new + n[len(old):]
    return n


def merged_view(layers: list[tuple[str, bytes]]) -> dict[str, dict]:
    """path -> member facts after applying layers in order and their whiteouts (final-image view)."""
    view: dict[str, dict] = {}
    for _, data in layers:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as tar:
            for member in tar:
                raw = ri.normalize(member.name).rstrip("/")
                if raw in ROOT_LINKS:
                    # The merged-/usr root entry of the base. canonical() maps these names, so record what the
                    # entry really is and let check_files require the exact symlink (G6 F-1).
                    kind = "sym" if member.issym() else "dir" if member.isdir() else "other"
                    view["@root/" + raw] = {"kind": kind, "mode": member.mode, "link": member.linkname, "pax": {}}
                    continue
                path = canonical(member.name)
                base = path.rsplit("/", 1)[-1]
                if base == ".wh..wh..opq":
                    prefix = path.rsplit("/", 1)[0] + "/"
                    for key in [k for k in view if k.startswith(prefix)]:
                        del view[key]
                    continue
                if base.startswith(".wh."):
                    target = path.rsplit("/", 1)[0] + "/" + base[4:]
                    for key in [k for k in view if k == target or k.startswith(target + "/")]:
                        del view[key]
                    continue
                kind = "dir" if member.isdir() else "sym" if member.issym() else "file" if (member.isreg() or member.islnk()) else "other"
                view[path] = {"kind": kind, "mode": member.mode, "link": member.linkname, "pax": dict(member.pax_headers or {})}
    return view


def check_files(view: dict[str, dict]) -> list[str]:
    """G3 M-1 e and ADR-0021 item 4: the executables, the applet links, setuid and file capabilities."""
    problems: list[str] = []
    executables = sorted(p for p, f in view.items() if f["kind"] == "file" and f["mode"] & 0o111)
    expected_exec = sorted(["usr/bin/busybox", "usr/bin/caddy", *BASE_EXEC_FILES])
    if executables != expected_exec:
        problems.append(f"executable regular files are {executables}, expected only /usr/bin/caddy, /bin/busybox and the four distroless base files {list(BASE_EXEC_FILES)}")
    for path, f in view.items():
        if f["kind"] in ("file", "dir") and f["mode"] & 0o6000:
            problems.append(f"{path}: setuid or setgid bit")
        if any("security.capability" in key for key in f["pax"]):
            problems.append(f"{path}: file capability")
    for name, target in ROOT_LINKS.items():
        f = view.get("@root/" + name)
        if f is not None and not (f["kind"] == "sym" and f["link"] == target):
            problems.append(f"/{name} must be a symlink to {target}, found {f['kind']} {f['link']!r}: PATH could be re-pointed (G6 F-1)")
    for directory in PATH_DIRS:
        f = view.get(directory)
        if f is not None and f["kind"] != "dir":
            problems.append(f"{directory}: a PATH directory must be a real directory, found {f['kind']} (G6 F-1)")
    links: dict[str, str] = {}
    for path, f in view.items():
        parent, _, name = path.rpartition("/")
        if parent in PATH_DIRS and f["kind"] != "dir" and name not in ("caddy", "busybox"):
            if f["kind"] == "sym" and f["link"] in ("busybox", "./busybox", "/bin/busybox", "/usr/bin/busybox"):
                links[name] = f["link"]
            else:
                problems.append(f"{path}: an entry in a PATH directory that is neither an applet link nor one of the two binaries")
    if sorted(links) != sorted(APPLETS):
        problems.append(f"applet links are {sorted(links)}, expected exactly {sorted(APPLETS)}")
    return problems


def layer_bytes_for(layers: list[tuple[str, bytes]], wanted: str) -> bytes:
    found = None
    for _, data in layers:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as tar:
            for member in tar:
                if canonical(member.name) == wanted and member.isreg():
                    handle = tar.extractfile(member)
                    if handle is None:
                        raise ReleaseError(f"{wanted} cannot be read")
                    found = handle.read()
    if found is None:
        raise ReleaseError(f"{wanted} is not in any layer")
    return found


def check_sbom(path: Path, ctx: dict, pins: dict) -> list[str]:
    """Strict D-2 must not pass vacuously (T188-07, T188-16): the SBOM must identify the Caddy binary's module,
    the Go standard library and BusyBox at the pinned version."""
    problems: list[str] = []
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        return [f"{path.name}: not valid JSON ({exc})"]
    comps = [c for c in (doc.get("components") or []) if isinstance(c, dict)]

    def has(test) -> bool:
        return any(test(c) for c in comps)

    if not has(lambda c: str(c.get("purl", "")).startswith(f"pkg:golang/{CADDY_MODULE}@v{ctx['caddy_version']}")):
        problems.append(f"SBOM has no Caddy module component at v{ctx['caddy_version']}: the binary was not catalogued")
    if not has(lambda c: str(c.get("purl", "")).startswith("pkg:golang/stdlib@")):
        problems.append("SBOM has no Go stdlib component: stdlib findings would be invisible")
    if not has(lambda c: c.get("name") == "busybox" and c.get("version") == pins["busybox_version"]):
        problems.append(f"SBOM does not identify BusyBox {pins['busybox_version']}: its CVEs would be invisible (S-3 a)")
    return problems


def cmd_inspect(_args) -> dict:
    ctx = context()
    pins = parse_dockerfile()
    tools = ri.parse_pins(ri.TOOL_PINS, ri.TOOL_ALIASES)
    import image_scan as ims  # the ADR-0015 grype policy; only this subcommand needs it

    work = work_dir()
    out = out_dir()
    local = f"{IMAGE_NAME}:{ctx['tag']}"
    cfg_dir = ri.make_cfg_dir(work)
    ri.canary(tools["gitleaks"], work, cfg_dir)  # fatal before any real scan

    scanner = ims.parse_scanner_ref(ri.SCANNER_PINS.read_text(encoding="utf-8"))
    today = datetime.now(timezone.utc).date()
    db_dir = work / "grype-db"
    db_dir.mkdir(parents=True, exist_ok=True)
    db_dir.chmod(0o777)
    run_checked(ims.db_update_argv(scanner, str(db_dir)), "grype database update")
    sbom_dir = work / "sbom"
    if sbom_dir.exists():
        shutil.rmtree(sbom_dir)
    sbom_dir.mkdir(parents=True)
    sbom_dir.chmod(0o777)

    archive = out / "caddy.tar"
    info = ri.read_archive(archive)
    problems: list[str] = []
    if info["repo_tags"] != [local]:
        problems.append(f"archive RepoTags are {info['repo_tags']}, expected [{local!r}]")
    problems += check_config(info["config"], ctx, pins)

    cfg_scan = work / "cfgscan"
    if cfg_scan.exists():
        shutil.rmtree(cfg_scan)
    cfg_scan.mkdir(parents=True)
    (cfg_scan / "config.json").write_bytes(info["config_bytes"])
    res = ri.scan_dir_with_gitleaks(tools["gitleaks"], cfg_scan, cfg_dir)
    if res["status"] != "clean":
        problems.append(f"gitleaks on the image config: {res['status']} {res['findings']}")
    for index, (_, data) in enumerate(info["layers"]):
        dest = work / "layers" / f"{index:02d}"
        if dest.exists():
            shutil.rmtree(dest)
        names, skipped = ri.unpack_layer(data, dest)
        problems += ri.check_names("caddy", index, names)
        for s in skipped:
            log(f"layer {index}: skipped link {s}")
        res = ri.scan_dir_with_gitleaks(tools["gitleaks"], dest, cfg_dir)
        log(f"gitleaks caddy layer {index}: {res['status']} ({len(names)} members, {len(skipped)} links skipped)")
        if res["status"] != "clean":
            problems.append(f"gitleaks on layer {index}: {res['status']} {res['findings']}")
    problems += check_files(merged_view(info["layers"]))

    # Version binding (M-1 f) and the binary evidence (S-2). The binary is only read: `go version -m` does not run it.
    binary = layer_bytes_for(info["layers"], "usr/bin/caddy")
    binary_sha = hashlib.sha256(binary).hexdigest()
    log(f"binary: /usr/bin/caddy sha256 {binary_sha}")
    bin_dir = work / "bin"
    if bin_dir.exists():
        shutil.rmtree(bin_dir)
    bin_dir.mkdir(parents=True)
    (bin_dir / "caddy").write_bytes(binary)
    bin_dir.chmod(0o755)
    (bin_dir / "caddy").chmod(0o644)
    vm = run_checked(["docker", "run", "--rm", "--network", "none", *ids(), *go_env_args(), "-v", f"{bin_dir}:/in:ro",
                      pins["refs"]["builder"], "go", "version", "-m", "/in/caddy"], "go version -m")
    lines = vm.stdout.splitlines()
    if not lines or lines[0].strip() != f"/in/caddy: go{pins['go_version']}":
        problems.append(f"the binary's Go version is {lines[0].strip() if lines else None!r}, the builder FROM tag names go{pins['go_version']}")
    deps = {}
    for line in lines[1:]:
        parts = line.split("\t")
        if len(parts) >= 4 and parts[1] == "dep":
            deps[parts[2]] = parts[3]
    if deps.get(CADDY_MODULE) != ctx["gomod"]["caddy"]:
        problems.append(f"the binary's Caddy module is {deps.get(CADDY_MODULE)!r}, go.mod requires {ctx['gomod']['caddy']}")
    for path, version in ctx["gomod"]["overrides"].items():
        if deps.get(path) != version:
            problems.append(f"the binary carries {path} {deps.get(path)!r}, the override says {version}")
    log(f"binary: go{pins['go_version']}, {len(deps)} dependencies; " + ", ".join(f"{p} {v}" for p, v in sorted(deps.items())))

    # Run the built image only with no network, a read-only root and no capabilities (M-1 g).
    sandbox = ["docker", "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", local]
    listed = run_checked(sandbox + ["caddy", "list-modules"], "caddy list-modules").stdout.strip()
    expected_modules = MODULES_TXT.read_text(encoding="utf-8").strip()
    if listed != expected_modules:
        problems.append("caddy list-modules differs from modules.txt (a Caddy bump that changes the module set updates the file in the same PR)")
    compiled = set(run_checked(sandbox + ["busybox", "--list"], "busybox --list").stdout.split())
    missing = sorted(set(APPLETS) - compiled)
    if missing:
        problems.append(f"BusyBox lacks the applets {missing}")
    log(f"caddy list-modules: {len(listed.splitlines())} lines, equal to modules.txt: {listed == expected_modules}")

    # Strict D-2: no exceptions, no ref, no tag state (ri._grype calls evaluate() without them). The archive is
    # named caddy.tar, so the alias is `caddy`.
    problems += ri._grype(scanner, db_dir, out, "caddy", ims, [], today)
    sbom_name = f"decisya-caddy-{ctx['tag']}.cdx.json"
    ri._syft(tools["syft"], out, "docker-archive:/in/caddy.tar", sbom_dir, sbom_name, True)
    problems += check_sbom(sbom_dir / sbom_name, ctx, pins)
    shutil.copyfile(sbom_dir / sbom_name, out / sbom_name)

    summary([f"### Caddy image {ctx['tag']}", "", f"- binary sha256: `{binary_sha}`", f"- config digest: `{info['config_digest']}`", ""])
    if problems:
        for p in problems:
            log(f"PROBLEM: {p}")
        raise ReleaseError(f"{len(problems)} pre-push check(s) failed; nothing was pushed")
    log("inspect: all pre-push checks passed")
    return {
        "tag": ctx["tag"], "caddy_version": ctx["caddy_version"], "caddy_config": info["config_digest"],
        "caddy_sha256": ri.sha256_file(archive), "binary_sha256": binary_sha,
    }


# --------------------------------------------------------------------------- push, sign

def cmd_push(_args) -> dict:
    ctx = publish_context()
    archive = in_dir() / "caddy.tar"
    if not archive.is_file() or archive.is_symlink():
        raise ReleaseError("the downloaded archive is missing")
    if ri.sha256_file(archive) != ri.env_required("CADDY_SHA256", ri.HEX64_RE):
        raise ReleaseError("archive SHA-256 differs from the one the build job scanned")
    info = ri.read_archive(archive)
    if info["config_digest"] != ri.env_required("CADDY_CONFIG", ri.DIGEST_RE):
        raise ReleaseError("archive config digest differs from the one the build job scanned")
    local = f"{IMAGE_NAME}:{ctx['tag']}"
    if info["repo_tags"] != [local]:
        raise ReleaseError(f"archive RepoTags are not [{local!r}]")
    remote = f"{REMOTE}:{ctx['tag']}"
    run_checked(["docker", "load", "-i", str(archive)], "docker load")
    run_checked(["docker", "tag", local, remote], "docker tag")
    pushed = run_checked(["docker", "push", remote], "docker push")
    m = re.search(r"digest:\s+(sha256:[0-9a-f]{64})\s+size:", pushed.stdout)
    if not m:
        raise ReleaseError("docker push printed no digest")
    digest = m.group(1)
    manifest = run_checked(["docker", "manifest", "inspect", f"{REMOTE}@{digest}"], "docker manifest inspect")
    try:
        pushed_config = (json.loads(manifest.stdout).get("config") or {}).get("digest")
    except (ValueError, AttributeError):
        pushed_config = None
    if pushed_config != info["config_digest"]:
        raise ReleaseError("the pushed config digest differs from the scanned archive's (or the manifest has an unexpected shape)")
    log(f"push: {remote} -> {digest}, config digest equals the scanned archive's")
    return {"digest": digest}


def cmd_sign(_args) -> dict:
    ctx = publish_context()
    tool = ri.parse_pins(ri.TOOL_PINS, ri.TOOL_ALIASES)["cosign"]
    ref = f"{REMOTE}@{ri.env_required('CADDY_DIGEST', ri.DIGEST_RE)}"
    run_checked(ri.cosign_argv(tool, [], True) + ["sign", "--yes", ref], "cosign sign")
    log(f"signed {ref}")
    sbom = f"decisya-caddy-{ctx['tag']}.cdx.json"
    if not (in_dir() / sbom).is_file():
        raise ReleaseError(f"{sbom} is missing from the downloaded artifact")
    run_checked(ri.cosign_argv(tool, [f"{in_dir()}:/in:ro"], True)
                + ["attest", "--yes", "--type", "cyclonedx", "--predicate", f"/in/{sbom}", ref], "cosign attest")
    log(f"attested {sbom} to {ref}")
    return {}


# --------------------------------------------------------------------------- verify

def cmd_verify(_args) -> dict:
    ctx = publish_context()
    tool = ri.parse_pins(ri.TOOL_PINS, ri.TOOL_ALIASES)["cosign"]
    pins = parse_dockerfile()
    trigger = ctx["event"]
    flags = derive_flags(ctx["tag"], trigger, ctx["sha"])
    ref = f"{REMOTE}@{ri.env_required('CADDY_DIGEST', ri.DIGEST_RE)}"
    problems: list[str] = []
    for attestation in (False, True):
        proc = ri._cosign_verify(tool, ref, flags, attestation)
        kind = "verify-attestation" if attestation else "verify"
        if proc.returncode != 0:
            ri.LOG.extend(tail(proc.stdout, 10) + tail(proc.stderr, 20))
            problems.append(f"cosign {kind} failed")
        else:
            log(f"cosign {kind} OK ({ref})")

    def negative(label: str, target: str, neg_flags: list[str], expect: tuple[str, ...]) -> None:
        proc = ri._cosign_verify(tool, target, neg_flags)
        verdict = ri.classify_negative(proc.returncode, proc.stdout + "\n" + proc.stderr)
        if verdict not in expect:
            ri.LOG.extend(tail(proc.stdout, 10) + tail(proc.stderr, 20))
            problems.append(f"negative case {label}: expected a failure classified {list(expect)}, got {verdict!r}; proof not made")
        else:
            log(f"negative case {label}: failed as required ({verdict})")

    # 1. an image this identity never signed: the digest-pinned distroless base (unsigned or signed by another identity)
    negative("1 base image under this identity", ri.digest_only(pins["refs"]["runtime"]), flags, ("missing", "mismatch"))
    # 2. our own signed image under the release.yml identity
    negative("2 wrong identity (release.yml)", ref, ri.verify_flags(ctx["sha"], trigger, ri.IDENTITY), ("mismatch",))
    # 3. a dry-run image must not verify as a push image
    if trigger == "workflow_dispatch":
        negative("3 dry-run image as a push image", ref, ri.verify_flags(ctx["sha"], "push", IDENTITY), ("mismatch",))
    if problems:
        for p in problems:
            log(f"PROBLEM: {p}")
        raise ReleaseError(f"{len(problems)} verification check(s) failed")
    return {}


def cmd_verify_command(args) -> dict:
    m = re.match(r"^" + re.escape(REMOTE) + r":([^@:/]+)@(sha256:[0-9a-f]{64})$", args.reference)
    if not m:
        raise ReleaseError(f"--reference must be {REMOTE}:<tag>@sha256:<64 hex>")
    tag, digest = m.groups()
    argv = ["cosign", "verify"] + derive_flags(tag, "push") + [f"{REMOTE}@{digest}"]
    if args.runner == "docker":
        argv = ["docker", "run", "--rm", ri.parse_pins(ri.TOOL_PINS, ri.TOOL_ALIASES)["cosign"]] + argv[1:]
    print(" ".join(shlex.quote(a) for a in argv))
    return {}


COMMANDS = {
    "inputs": cmd_inputs, "buildlist": cmd_buildlist, "build": cmd_build, "inspect": cmd_inspect, "push": cmd_push,
    "sign": cmd_sign, "verify": cmd_verify, "verify-command": cmd_verify_command, "pins": cmd_pins,
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    for name in COMMANDS:
        p = sub.add_parser(name)
        if name == "verify-command":
            p.add_argument("--reference", required=True)
            p.add_argument("--runner", choices=("cosign", "docker"), default="cosign")
    args = parser.parse_args(argv)
    outputs: dict[str, str] = {}
    failure = None
    try:
        outputs = COMMANDS[args.command](args) or {}
    except ReleaseError as exc:
        failure = str(exc)
    except SystemExit:
        raise
    except Exception as exc:  # fail closed, and never echo tool text outside the window
        failure = f"unexpected {type(exc).__name__}: {exc}"
    if ri.LOG:
        ri.emit_protected(ri.LOG)
    if failure is not None:
        ri.emit_protected([f"caddy_image {args.command}: {failure}"])
        print(f"::error::caddy_image {args.command} failed; see the protected log above")
        return 1
    try:
        ri.write_outputs(outputs)
    except ReleaseError as exc:
        ri.emit_protected([str(exc)])
        print(f"::error::caddy_image {args.command} failed writing outputs")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
