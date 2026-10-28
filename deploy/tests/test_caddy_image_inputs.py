"""The build inputs of the self-built Caddy image keep ADR-0021 items 2 to 4 and G3 M-1 (issue #188).

Offline and stdlib only. The files in deploy/caddy/image/ are read as text. Each `*_problems` function returns one
problem per broken rule, each starting with a rule tag. The real files must give no problem, and every rule has a
red case: the real text (or a scratch copy of the folder) with one deliberate change, written red first.

These checks are independent of `.github/scripts/caddy_image.py inputs` (which runs the same rules in the build
job); test_caddy_image_script.py checks that the script refuses the same mistakes.

Rules and tags:
  syntax       no parser directive (`# syntax=`, `# escape=`): BuildKit's built-in frontend only (M-1 c)
  from         three FROM lines, each <name>:<tag>@sha256:<64 hex>, stages builder, busybox, runtime (G2 D1, M-1 e)
  env          the fixed Go environment: GOWORK=off, GOFLAGS=-mod=readonly, CGO_ENABLED=0, ... (item 2, M-1 a)
  godebug      no GODEBUG in the Dockerfile, no `godebug` in go.mod, no `//go:` in main.go (M-1 d)
  copy         the builder copies go.mod, go.sum and main.go by name; no ADD (M-1 b)
  compile      one network-free go build with Caddy's release flags; module steps present
  applets      the BusyBox link set equals the image contract (15 applets)
  final-stage  the final stage is COPY, ENV, WORKDIR, CMD, ARG, LABEL only
  run-config   CMD, ENV and WORKDIR as the stack expects
  entrypoint   no ENTRYPOINT in any stage
  volume       no VOLUME in any stage
  gomod        directives, one direct requirement, annotated overrides, go and toolchain lines (item 3, M-1 d)
  main-go      main.go imports exactly caddycmd and modules/standard
  directory    only the allowed files; go.sum and modules.txt present and non-empty (M-1 b)
"""
from __future__ import annotations

import json
import re
import shlex
import shutil
import unittest
from pathlib import Path

from deploy_common import ROOT, scratch_dir

IMAGE_DIR = ROOT / "deploy" / "caddy" / "image"

GO_ENV = {
    "CGO_ENABLED": "0", "GOOS": "linux", "GOARCH": "amd64", "GOAMD64": "v1", "GOTOOLCHAIN": "local",
    "GOFLAGS": "-mod=readonly", "GOWORK": "off", "GOPROXY": "https://proxy.golang.org", "GOSUMDB": "sum.golang.org",
    "GOPRIVATE": "", "GONOPROXY": "", "GONOSUMDB": "", "GOINSECURE": "",
}
# The image contract in docs/architecture/self-built-caddy.md: the union of what the stack uses from the image.
APPLETS = ["[", "cat", "chmod", "chown", "echo", "find", "grep", "nc", "printf", "rm", "sh", "test", "tr", "wc", "wget"]
RUNTIME_ENV = {"XDG_CONFIG_HOME": "/config", "XDG_DATA_HOME": "/data"}
RUNTIME_CMD = ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
# Caddy's own release flags (.goreleaser.yml at the tag) plus -buildid= and -buildvcs=false (ADR-0021 item 2).
RELEASE_BUILD = 'go build -trimpath -buildvcs=false -tags nobadger,nomysql,nopgx -ldflags "-s -w -buildid=" -o /out/caddy .'
# G3 M-1 f, the version-binding block inside the compile RUN: the builder's Go (GOLANG_VERSION, go env), the binary's
# build info (go version -m) and the Caddy version in go.mod must agree, and the build info shows the release flags.
BINDING_CHECKS = [
    "set -eu;",
    r'test -n "${GOLANG_VERSION}"',
    r'test "$(go env GOVERSION)" = "go${GOLANG_VERSION}"',
    r'info="$(go version -m /out/caddy)"',
    r'''caddy_version="$(go list -m -f '{{.Version}}' github.com/caddyserver/caddy/v2)"''',
    r'''head -n 1 | grep -qx "/out/caddy: go${GOLANG_VERSION}"''',
    r'''grep -q "^[[:space:]]dep[[:space:]]github.com/caddyserver/caddy/v2[[:space:]]${caddy_version}[[:space:]]"''',
    r'''grep -q "^[[:space:]]build[[:space:]]-trimpath=true$"''',
    r'''grep -q "^[[:space:]]build[[:space:]]CGO_ENABLED=0$"''',
    r'''grep -q "^[[:space:]]build[[:space:]]GOAMD64=v1$"''',
    r'''grep -q "^[[:space:]]build[[:space:]]-tags=nobadger,nomysql,nopgx$"''',
]
FINAL_COPIES = ["--from=builder /out/caddy /usr/bin/caddy", "--from=busybox /out/bin/ /bin/"]
FINAL_KINDS = {"FROM", "ARG", "LABEL", "COPY", "ENV", "WORKDIR", "CMD"}
MAIN_IMPORTS = {"github.com/caddyserver/caddy/v2/cmd", "github.com/caddyserver/caddy/v2/modules/standard"}
CADDY_MODULE = "github.com/caddyserver/caddy/v2"
ALLOWED_FILES = {"Dockerfile", "main.go", "go.mod", "go.sum", "modules.txt", "README.md"}
FROM_RE = re.compile(r"^(\S+/\S+:[A-Za-z0-9._-]+)@sha256:([0-9a-f]{64}) AS (\S+)$")
ADVISORY = r"(?:GO-\d{4}-\d{4,}|CVE-\d{4}-\d{4,})"
OVERRIDE_COMMENT = re.compile(r"^indirect; override %s(?:,%s)* #\d+$" % (ADVISORY, ADVISORY))
PARSER_DIRECTIVE = re.compile(r"(?im)^\s*#\s*(syntax|escape)\s*=")


