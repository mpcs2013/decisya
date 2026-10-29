"""The self-built Caddy image workflow keeps the #119 controls (issue #188, G3 M-2, ADR-0021 item 5).

Offline and stdlib only. `.github/workflows/caddy-image.yml` and the verify code of `.github/scripts/caddy_image.py`
are read as text (no YAML parser) and fail closed when a landmark is missing. `workflow_problems` returns one
problem per broken rule, each starting with a rule tag. The real files must give no problem, and every rule has
a red case: the real text with one deliberate change (written red first, then the file made to pass).

Rules and tags:
  triggers        exact triggers: push to main on the build-input paths, dispatch with no inputs, pull_request
  dispatch-main   a dispatch builds, publishes and verifies only on refs/heads/main
  pr-no-publish   a pull request never publishes (no publish or verify job, no upload, no push in build)
  permissions-top top-level `permissions: {}`
  permissions-job exact per-job permissions
  write-outside-publish  write and id-token only in `publish`
  publish-third-party    `publish` uses first-party checkout and download-artifact only
  publish-build   `publish` runs no Go and no docker build
  pins            every `uses:` is a 40-hex SHA with its tag comment
  checkout        every checkout drops its credentials
  identity        one exact identity, no regular expression, one flag-derivation code path
  negative        the negative verify cases are present
  build-integrity the #119 lines: checkout ref and RELEASE_SHA = github.sha, sparse-checkout lists, one artifact
                  name `caddy-image`, the pushed-config-digest check, registry login only after the download
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "caddy-image.yml"
SCRIPT = ROOT / ".github" / "scripts" / "caddy_image.py"

JOBS = ["build", "publish", "verify"]
PUSH_PATHS = ["deploy/caddy/image/**", ".github/workflows/caddy-image.yml", ".github/scripts/caddy_image.py"]
DISPATCH_MAIN = "github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main'"
EXPECTED_PERMISSIONS = {
    "build": {"contents": "read"},
    "publish": {"contents": "read", "packages": "write", "id-token": "write"},
    "verify": {"contents": "read", "packages": "read"},
}
PINNED_USE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$")
IDENTITY_LINE = re.compile(
    r'(?m)^IDENTITY = f"https://github\.com/\{ri\.REPO\}/\.github/workflows/caddy-image\.yml@\{ri\.WORKFLOW_REF\}"$')
NEGATIVE_MARKERS = [
    'negative("1 base image under this identity", ri.digest_only(pins["refs"]["runtime"]), flags, ("missing", "mismatch"))',
    'negative("2 wrong identity (release.yml)", ref, ri.verify_flags(ctx["sha"], trigger, ri.IDENTITY), ("mismatch",))',
    'negative("3 dry-run image as a push image", ref, ri.verify_flags(ctx["sha"], "push", IDENTITY), ("mismatch",))',
    "for attestation in (False, True):",
]
THIRD_PARTY_FREE = {"actions/checkout", "actions/download-artifact"}


# --------------------------------------------------------------------------- reading the workflow as text

def code_lines(block: str) -> list:
    return [l for l in block.splitlines() if l.strip() and not l.lstrip().startswith("#")]


def uncomment(line: str) -> str:
    return re.sub(r"\s+#.*$", "", line).rstrip()


def jobs_start(text: str) -> int:
    index = text.find("\njobs:\n")
    assert index >= 0, "no jobs: section in caddy-image.yml"
    return index + 1


def job_span(text: str, name: str) -> tuple:
    """From the job's header line to the next job header (or the end of the file)."""
    header = "\n  %s:\n" % name
    start = text.find(header, jobs_start(text))
    assert start >= 0, "job %s not found in caddy-image.yml" % name
    start += 1
    base = start + len(header) - 1
    following = re.search(r"\n  [A-Za-z_][\w-]*:\n", text[base:])
    end = len(text) if following is None else base + following.start() + 1
    return start, end


def job_block(text: str, name: str) -> str:
    start, end = job_span(text, name)
    return text[start:end]


def job_if(block: str):
    """The job-level `if:` as one line (a folded block scalar is joined), or None."""
    lines = block.splitlines()
    for i, line in enumerate(lines):
        m = re.match(r"^    if:\s*(.*)$", line)
        if not m:
            continue
        first = m.group(1).strip()
        parts = [] if first in (">-", ">", "|", "|-") else [first]
        for nxt in lines[i + 1:]:
            if re.match(r"^\s{6,}\S", nxt) and not nxt.lstrip().startswith("#"):
                parts.append(nxt.strip())
            else:
                break
        return " ".join(parts)
    return None


def job_permissions(block: str):
    lines = block.splitlines()
    for i, line in enumerate(lines):
        if line == "    permissions:":
            perms = {}
            for nxt in lines[i + 1:]:
                m = re.match(r"^      ([\w-]+):\s*(\S+)\s*$", nxt)
                if not m:
                    break
                perms[m.group(1)] = m.group(2)
            return perms
    return None


def trigger_sections(text: str):
    """(ordered trigger names, {name: {"head": text after the colon, "lines": code lines below}})."""
    found = re.search(r"(?ms)^on:\n(.*?)(?=^\S)", text)
    assert found, "no on: block in caddy-image.yml"
    order, sections, current = [], {}, None
    for line in code_lines(found.group(1)):
        m = re.match(r"^  ([A-Za-z_][\w-]*):(.*)$", line)
        if m:
            current = m.group(1)
            order.append(current)
            sections[current] = {"head": m.group(2).strip(), "lines": []}
        elif current:
            sections[current]["lines"].append(line)
    return order, sections


def section_shape(section: dict):
    heads = [l for l in section["lines"] if re.match(r"^    \S", l)]
    items = [l.strip()[2:] for l in section["lines"] if re.match(r"^      - ", l)]
    return heads, items


# --------------------------------------------------------------------------- the rules

def workflow_problems(text: str, script: str) -> list:
    p = []

    # triggers
    order, sections = trigger_sections(text)
    if order != ["push", "workflow_dispatch", "pull_request"]:
        p.append("triggers: the triggers are %s, expected exactly push, workflow_dispatch, pull_request" % order)
    if "push" in sections:
        heads, items = section_shape(sections["push"])
        if heads != ["    branches: [main]", "    paths:"]:
            p.append("triggers: push must be exactly 'branches: [main]' and 'paths:' (got %s)" % heads)
        if items != PUSH_PATHS:
            p.append("triggers: the push paths are %s, expected %s" % (items, PUSH_PATHS))
    if "workflow_dispatch" in sections:
        d = sections["workflow_dispatch"]
        if d["head"] or d["lines"]:
            p.append("triggers: workflow_dispatch takes no inputs and no other keys")
    if "pull_request" in sections:
        heads, items = section_shape(sections["pull_request"])
        if heads != ["    paths:"] or not set(PUSH_PATHS) <= set(items):
            p.append("triggers: pull_request must filter on paths that include the push paths")

    # jobs and `if:` guards
    names = re.findall(r"(?m)^  ([A-Za-z_][\w-]*):\s*$", text[jobs_start(text):])
    if names != JOBS:
        p.append("permissions-job: the jobs are %s, expected %s" % (names, JOBS))
    jobs = {n: job_block(text, n) for n in JOBS}
    for name in JOBS:
        cond = job_if(jobs[name]) or ""
        if re.search(r"workflow_dispatch'(?! && github\.ref == 'refs/heads/main')", cond):
            p.append("dispatch-main: %s lets a dispatch run outside refs/heads/main" % name)
    build_if = job_if(jobs["build"]) or ""
    if "github.event_name == 'pull_request'" not in build_if or "github.event_name == 'push'" not in build_if \
            or DISPATCH_MAIN not in build_if:
        p.append("dispatch-main: the build job must run for pull_request, push and a dispatch on main only")
    for name in ("publish", "verify"):
        cond = job_if(jobs[name]) or ""
        if "pull_request" in cond:
            p.append("pr-no-publish: %s runs on a pull request" % name)
        if "github.event_name == 'push'" not in cond or DISPATCH_MAIN not in cond:
            p.append("dispatch-main: %s must run only for push or a dispatch on main" % name)

    # a pull request never publishes
    build_code = "\n".join(code_lines(jobs["build"]))
    if re.search(r"caddy_image\.py\s+(push|sign|verify)\b|docker\s+(push|login)|cosign|ghcr\.io", build_code):
        p.append("pr-no-publish: the build job pushes, signs, verifies or logs in")
    upload = [s for s in re.split(r"(?m)^      - ", jobs["build"]) if "actions/upload-artifact@" in s]
    if len(upload) != 1 or "if: github.event_name != 'pull_request'" not in upload[0]:
        p.append("pr-no-publish: the artifact upload must be skipped on pull_request")

    # permissions
    if len(re.findall(r"(?m)^permissions:", text)) != 1 or not re.search(r"(?m)^permissions: \{\}\s*$", text):
        p.append("permissions-top: the workflow must start with exactly one top-level 'permissions: {}'")
    for name, expected in EXPECTED_PERMISSIONS.items():
        got = job_permissions(jobs[name])
        if got != expected:
            p.append("permissions-job: %s permissions are %s, expected %s" % (name, got, expected))
    scoped = {"(before jobs)": text[: jobs_start(text)], "build": jobs["build"], "verify": jobs["verify"]}
    for name, block in scoped.items():
        for line in code_lines(block):
            if re.search(r":\s*write(-all)?\s*$", line) or "id-token" in line or "write-all" in line:
                p.append("write-outside-publish: %s holds a write or id-token permission: %s" % (name, line.strip()))

    # publish: first-party actions only, no Go, no docker build
    publish_code = code_lines(jobs["publish"])
    uses = [uncomment(l.strip()) for l in publish_code if re.match(r"^\s*(?:-\s+)?uses:", l)]
    used = {re.sub(r"^.*uses:\s*", "", u).split("@")[0] for u in uses}
    if not used or not used <= THIRD_PARTY_FREE:
        p.append("publish-third-party: publish uses %s, only %s are allowed" % (sorted(used), sorted(THIRD_PARTY_FREE)))
    for line in publish_code:
        if re.match(r"^\s*(-\s+)?name:", line):
            continue
        if re.search(r"\bdocker\s+(buildx\s+)?build\b|\bdocker\s+buildx\b|\bgo\s+(build|run|mod|install|generate|test|get)\b"
                     r"|\bxcaddy\b|setup-go", line):
            p.append("publish-build: publish runs Go or a docker build: %s" % line.strip())

    # every uses: is a commit SHA with its tag comment
    seen = 0
    for line in code_lines(text):
        if not re.match(r"^\s*(?:-\s+)?uses:", line):
            continue
        seen += 1
        m = re.match(r"^\s*(?:-\s+)?uses:\s*(\S+)(\s+#\s*v\d\S*)?\s*$", line)
        if not m or not m.group(2) or not PINNED_USE.match(m.group(1)):
            p.append("pins: not pinned to a 40-hex SHA with a version comment: %s" % line.strip())
    if seen == 0:
        p.append("pins: no uses: line found")
    for step in re.split(r"(?m)^      - ", text):
        if "actions/checkout@" in step and "persist-credentials: false" not in step:
            p.append("checkout: a checkout step keeps its credentials")

    p += integrity_problems(text, jobs)
    p += script_problems(script)
    # the verify job calls the verify subcommand
    if "python3 .github/scripts/caddy_image.py verify" not in "\n".join(code_lines(jobs["verify"])):
        p.append("negative: the verify job does not run 'caddy_image.py verify'")
    return p