# --------------------------------------------------------------------------- Dockerfile

def instructions(text: str) -> list:
    """(stage index, KEYWORD, arguments) per instruction. Continuation lines are joined, comments dropped."""
    out, stage, buf = [], -1, ""
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


def env_pairs(rest: str) -> dict:
    pairs = {}
    for token in shlex.split(rest):
        key, sep, value = token.partition("=")
        if not sep:
            raise AssertionError("ENV token %r is not KEY=value" % token)
        pairs[key] = value
    return pairs


def version_tuple(value: str) -> tuple:
    return tuple(int(n) for n in re.findall(r"\d+", value)[:3])


def builder_go_version(text: str) -> tuple:
    m = re.search(r"(?m)^FROM docker\.io/library/golang:(\d+\.\d+\.\d+)-", text)
    assert m, "no golang builder FROM line in the Dockerfile"
    return version_tuple(m.group(1))


def dockerfile_problems(text: str) -> list:
    p = []
    if PARSER_DIRECTIVE.search(text):
        p.append("syntax: a parser directive is present; BuildKit's built-in frontend must be used")
    ins = instructions(text)
    code = "\n".join("%s %s" % (k, a) for _, k, a in ins)
    if re.search(r"godebug", code, re.I):
        p.append("godebug: the Dockerfile sets GODEBUG")

    # FROM lines
    froms = [a for _, k, a in ins if k == "FROM"]
    refs = {}
    for args in froms:
        m = FROM_RE.match(args)
        if not m:
            p.append("from: %r is not '<name>:<tag>@sha256:<64 hex> AS <stage>'" % args)
            continue
        name, digest, alias = m.groups()
        if set(digest) == {"0"}:
            p.append("from: %s has an unresolved all-zero digest" % alias)
        refs[alias] = name
    if list(refs) != ["builder", "busybox", "runtime"] or len(froms) != 3:
        p.append("from: expected exactly the stages builder, busybox, runtime, found %s" % list(refs))
        return p
    if not refs["builder"].startswith("docker.io/library/golang:"):
        p.append("from: the builder must be docker.io/library/golang")
    if not (refs["busybox"].startswith("docker.io/library/busybox:") and refs["busybox"].endswith("-musl")):
        p.append("from: the busybox stage must be docker.io/library/busybox:<x.y.z>-musl")
    if refs["runtime"] != "gcr.io/distroless/static-debian13:latest":
        p.append("from: the runtime must be the plain root gcr.io/distroless/static-debian13:latest (no debug or nonroot)")

    # any stage
    kinds = [k for _, k, _ in ins]
    if "ENTRYPOINT" in kinds:
        p.append("entrypoint: an ENTRYPOINT is present (the stack overrides the command for adapt and the one-shots)")
    if "VOLUME" in kinds:
        p.append("volume: a VOLUME is present (the Compose overlay mounts both paths)")
    if "ADD" in kinds:
        p.append("copy: ADD is not allowed")

    by_stage = {0: [], 1: [], 2: []}
    for stage, kind, args in ins:
        by_stage.setdefault(stage, []).append((kind, args))

    # builder
    builder = by_stage[0]
    env = {}
    for kind, args in builder:
        if kind == "ENV":
            env.update(env_pairs(args))
    for key, want in GO_ENV.items():
        if env.get(key) != want:
            p.append("env: %s is %r, expected %r" % (key, env.get(key), want))
    for key in sorted(set(env) - set(GO_ENV)):
        p.append("env: unexpected builder variable %s" % key)
    copies = [a for k, a in builder if k == "COPY"]
    if copies != ["go.mod go.sum main.go ./"]:
        p.append("copy: the builder must copy exactly 'go.mod go.sum main.go ./', found %s" % copies)
    runs = [a for k, a in builder if k == "RUN"]
    compiles = [a for a in runs if "go build" in a]
    if len(compiles) != 1:
        p.append("compile: expected exactly one RUN with go build, found %d" % len(compiles))
    else:
        # One needle for the whole command: the version-binding greps in the same RUN also contain '-trimpath'.
        for needle in ("--network=none", RELEASE_BUILD):
            if needle not in compiles[0]:
                p.append("compile: the go build step lacks %r" % needle)
        for needle in BINDING_CHECKS:
            if needle not in compiles[0]:
                p.append("binding: the version-binding block of the compile step lacks %r" % needle)
    for step in ("go mod download && go mod verify", "go mod tidy -diff"):
        if step not in runs:
            p.append("compile: the module step %r is missing" % step)

    # busybox
    applets = None
    for kind, args in by_stage[1]:
        m = re.search(r"for applet in (.*?); do", args) if kind == "RUN" else None
        if m:
            applets = shlex.split(m.group(1))
    if applets is None or sorted(applets) != sorted(APPLETS):
        p.append("applets: the link set %s differs from the image contract %s" % (applets, APPLETS))

    # final stage
    final = by_stage[2]
    for kind in sorted({k for k, _ in final} - FINAL_KINDS):
        p.append("final-stage: the final stage contains %s (COPY only, root, no entrypoint)" % kind)
    if [a for k, a in final if k == "COPY"] != FINAL_COPIES:
        p.append("final-stage: the final stage must copy exactly %s" % FINAL_COPIES)
    renv = {}
    for kind, args in final:
        if kind == "ENV":
            renv.update(env_pairs(args))
    if renv != RUNTIME_ENV:
        p.append("run-config: the final ENV is %s, expected %s" % (renv, RUNTIME_ENV))
    if [a for k, a in final if k == "WORKDIR"] != ["/srv"]:
        p.append("run-config: WORKDIR must be /srv")
    cmds = [a for k, a in final if k == "CMD"]
    try:
        cmd_ok = len(cmds) == 1 and json.loads(cmds[0]) == RUNTIME_CMD
    except ValueError:
        cmd_ok = False
    if not cmd_ok:
        p.append("run-config: CMD must be %s" % RUNTIME_CMD)
    return p


# --------------------------------------------------------------------------- go.mod, main.go, directory

def gomod_problems(text: str, builder: tuple) -> list:
    p = []
    block = None
    module = go_line = toolchain = None
    requires = []  # (path, version, comment)
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
                if kw != "require":
                    p.append("gomod: line %d opens a '%s' block" % (n, kw))
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
                p.append("gomod: line %d is an unreadable requirement" % n)
                continue
            requires.append((parts[0], parts[1], comment.strip()))
        elif kw == "godebug":
            p.append("godebug: line %d is a godebug directive" % n)
        else:
            p.append("gomod: line %d: directive '%s' is not allowed (replace, exclude, retract and tool are banned)" % (n, kw))
    if not module or not go_line:
        p.append("gomod: the module and go lines are required")
    if go_line and version_tuple(go_line) > builder:
        p.append("gomod: the go line %s is above the builder's Go %s" % (go_line, builder))
    if toolchain and version_tuple(toolchain) > builder:
        p.append("gomod: the toolchain line %s is above the builder's Go %s" % (toolchain, builder))
    direct = [(path, version) for path, version, comment in requires if not comment.startswith("indirect")]
    if len(direct) != 1 or direct[0][0] != CADDY_MODULE:
        p.append("gomod: exactly one direct requirement, %s, expected; found %s" % (CADDY_MODULE, [d[0] for d in direct]))
    elif not re.fullmatch(r"v\d+\.\d+\.\d+", direct[0][1]):
        p.append("gomod: the Caddy requirement %s is not a release tag" % direct[0][1])
    overrides = {}
    for path, version, comment in requires:
        if comment.startswith("indirect;"):
            if not OVERRIDE_COMMENT.match(comment):
                p.append("gomod: the override of %s must read '// indirect; override <GO or CVE id>[,<id>] #<issue>'" % path)
            overrides[path] = (version, comment)
    net = overrides.get("golang.org/x/net")
    if net is None:
        p.append("gomod: the golang.org/x/net override is missing (remove this rule with the override, "
                 "when the build's stale-override check says upstream has caught up)")
    elif not re.fullmatch(r"v\d+\.\d+\.\d+", net[0]) or not net[1].endswith(" #188"):
        p.append("gomod: the x/net override must be a release version and end with ' #188'")
    return p