SPARSE = {
    "publish": [".github/release/", ".github/scripts/release_images.py", ".github/scripts/caddy_image.py"],
    "verify": [".github/release/", ".github/scripts/release_images.py", ".github/scripts/caddy_image.py", "deploy/caddy/image/Dockerfile"],
}
SHA_EXPR = "${{ github.sha }}"
ARTIFACT = "caddy-image"
ARTIFACT_FORBIDDEN = ("run-id", "github-token", "pattern", "merge-multiple")
PUSH_MARKERS = [
    'if info["config_digest"] != ri.env_required("CADDY_CONFIG", ri.DIGEST_RE):',
    'if ri.sha256_file(archive) != ri.env_required("CADDY_SHA256", ri.HEX64_RE):',
    'if pushed_config != info["config_digest"]:',
    'if event == "push":\n        m = PUSH_TAG_RE.match(tag)\n        if not m or m.group(2) != sha:',
]


def integrity_problems(text: str, jobs: dict) -> list:
    """The #119 build-integrity lines (G4-119-01 and -04): the commit, the sparse checkout, one artifact, the digest, the login."""
    p = []
    # G4-119-01: the build commit is github.sha, bound to every script step and every non-PR checkout
    refs = re.findall(r"(?m)^\s+ref: (.*)$", text)
    if refs != [SHA_EXPR] * 3:
        p.append("build-integrity: checkout refs are %s, expected three of %s" % (refs, SHA_EXPR))
    for name in ("publish", "verify"):
        if "ref: " + SHA_EXPR not in jobs[name]:
            p.append("build-integrity: the %s checkout does not use ref: %s" % (name, SHA_EXPR))
    shas = re.findall(r"(?m)^\s+RELEASE_SHA: (.*)$", text)
    if not shas or any(s != SHA_EXPR for s in shas):
        p.append("build-integrity: RELEASE_SHA must always be %s, got %s" % (SHA_EXPR, shas))
    for name in JOBS:
        if "RELEASE_SHA: " + SHA_EXPR not in jobs[name]:
            p.append("build-integrity: the %s job does not bind RELEASE_SHA" % name)

    # the sparse checkout of publish and verify
    for name, expected in SPARSE.items():
        block = jobs[name]
        m = re.search(r"sparse-checkout: \|\n((?:            \S.*\n)+)", block)
        got = [l.strip() for l in m.group(1).splitlines()] if m else None
        if got != expected:
            p.append("build-integrity: the %s sparse-checkout list is %s, expected %s" % (name, got, expected))
        if "sparse-checkout-cone-mode: false" not in block:
            p.append("build-integrity: the %s sparse checkout is not in non-cone mode" % name)
    if "sparse-checkout" in jobs["build"]:
        p.append("build-integrity: the build job must check out everything it builds, not a sparse list")

    # G4-119-04: one artifact name, upload in build, download in publish, no run-id, token, pattern or merge
    for name, action, count in (("build", "upload-artifact", 1), ("publish", "download-artifact", 1)):
        steps = [s for s in re.split(r"(?m)^      - ", jobs[name]) if "actions/%s@" % action in s]
        if len(steps) != count or len(re.findall(r"actions/%s@" % action, text)) != 1:
            p.append("build-integrity: expected exactly one %s step, in %s" % (action, name))
            continue
        names = re.findall(r"(?m)^\s+name: (\S+)$", steps[0])
        if names != [ARTIFACT]:
            p.append("build-integrity: the %s artifact name is %s, expected only %s" % (action, names, ARTIFACT))
        for key in ARTIFACT_FORBIDDEN:
            if re.search(r"(?m)^\s+%s:" % key, steps[0]):
                p.append("build-integrity: the %s step sets %s" % (action, key))

    # the config digest and archive hash travel from build to publish as outputs
    if "caddy_config: ${{ steps.inspect.outputs.caddy_config }}" not in jobs["build"] \
            or "caddy_sha256: ${{ steps.inspect.outputs.caddy_sha256 }}" not in jobs["build"]:
        p.append("build-integrity: the build job must output caddy_config and caddy_sha256 from the inspect step")
    for line in ("CADDY_CONFIG: ${{ needs.build.outputs.caddy_config }}", "CADDY_SHA256: ${{ needs.build.outputs.caddy_sha256 }}"):
        if line not in jobs["publish"]:
            p.append("build-integrity: the publish job lacks %s" % line)

    # login only after the artifact download
    steps = re.split(r"(?m)^      - ", jobs["publish"])[1:]
    download = [i for i, s in enumerate(steps) if "actions/download-artifact@" in s]
    login = [i for i, s in enumerate(steps) if "docker login" in s]
    if len(download) != 1 or len(login) != 1 or login[0] < download[0]:
        p.append("build-integrity: the registry login must come after the artifact download in publish")
    if "docker login" in jobs["build"]:
        p.append("build-integrity: the build job logs in to the registry")
    return p