def main_go_problems(text: str) -> list:
    p = []
    lines = text.splitlines()
    if any(l.strip().startswith("//go:") for l in lines):
        p.append("godebug: main.go carries a //go: directive (//go:debug switches Go's security defaults)")
    body = "\n".join(l for l in lines if not l.strip().startswith("//"))
    if set(re.findall(r'"([^"]+)"', body)) != MAIN_IMPORTS:
        p.append("main-go: main.go must import exactly %s" % sorted(MAIN_IMPORTS))
    return p


def directory_problems(folder: Path) -> list:
    p = []
    for entry in sorted(folder.iterdir()):
        if entry.is_symlink() or entry.is_dir() or entry.name not in ALLOWED_FILES:
            p.append("directory: %s is not an allowed input (no vendor, go.work or other .go file)" % entry.name)
    for name in ("Dockerfile", "main.go", "go.mod"):
        if not (folder / name).is_file():
            p.append("directory: %s is missing" % name)
    for name in ("go.sum", "modules.txt"):
        path = folder / name
        if not path.is_file() or path.stat().st_size == 0:
            p.append("directory: %s is missing or empty" % name)
    return p


# --------------------------------------------------------------------------- red-case helpers

def read(name: str) -> str:
    return (IMAGE_DIR / name).read_text(encoding="utf-8")


def mutate(text: str, old: str, new: str) -> str:
    assert old in text, "fixture text not found: %r" % old
    return text.replace(old, new, 1)


def resub(text: str, pattern: str, repl: str) -> str:
    result, count = re.subn(pattern, repl, text, count=1, flags=re.M)
    assert count == 1, "fixture pattern not found: %r" % pattern
    return result


def tags(problems: list) -> set:
    return {x.split(":")[0] for x in problems}


class DockerfileInputTests(unittest.TestCase):
    def setUp(self):
        self.text = read("Dockerfile")

    def assert_red(self, tag: str, text: str, containing: str = ""):
        problems = dockerfile_problems(text)
        self.assertIn(tag, tags(problems), "no %s problem; got %s" % (tag, problems))
        if containing:
            self.assertTrue(any(containing in x for x in problems), "no problem mentions %r: %s" % (containing, problems))

    def test_the_real_dockerfile_breaks_no_rule(self):
        self.assertEqual(dockerfile_problems(self.text), [])

    def test_the_contract_lists_fifteen_applets(self):
        self.assertEqual(len(APPLETS), 15)
        self.assertEqual(len(set(APPLETS)), 15)

    # ---- frontend, FROM pins
    def test_red_a_parser_directive_is_refused(self):
        self.assert_red("syntax", "# syntax=docker/dockerfile:1\n" + self.text)
        self.assert_red("syntax", "# escape=`\n" + self.text)

    def test_red_a_from_without_a_digest_is_refused(self):
        for alias in ("builder", "busybox", "runtime"):
            with self.subTest(stage=alias):
                text = resub(self.text, r"^(FROM \S+)@sha256:[0-9a-f]{64}( AS %s)$" % alias, r"\1\2")
                self.assert_red("from", text)

    def test_red_a_short_digest_is_refused(self):
        self.assert_red("from", resub(self.text, r"@sha256:[0-9a-f]{64}( AS builder)$", r"@sha256:abc123\1"))

    def test_red_an_unresolved_zero_digest_is_refused(self):
        self.assert_red("from", resub(self.text, r"@sha256:[0-9a-f]{64}( AS busybox)$", r"@sha256:" + "0" * 64 + r"\1"), "all-zero")

    def test_red_a_debug_or_nonroot_runtime_is_refused(self):
        for variant in ("debug", "nonroot", "debug-nonroot"):
            with self.subTest(variant=variant):
                text = resub(self.text, r"static-debian13:latest@", "static-debian13:%s@" % variant)
                self.assert_red("from", text, "plain root")

    def test_red_a_fourth_stage_is_refused(self):
        self.assert_red("from", self.text + "\nFROM docker.io/library/busybox:1.0.0-musl@sha256:" + "a" * 64 + " AS extra\n")

    # ---- the Go environment
    def test_red_gowork_not_off_is_refused(self):
        # The ENV line is meant: a comment above it also names GOWORK=off.
        self.assert_red("env", mutate(self.text, "    GOWORK=off \\\n", "    GOWORK= \\\n"), "GOWORK")
        self.assert_red("env", mutate(self.text, "    GOWORK=off \\\n", ""), "GOWORK")

    def test_red_an_extra_goflags_flag_is_refused(self):
        self.assert_red("env", mutate(self.text, "GOFLAGS=-mod=readonly", "GOFLAGS=-mod=readonly,-toolexec=x"), "GOFLAGS")
        self.assert_red("env", mutate(self.text, "GOFLAGS=-mod=readonly", "GOFLAGS=-mod=mod"), "GOFLAGS")

    def test_red_cgo_enabled_is_refused(self):
        self.assert_red("env", mutate(self.text, "CGO_ENABLED=0", "CGO_ENABLED=1"), "CGO_ENABLED")

    def test_red_a_goproxy_with_direct_is_refused(self):
        self.assert_red("env", mutate(self.text, "GOPROXY=https://proxy.golang.org", "GOPROXY=https://proxy.golang.org,direct"), "GOPROXY")

    def test_red_a_disabled_checksum_database_is_refused(self):
        self.assert_red("env", mutate(self.text, "GOSUMDB=sum.golang.org", "GOSUMDB=off"), "GOSUMDB")
        self.assert_red("env", mutate(self.text, "GONOSUMDB=", "GONOSUMDB=example.invalid"), "GONOSUMDB")

    def test_red_a_private_or_insecure_module_setting_is_refused(self):
        self.assert_red("env", mutate(self.text, "GOPRIVATE=", "GOPRIVATE=example.invalid"), "GOPRIVATE")
        self.assert_red("env", mutate(self.text, "GOINSECURE=", "GOINSECURE=example.invalid"), "GOINSECURE")

    def test_red_a_local_toolchain_switch_is_refused(self):
        self.assert_red("env", mutate(self.text, "GOTOOLCHAIN=local", "GOTOOLCHAIN=auto"), "GOTOOLCHAIN")

    def test_red_an_extra_builder_variable_is_refused(self):
        self.assert_red("env", mutate(self.text, "WORKDIR /src\n", "ENV GOEXPERIMENT=x\nWORKDIR /src\n"), "GOEXPERIMENT")

    def test_red_godebug_in_the_dockerfile_is_refused(self):
        self.assert_red("godebug", mutate(self.text, "WORKDIR /src\n", "ENV GODEBUG=x509sha1=1\nWORKDIR /src\n"))

    # ---- copied inputs
    def test_red_a_directory_copy_is_refused(self):
        self.assert_red("copy", mutate(self.text, "COPY go.mod go.sum main.go ./", "COPY . ./"))

    def test_red_a_copy_of_an_extra_file_is_refused(self):
        self.assert_red("copy", mutate(self.text, "COPY go.mod go.sum main.go ./", "COPY go.mod go.sum main.go go.work ./"))

    def test_red_add_is_refused(self):
        self.assert_red("copy", mutate(self.text, "COPY go.mod go.sum main.go ./", "ADD go.mod go.sum main.go ./"))

    def test_red_a_compile_step_with_network_is_refused(self):
        self.assert_red("compile", mutate(self.text, "RUN --network=none set -eu;", "RUN set -eu;"), "--network=none")

    def test_red_missing_release_flags_are_refused(self):
        self.assert_red("compile", mutate(self.text, "-trimpath ", ""), "-trimpath")
        self.assert_red("compile", mutate(self.text, "-tags nobadger,nomysql,nopgx", "-tags nobadger"), "-tags")

    def test_red_each_version_binding_check_is_required(self):
        for needle in BINDING_CHECKS:
            with self.subTest(check=needle[:50]):
                self.assert_red("binding", mutate(self.text, needle, "true"), needle[:20])

    def test_red_a_removed_version_binding_block_is_refused(self):
        start = self.text.index('    info="$(go version -m /out/caddy)"; \\\n')
        end = self.text.index("\n\n# BusyBox:")
        text = self.text[:start] + "    true\n" + self.text[end + 1:]
        self.assert_red("binding", text)

    def test_red_a_missing_module_verification_is_refused(self):
        self.assert_red("compile", mutate(self.text, "RUN go mod download && go mod verify\n", "RUN go mod download\n"))

    # ---- applets
    def test_red_an_extra_applet_link_is_refused(self):
        self.assert_red("applets", mutate(self.text, " wget; do", " wget tar; do"))
        self.assert_red("applets", mutate(self.text, " wget; do", " wget telnet; do"))

    def test_red_a_missing_applet_link_is_refused(self):
        self.assert_red("applets", mutate(self.text, " nc printf", " printf"))

    # ---- final stage and run configuration
    def test_red_a_run_in_the_final_stage_is_refused(self):
        self.assert_red("final-stage", self.text + "\nRUN chmod 4755 /usr/bin/caddy\n", "RUN")

    def test_red_a_user_or_healthcheck_in_the_final_stage_is_refused(self):
        self.assert_red("final-stage", self.text + "\nUSER 65532\n", "USER")
        self.assert_red("final-stage", self.text + "\nHEALTHCHECK CMD true\n", "HEALTHCHECK")

    def test_red_an_extra_copy_in_the_final_stage_is_refused(self):
        self.assert_red("final-stage", mutate(self.text, "COPY --from=busybox /out/bin/ /bin/\n", "COPY --from=busybox /out/bin/ /bin/\nCOPY --from=builder /src /src\n"))

    def test_red_a_wrong_cmd_is_refused(self):
        self.assert_red("run-config", mutate(self.text, '"--adapter", "caddyfile"', '"--adapter", "json"'))

    def test_red_a_wrong_workdir_is_refused(self):
        self.assert_red("run-config", mutate(self.text, "WORKDIR /srv", "WORKDIR /"))

    def test_red_a_wrong_or_extra_runtime_env_is_refused(self):
        self.assert_red("run-config", mutate(self.text, "XDG_DATA_HOME=/data", "XDG_DATA_HOME=/var/lib/caddy"))
        self.assert_red("run-config", mutate(self.text, "WORKDIR /srv\n", "ENV EXTRA=1\nWORKDIR /srv\n"))

    def test_red_an_entrypoint_is_refused(self):
        self.assert_red("entrypoint", self.text + '\nENTRYPOINT ["caddy"]\n')

    def test_red_an_entrypoint_in_the_builder_is_refused(self):
        self.assert_red("entrypoint", mutate(self.text, "WORKDIR /src\n", 'ENTRYPOINT ["go"]\nWORKDIR /src\n'))

    def test_red_a_volume_is_refused(self):
        self.assert_red("volume", self.text + "\nVOLUME /data\n")