def script_problems(script: str) -> list:
    p = []
    for marker in PUSH_MARKERS:
        if marker not in script:
            p.append("build-integrity: caddy_image.py lacks: %s" % marker.splitlines()[0])
    if script.count('ri.env_required("RELEASE_SHA", ri.SHA40_RE)') != 2:
        p.append("build-integrity: both contexts must read RELEASE_SHA through ri.env_required")
    if re.search(r"regexp", script, re.I):
        p.append("identity: a regular-expression identity flag appears in caddy_image.py")
    if not IDENTITY_LINE.search(script):
        p.append("identity: IDENTITY must be the exact caddy-image.yml identity on refs/heads/main")
    verify = re.search(r"def cmd_verify\(_args\).*?(?=\ndef cmd_verify_command)", script, re.S)
    command = re.search(r"def cmd_verify_command\(args\).*?(?=\nCOMMANDS)", script, re.S)
    if not verify or not command:
        p.append("negative: cmd_verify or cmd_verify_command not found in caddy_image.py")
        return p
    for marker in NEGATIVE_MARKERS:
        if marker not in verify.group(0):
            p.append("negative: cmd_verify lacks: %s" % marker)
    if "derive_flags(" not in verify.group(0) or "derive_flags(" not in command.group(0):
        p.append("identity: the verify job and verify-command must derive the flags with the same derive_flags()")
    return p