class GoModuleInputTests(unittest.TestCase):
    def setUp(self):
        self.text = read("go.mod")
        self.builder = builder_go_version(read("Dockerfile"))

    def assert_red(self, tag: str, text: str):
        problems = gomod_problems(text, self.builder)
        self.assertIn(tag, tags(problems), "no %s problem; got %s" % (tag, problems))

    def test_the_real_go_mod_breaks_no_rule(self):
        self.assertEqual(gomod_problems(self.text, self.builder), [])

    def test_the_override_line_has_the_documented_format(self):
        line = [l for l in self.text.splitlines() if l.startswith("require golang.org/x/net ")]
        self.assertEqual(len(line), 1)
        self.assertRegex(line[0], r"^require golang\.org/x/net v\d+\.\d+\.\d+ // indirect; override GO-\d{4}-\d{4,}(,GO-\d{4}-\d{4,})* #188$")

    def test_red_a_replace_is_refused(self):
        self.assert_red("gomod", self.text + "\nreplace golang.org/x/net => ../net\n")
        self.assert_red("gomod", self.text + "\nreplace (\n\tgolang.org/x/net => ../net\n)\n")

    def test_red_exclude_retract_and_tool_are_refused(self):
        for line in ("exclude golang.org/x/net v0.1.0", "retract v0.0.1", "tool golang.org/x/tools/cmd/stringer"):
            with self.subTest(line=line):
                self.assert_red("gomod", self.text + "\n" + line + "\n")

    def test_red_a_godebug_directive_is_refused(self):
        self.assert_red("godebug", self.text + "\ngodebug x509sha1=1\n")
        self.assert_red("godebug", self.text + "\ngodebug (\n\tx509sha1=1\n)\n")

    def test_red_a_second_direct_requirement_is_refused(self):
        self.assert_red("gomod", self.text + "\nrequire github.com/example/other v1.0.0\n")

    def test_red_a_missing_caddy_requirement_is_refused(self):
        self.assert_red("gomod", mutate(self.text, "require github.com/caddyserver/caddy/v2 v2.11.7\n", ""))

    def test_red_a_pseudo_version_for_caddy_is_refused(self):
        self.assert_red("gomod", resub(self.text, r"^require github\.com/caddyserver/caddy/v2 v\S+$", "require github.com/caddyserver/caddy/v2 v2.11.8-0.20260101000000-abcdefabcdef"))

    def test_red_an_override_without_the_issue_is_refused(self):
        self.assert_red("gomod", mutate(self.text, " #188", ""))

    def test_red_an_override_without_advisory_ids_is_refused(self):
        self.assert_red("gomod", resub(self.text, r"override GO-\S+ #188$", "override #188"))

    def test_red_an_override_with_a_malformed_id_is_refused(self):
        self.assert_red("gomod", resub(self.text, r"override GO-2026-6603,", "override GO-26-6603,"))

    def test_red_an_override_for_another_issue_is_refused(self):
        self.assert_red("gomod", mutate(self.text, " #188", " #142"))

    def test_red_a_missing_x_net_override_is_refused(self):
        lines = [l for l in self.text.splitlines() if not l.startswith("require golang.org/x/net ")]
        self.assert_red("gomod", "\n".join(lines) + "\n")

    def test_red_a_go_line_above_the_builder_is_refused(self):
        self.assert_red("gomod", mutate(self.text, "\ngo 1.26.0\n", "\ngo 1.27.0\n"))

    def test_red_a_toolchain_above_the_builder_is_refused(self):
        self.assert_red("gomod", mutate(self.text, "\ngo 1.26.0\n", "\ngo 1.26.0\n\ntoolchain go1.26.99\n"))

    def test_a_toolchain_line_inside_the_builder_is_allowed(self):
        text = mutate(self.text, "\ngo 1.26.0\n", "\ngo 1.26.0\n\ntoolchain go1.26.0\n")
        self.assertEqual(gomod_problems(text, self.builder), [])


class MainGoInputTests(unittest.TestCase):
    def setUp(self):
        self.text = read("main.go")

    def assert_red(self, tag: str, text: str):
        problems = main_go_problems(text)
        self.assertIn(tag, tags(problems), "no %s problem; got %s" % (tag, problems))

    def test_the_real_main_go_breaks_no_rule(self):
        self.assertEqual(main_go_problems(self.text), [])

    def test_red_a_go_debug_directive_is_refused(self):
        self.assert_red("godebug", "//go:debug x509sha1=1\n" + self.text)

    def test_red_any_go_directive_is_refused(self):
        self.assert_red("godebug", mutate(self.text, "func main() {", "//go:noinline\nfunc main() {"))

    def test_red_an_extra_import_is_refused(self):
        old = '_ "github.com/caddyserver/caddy/v2/modules/standard"'
        self.assert_red("main-go", mutate(self.text, old, old + '\n\t_ "example.invalid/plugin"'))

    def test_red_a_missing_standard_modules_import_is_refused(self):
        self.assert_red("main-go", mutate(self.text, '_ "github.com/caddyserver/caddy/v2/modules/standard"', ""))

    def test_red_a_missing_command_import_is_refused(self):
        self.assert_red("main-go", mutate(self.text, 'caddycmd "github.com/caddyserver/caddy/v2/cmd"', ""))