# --------------------------------------------------------------------------- red-case helpers

def mutate(text: str, old: str, new: str) -> str:
    assert old in text, "fixture text not found: %r" % old
    return text.replace(old, new, 1)


def mutate_job(text: str, job: str, old: str, new: str) -> str:
    start, end = job_span(text, job)
    return text[:start] + mutate(text[start:end], old, new) + text[end:]


def rule_tags(problems: list) -> set:
    return {p.split(":")[0] for p in problems}


class CaddyImageWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")
        self.script = SCRIPT.read_text(encoding="utf-8")

    def assert_red(self, tag: str, text: str | None = None, script: str | None = None):
        problems = workflow_problems(self.text if text is None else text, self.script if script is None else script)
        self.assertIn(tag, rule_tags(problems), "no %s problem; got %s" % (tag, problems))

    def test_the_real_workflow_and_script_break_no_rule(self):
        self.assertEqual(workflow_problems(self.text, self.script), [])

    # ---- triggers
    def test_red_an_extra_trigger_is_refused(self):
        for trigger in ("pull_request_target", "workflow_call", "workflow_run", "schedule"):
            with self.subTest(trigger=trigger):
                self.assert_red("triggers", mutate(self.text, "  workflow_dispatch:\n", "  %s:\n    types: [x]\n  workflow_dispatch:\n" % trigger))

    def test_red_a_dispatch_input_is_refused(self):
        self.assert_red("triggers", mutate(self.text, "  workflow_dispatch:\n", "  workflow_dispatch:\n    inputs:\n      tag:\n        type: string\n"))

    def test_red_a_push_to_another_branch_is_refused(self):
        self.assert_red("triggers", mutate(self.text, "branches: [main]", "branches: [main, 'issue/**']"))

    def test_red_a_missing_push_path_is_refused(self):
        self.assert_red("triggers", mutate(self.text, "    branches: [main]\n    paths:\n      - deploy/caddy/image/**\n", "    branches: [main]\n    paths:\n"))

    # ---- dispatch only on main
    def test_red_a_publish_dispatch_outside_main_is_refused(self):
        text = mutate_job(self.text, "publish", " && github.ref == 'refs/heads/main'", "")
        self.assert_red("dispatch-main", text)

    def test_red_a_verify_dispatch_outside_main_is_refused(self):
        text = mutate_job(self.text, "verify", " && github.ref == 'refs/heads/main'", "")
        self.assert_red("dispatch-main", text)

    def test_red_a_build_dispatch_outside_main_is_refused(self):
        text = mutate_job(self.text, "build", " && github.ref == 'refs/heads/main'", "")
        self.assert_red("dispatch-main", text)

    # ---- pull requests never publish
    def test_red_publish_on_a_pull_request_is_refused(self):
        text = mutate_job(self.text, "publish", "(github.event_name == 'push' ||", "(github.event_name == 'pull_request' || github.event_name == 'push' ||")
        self.assert_red("pr-no-publish", text)

    def test_red_verify_on_a_pull_request_is_refused(self):
        text = mutate_job(self.text, "verify", "(github.event_name == 'push' ||", "(github.event_name == 'pull_request' || github.event_name == 'push' ||")
        self.assert_red("pr-no-publish", text)

    def test_red_an_upload_on_a_pull_request_is_refused(self):
        text = mutate(self.text, "        if: github.event_name != 'pull_request'\n        uses: actions/upload-artifact", "        uses: actions/upload-artifact")
        self.assert_red("pr-no-publish", text)

    def test_red_a_push_step_in_the_build_job_is_refused(self):
        text = mutate_job(self.text, "build", "      # Pull requests upload nothing", "      - run: python3 .github/scripts/caddy_image.py push\n      # Pull requests upload nothing")
        self.assert_red("pr-no-publish", text)

    # ---- permissions
    def test_red_a_workflow_level_permission_is_refused(self):
        self.assert_red("permissions-top", mutate(self.text, "\npermissions: {}\n", "\npermissions:\n  contents: read\n"))

    def test_red_id_token_outside_publish_is_refused(self):
        text = mutate_job(self.text, "build", "    permissions:\n      contents: read\n    outputs:", "    permissions:\n      contents: read\n      id-token: write\n    outputs:")
        self.assert_red("write-outside-publish", text)
        self.assert_red("permissions-job", text)

    def test_red_write_in_verify_is_refused(self):
        text = mutate_job(self.text, "verify", "      packages: read\n", "      packages: write\n")
        self.assert_red("write-outside-publish", text)
        self.assert_red("permissions-job", text)

    def test_red_a_widened_publish_permission_is_refused(self):
        text = mutate_job(self.text, "publish", "      packages: write\n", "      packages: write\n      contents: write\n")
        self.assert_red("permissions-job", text)

    # ---- publish holds no build code
    def test_red_a_third_party_action_in_publish_is_refused(self):
        step = "      - uses: docker/setup-buildx-action@%s  # v3.0.0\n" % ("a" * 40)
        self.assert_red("publish-third-party", mutate_job(self.text, "publish", "      - name: Log out of GHCR\n", step + "      - name: Log out of GHCR\n"))

    def test_red_go_in_publish_is_refused(self):
        text = mutate_job(self.text, "publish", "        run: python3 .github/scripts/caddy_image.py push\n", "        run: go build ./...\n")
        self.assert_red("publish-build", text)

    def test_red_a_docker_build_in_publish_is_refused(self):
        text = mutate_job(self.text, "publish", "docker logout ghcr.io || true", "docker build -t x deploy/caddy/image")
        self.assert_red("publish-build", text)

    # ---- pins
    def test_red_an_action_pinned_to_a_tag_is_refused(self):
        old = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1  # v7.0.1"
        self.assert_red("pins", mutate(self.text, old, "actions/checkout@v7  # v7.0.1"))

    def test_red_an_action_pinned_to_a_short_sha_is_refused(self):
        old = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1  # v7.0.1"
        self.assert_red("pins", mutate(self.text, old, "actions/checkout@3d3c42e  # v7.0.1"))

    def test_red_a_pin_without_its_tag_comment_is_refused(self):
        old = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1  # v7.0.1"
        self.assert_red("pins", mutate(self.text, old, "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"))

    def test_red_a_checkout_that_keeps_credentials_is_refused(self):
        self.assert_red("checkout", mutate(self.text, "persist-credentials: false", "fetch-depth: 1"))

    # ---- identity and negative cases
    def test_red_a_regular_expression_identity_is_refused(self):
        self.assert_red("identity", script=self.script + "\nFLAG = '--certificate-identity-regexp'\n")

    def test_red_an_inexact_identity_is_refused(self):
        self.assert_red("identity", script=mutate(self.script, "caddy-image.yml@{ri.WORKFLOW_REF}", "caddy-image.yml.*@{ri.WORKFLOW_REF}"))

    def test_red_a_separate_flag_derivation_is_refused(self):
        start = self.script.index("def cmd_verify_command(args)")
        script = self.script[:start] + self.script[start:].replace("derive_flags(", "other_flags(")
        self.assert_red("identity", script=script)

    def test_red_each_negative_verify_case_is_required(self):
        for marker in NEGATIVE_MARKERS:
            with self.subTest(marker=marker[:40]):
                self.assert_red("negative", script=mutate(self.script, marker, "pass"))

    def test_red_a_verify_job_that_does_not_verify_is_refused(self):
        text = mutate_job(self.text, "verify", "run: python3 .github/scripts/caddy_image.py verify\n", "run: echo skipped\n")
        self.assert_red("negative", text)

    # ---- build integrity (#119 G4-119-01 and -04)
    def test_red_a_checkout_that_does_not_use_the_pushed_commit_is_refused(self):
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "          ref: ${{ github.sha }}\n", ""))
        self.assert_red("build-integrity", mutate_job(self.text, "verify", "ref: ${{ github.sha }}", "ref: ${{ github.ref }}"))
        self.assert_red("build-integrity", mutate_job(self.text, "build", "ref: ${{ github.sha }}", "ref: main"))

    def test_red_a_release_sha_that_is_not_the_commit_is_refused(self):
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "RELEASE_SHA: ${{ github.sha }}", "RELEASE_SHA: ${{ needs.build.outputs.tag }}"))
        self.assert_red("build-integrity", mutate_job(self.text, "verify", "RELEASE_SHA: ${{ github.sha }}", "RELEASE_SHA: ${{ github.event.after }}"))

    def test_red_a_script_that_does_not_read_release_sha_is_refused(self):
        script = self.script.replace('ri.env_required("RELEASE_SHA", ri.SHA40_RE)', 'os.environ.get("RELEASE_SHA")')
        self.assert_red("build-integrity", script=script)
        self.assert_red("build-integrity", script=mutate(self.script, "if not m or m.group(2) != sha:", "if not m:"))

    def test_red_a_changed_sparse_checkout_is_refused(self):
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "            .github/scripts/release_images.py\n", ""))
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "            .github/release/\n", "            .github/release/\n            src/\n"))
        self.assert_red("build-integrity", mutate_job(self.text, "verify", "            deploy/caddy/image/Dockerfile\n", ""))
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "sparse-checkout-cone-mode: false", "sparse-checkout-cone-mode: true"))

    def test_red_a_changed_artifact_name_or_option_is_refused(self):
        self.assert_red("build-integrity", mutate_job(self.text, "build", "          name: caddy-image\n", "          name: caddy-image-${{ github.run_id }}\n"))
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "          name: caddy-image\n", "          name: other\n"))
        for option in ("run-id: 1", "github-token: ${{ github.token }}", "pattern: caddy-*", "merge-multiple: true"):
            with self.subTest(option=option):
                text = mutate_job(self.text, "publish", "          name: caddy-image\n", "          name: caddy-image\n          %s\n" % option)
                self.assert_red("build-integrity", text)

    def test_red_a_second_artifact_step_is_refused(self):
        step = "      - uses: actions/download-artifact@%s  # v8.0.2\n        with:\n          name: caddy-image\n" % ("a" * 40)
        self.assert_red("build-integrity", mutate_job(self.text, "publish", "      - name: Log out of GHCR\n", step + "      - name: Log out of GHCR\n"))

    def test_red_a_missing_digest_hand_over_is_refused(self):
        line = "          CADDY_CONFIG: ${{ needs.build.outputs.caddy_config }}\n"
        self.assert_red("build-integrity", mutate_job(self.text, "publish", line, ""))
        self.assert_red("build-integrity", mutate_job(self.text, "build", "      caddy_config: ${{ steps.inspect.outputs.caddy_config }}\n", ""))

    def test_red_a_push_that_does_not_compare_the_config_digest_is_refused(self):
        for marker in PUSH_MARKERS:
            with self.subTest(marker=marker[:50]):
                self.assert_red("build-integrity", script=mutate(self.script, marker, "if False:"))

    def test_red_a_login_before_the_artifact_download_is_refused(self):
        text = mutate_job(self.text, "publish", "      # Exact name; no run-id", "      - run: docker login ghcr.io\n      # Exact name; no run-id")
        self.assert_red("build-integrity", text)

    def test_red_a_login_in_the_build_job_is_refused(self):
        text = mutate_job(self.text, "build", "      # Pull requests upload nothing", "      - run: docker login ghcr.io\n      # Pull requests upload nothing")
        self.assert_red("build-integrity", text)

    def test_the_negative_cases_name_both_cross_identities(self):
        # Our image under the release.yml identity, and a dry-run image under the push flags, must fail.
        verify = re.search(r"def cmd_verify\(_args\).*?(?=\ndef cmd_verify_command)", self.script, re.S).group(0)
        self.assertIn("ri.IDENTITY", verify)
        self.assertIn('ri.verify_flags(ctx["sha"], "push", IDENTITY)', verify)


if __name__ == "__main__":
    unittest.main()