class DirectoryInputTests(unittest.TestCase):
    def copy_folder(self, parent: Path) -> Path:
        target = parent / "image"
        shutil.copytree(IMAGE_DIR, target)
        return target

    def assert_red(self, mutator, containing: str = ""):
        with scratch_dir() as parent:
            folder = self.copy_folder(parent)
            mutator(folder)
            problems = directory_problems(folder)
        self.assertIn("directory", tags(problems), "no directory problem; got %s" % problems)
        if containing:
            self.assertTrue(any(containing in x for x in problems), "no problem mentions %r: %s" % (containing, problems))

    def test_the_real_folder_breaks_no_rule(self):
        self.assertEqual(directory_problems(IMAGE_DIR), [])

    def test_modules_txt_is_present_and_not_empty(self):
        path = IMAGE_DIR / "modules.txt"
        self.assertTrue(path.is_file())
        self.assertGreater(path.stat().st_size, 0)

    def test_red_a_vendor_directory_is_refused(self):
        self.assert_red(lambda f: (f / "vendor").mkdir(), "vendor")

    def test_red_a_go_work_file_is_refused(self):
        self.assert_red(lambda f: (f / "go.work").write_text("go 1.26.0\n", encoding="utf-8"), "go.work")
        self.assert_red(lambda f: (f / "go.work.sum").write_text("\n", encoding="utf-8"), "go.work.sum")

    def test_red_another_go_file_is_refused(self):
        self.assert_red(lambda f: (f / "extra.go").write_text("package main\n", encoding="utf-8"), "extra.go")

    def test_red_a_missing_modules_txt_is_refused(self):
        self.assert_red(lambda f: (f / "modules.txt").unlink(), "modules.txt")

    def test_red_an_empty_modules_txt_is_refused(self):
        self.assert_red(lambda f: (f / "modules.txt").write_text("", encoding="utf-8"), "modules.txt")

    def test_red_a_missing_or_empty_go_sum_is_refused(self):
        self.assert_red(lambda f: (f / "go.sum").unlink(), "go.sum")
        self.assert_red(lambda f: (f / "go.sum").write_text("", encoding="utf-8"), "go.sum")


if __name__ == "__main__":
    unittest.main()
