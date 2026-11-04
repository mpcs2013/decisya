#!/usr/bin/env python3
"""Pipeline gate checker for one GitHub issue.


Usage (from the repository root):
  python .claude/scripts/gates.py <n>              check every gate; exit 1 if any is not passed
  python .claude/scripts/gates.py <n> --next       print the first gate that is not passed ("done" if none)
  python .claude/scripts/gates.py init <n> --title "<title>" --class <class> --tier light|full [--kind <kind> --phase <id>]
                                                   [--epic EP-nn]
                                                   create docs/ai/pipeline/<n>.md from the template; with the
                                                   label set adopted (labels in project.json), the issue's kind and
                                                   phase are required and recorded in the manifest
  python .claude/scripts/gates.py classify         print the change class of the current branch
  python .claude/scripts/gates.py bot              a dependency bot's branch (gates.dependency_bot_branches): pass
                                                   only for a dependency-only change with no review-required path

The manifest docs/ai/pipeline/<n>.md holds one table row per gate:
  | G1 | product-owner | required | <artifact path> | |
Status values:
  required  an agent gate (G1, G2, G3, G5, G6): its artifact must exist and contain
            <!-- gate: G1 | verdict: PASS|PASS-WITH-NOTES|N/A | issue: #<n> -->
            (N/A also needs "reason: ..." inside the comment; G3 and G6 take no N/A, except G3 with
            "reason: light tier" when the manifest records "- Tier: light"); BLOCK never passes
A change that adds a third-party package to a package manifest (package.json, pyproject.toml, a requirements file,
a project or props file) needs G3 required and not N/A, whatever its class and tier; so does a change to a
review-required path.
  passed    an orchestrator gate (G0, G4, G7) the main session completed; Note holds the evidence
  skipped   Note holds the fields "reason: ..." and "approved: <name> YYYY-MM-DD", each at the start
            or after a ";" (as init writes them), the name one of gates.approvers
Exit 0 = every gate passed (or the command succeeded), 1 = a gate is not passed, 2 = usage, or
.claude/project.json or .claude/boundaries.json cannot be used (the path classes and the project
part of the review-required paths come from project.json, the G4 owner from boundaries.json).
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import importlib.util
import json
import os
import re
import subprocess
import sys
import tomllib
import unicodedata
from datetime import date
from pathlib import Path

SELF = Path(__file__).resolve()
ROOT = SELF.parents[2]
PIPELINE = ROOT / "docs" / "ai" / "pipeline"
PROJECT_FILE = ROOT / ".claude" / "project.json"  # fixed at import, never from a patched ROOT


def _load(name: str, path: Path):
    """Import a sibling module by absolute path, not by sys.path name."""
    module = sys.modules.get(name)
    if module is not None and Path(getattr(module, "__file__", "")).resolve() == path:
        return module
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    # the source file itself, never bytecode from a __pycache__ folder: a planted .pyc under any name the file
    # system maps to that folder would otherwise run in place of the signed source
    exec(compile(Path(path).read_bytes(), str(path), "exec"), module.__dict__)  # noqa: S102
    return module


claudecfg = _load("_claudecfg", SELF.parent / "_claudecfg.py")
hooklib = _load("_hooklib", SELF.parents[1] / "hooks" / "_hooklib.py")


def _stop(message: str) -> None:
    """Run as a script: exit 2 before any other output. Imported (roster, lint, tests): raise."""
    if __name__ == "__main__":
        print(f"gates: {message}", file=sys.stderr)
        sys.exit(2)
    raise hooklib.ConfigError(message)


def g4_owner(config: dict) -> str:
    """The G4 row's owner, from routing.G4 in .claude/boundaries.json."""
    agents = list(dict.fromkeys(row["agent"] for row in config.get("routing", {}).get("G4", [])))
    if not agents:
        raise hooklib.ConfigError("routing.G4 names no agent")
    # the main session is no implementer agent: a route to it is named apart (issue #32 item 21)
    implementers = [a for a in agents if a != hooklib.MAIN_SESSION]
    main = hooklib.MAIN_SESSION in agents
    if not implementers:
        return "the main session (by path)"
    return f"implementer (by path: {', '.join(implementers)}{'; the main session for its own paths' if main else ''})"


def boundaries_problem(exc: Exception) -> str:
    """Why boundaries.json cannot be used, for the terminal. A ConfigError carries the loader's fixed text naming the
    key and the rule; the few that quote a name from the file are reduced to printable ASCII and capped. Any other
    error gives only its type, so no other file content reaches the output."""
    if isinstance(exc, hooklib.ConfigError):
        return hooklib.printable(str(exc))
    return type(exc).__name__


try:
    PROJECT = claudecfg.load_project(PROJECT_FILE)
except claudecfg.ProjectConfigError as _exc:
    _stop(str(_exc))
try:
    G4_OWNER = g4_owner(hooklib.parse_boundaries((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8-sig")))
except Exception as _exc:  # noqa: BLE001
    _stop(f".claude/boundaries.json cannot be used: {boundaries_problem(_exc)}")

GATES = [
    ("G0", "orchestrator", "Issue open, branch issue/<n>-<slug>, manifest, change class"),
    ("G1", "product-owner", "Requirements with Gherkin acceptance criteria"),
    ("G2", "architect", "Architecture note, or N/A with a reason"),
    ("G3", "security-reviewer", "Threat model (threat delta)"),
    ("G4", G4_OWNER, "Code and tests; build and tests green (re-run by the orchestrator)"),
    ("G5", "test-engineer", "Traceability: every acceptance criterion mapped to a test"),
    ("G6", "security-reviewer", "Security diff review"),
    ("G7", "orchestrator", "PR body: Closes #<n>, one line per new package, Docs: line"),
]
ORCHESTRATOR_GATES = {"G0", "G4", "G7"}

# Gates run per change class; the others start as "skipped" pending an approver's yes (gates.approvers).
CLASSES = {
    "feature": {"G0", "G1", "G2", "G3", "G4", "G5", "G6", "G7"},
    "docs-only": {"G0", "G7"},
    "ci-tooling": {"G0", "G3", "G6", "G7"},
    "dependency": {"G0", "G6", "G7"},
}

# Paths that count as docs for the change class. Wider than the "not code" list in
# ci.yml's changes job: CI treats .claude/ and .vscode/ as code.
DOCS_ONLY = re.compile(r"^docs/|\.md$|^\.claude/|^\.vscode/|^LICENSE$|^\.github/(ISSUE_TEMPLATE/|dependabot\.yml$)")
# the CI-tooling and dependency path classes are project facts (gates.ci_tooling_paths and
# gates.dependency_paths in .claude/project.json), joined in the file's order.
try:
    CI_TOOLING = re.compile("|".join(f"(?:{p})" for p in PROJECT["gates"]["ci_tooling_paths"]))
    DEPENDENCY = re.compile("|".join(f"(?:{p})" for p in PROJECT["gates"]["dependency_paths"]))
except re.error:
    _stop(".claude/project.json gates.ci_tooling_paths or gates.dependency_paths does not compile")

# A skip's note holds fields separated by ";": a "reason: ..." field and exactly one "approved: <name> YYYY-MM-DD"
# field, the name one of gates.approvers and the date a real date. Fields are anchored, so "disapproved:" or an
# approval inside the reason does not count. The name group is the loader's name rule, so matching stays linear on
# any note; names compare after NFKC and case folding, so a lookalike such as the Kelvin sign is not a different K.
REASON_FIELD = re.compile(r"(?:^|;)\s*reason:\s*\S")
APPROVAL_FIELD = re.compile(r"(?:^|;)\s*approved:")
APPROVED = re.compile(r"(?:^|;)\s*approved:\s*([^\W\d_]+(?:[ .'-]{1,2}[^\W\d_]+)*\.?)\s+([0-9]{4}-[0-9]{2}-[0-9]{2})\b")


def approver_key(name: str) -> str:
    return unicodedata.normalize("NFKC", name).casefold()


APPROVERS = {approver_key(name) for name in PROJECT["gates"]["approvers"]}
GATE_LINE = re.compile(r"<!--\s*gate:\s*(G\d)\s*\|\s*verdict:\s*([A-Z/-]+)\s*\|\s*issue:\s*#(\d+)(.*?)-->")
# the G6 review's counts for the status pack's delivery report (ADR 0007, amendment of 2026-11-02): one own line,
# next to the verdict line, when the status pack is installed
FINDINGS_LINE = re.compile(r"<!--\s*findings:\s*high=(\d{1,4})\s+medium=(\d{1,4})\s+low=(\d{1,4})\s+info=(\d{1,4})"
                           r"\s*\|\s*rechecks:\s*(\d{1,3})\s*-->", re.ASCII)  # ASCII digits: int() takes others
EPIC = re.compile(r"EP-[0-9]{2,}")
ROW = re.compile(r"^\|\s*(G\d)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|([^|]*)\|\s*$")


def manifest_path(n: int) -> Path:
    return PIPELINE / f"{n}.md"


TIERS = ("light", "full")
TIER_LINE = re.compile(r"- Tier:[ \t]*(\S+)[ \t]*")
LIGHT_REASON = re.compile(r"reason:\s*light tier\b", re.IGNORECASE)


def manifest_tier(n: int) -> str:
    """The tier the manifest records (`- Tier: light|full`, written by init above the gate table); "full" when it
    records none or another value, so a missing line never relaxes a gate."""
    try:
        lines = manifest_path(n).read_text(encoding="utf-8-sig").splitlines()
    except OSError:
        return "full"
    for line in lines:
        if line.startswith("|"):
            break
        m = TIER_LINE.fullmatch(line)
        if m:
            return m.group(1).lower() if m.group(1).lower() in TIERS else "full"
    return "full"


def read_rows(n: int) -> dict[str, dict[str, str]]:
    rows = {}
    for line in manifest_path(n).read_text(encoding="utf-8-sig").splitlines():
        m = ROW.match(line.strip())
        if m:
            gate, owner, status, artifact, note = (s.strip() for s in m.groups())
            rows[gate] = {"owner": owner, "status": status.lower(), "artifact": artifact.strip("`"), "note": note}
    return rows


def approved_by_a_listed_approver(note: str) -> bool:
    if len(APPROVAL_FIELD.findall(note)) != 1:
        return False
    approval = APPROVED.search(note)
    if approval is None:
        return False
    try:
        date.fromisoformat(approval.group(2))
    except ValueError:
        return False
    return approver_key(approval.group(1)) in APPROVERS


def check_gate(n: int, gate: str, row: dict[str, str] | None) -> tuple[bool, str]:
    if row is None:
        return False, "no row in manifest"
    status, artifact, note = row["status"], row["artifact"], row["note"]
    if status == "skipped":
        if REASON_FIELD.search(note) and approved_by_a_listed_approver(note):
            return True, f"skipped ({note})"
        return False, (
            "skipped without the ';'-separated fields 'reason: ...' and "
            "'approved: <an approver from gates.approvers> YYYY-MM-DD'"
        )
    if gate in ORCHESTRATOR_GATES:
        if status == "passed" and note:
            if gate == "G7":
                problem = g7_docs_problem(n)
                if problem:
                    return False, problem
            return True, note
        return False, "not completed" if status != "passed" else "passed without evidence in Note"
    if status != "required":
        return False, f"unknown status '{status}'"
    if not artifact:
        return False, "MISSING: no artifact recorded"
    problem = artifact_path_problem(artifact, gate)
    if problem:
        return False, problem
    path = ROOT / artifact
    if not path.is_file():
        return False, f"MISSING: {artifact} does not exist"
    text = path.read_text(encoding="utf-8-sig")
    own = verdict_lines(text, gate)
    if not own:
        return False, (f"NO VERDICT: {artifact} has no own-line '<!-- gate: {gate} | verdict: ... -->' comment (column 0, "
                       "outside fenced code blocks and multi-line HTML comments; a '<!--' with no '-->' after it on its "
                       "line opens one, and a fence closes only on the same character, at least as long, alone)")
    if len(own) > 1:
        return False, f"AMBIGUOUS: {artifact} has {len(own)} verdict lines for {gate}; keep exactly one"
    m = own[0]
    verdict, issue, rest = m.group(2), int(m.group(3)), m.group(4)
    if issue != n:
        return False, f"verdict line names issue #{issue}, expected #{n}"
    # any other complete comment for the same gate and issue, even fenced, indented,
    # quoted or mid-sentence, makes the artifact ambiguous (examples must use placeholders).
    same = [x for x in GATE_LINE.finditer(text) if x.group(1) == gate and int(x.group(3)) == n]
    if len(same) > 1:
        return False, f"AMBIGUOUS: {artifact} contains {len(same)} verdict comments for {gate} and #{n}; examples must use placeholders"
    if verdict not in KNOWN_VERDICTS:
        return False, f"UNKNOWN VERDICT '{verdict}' in {artifact}; use one of {', '.join(sorted(KNOWN_VERDICTS))}"
    if verdict not in ("PASS", "PASS-WITH-NOTES", "N/A"):
        return False, f"{verdict} ({artifact})"
    if verdict == "N/A" and "reason:" not in rest:
        return False, "N/A without 'reason:'"
    if verdict == "N/A" and gate in ("G3", "G6"):
        # a required G3 or G6 is never answered with N/A at the Full tier; the Light tier's combined review alone
        # carries a G3 N/A, with `reason: light tier` (issue #32 item 19). A manifest without a tier is Full.
        light = manifest_tier(n) == "light" and gate == "G3" and LIGHT_REASON.search(rest)
        if not light:
            return False, (f"N/A is not allowed for a required {gate}: only the Light tier's combined review gives G3 "
                           "N/A, with 'reason: light tier' (record '- Tier: light' in the manifest)")
    if gate == "G6" and "status" in PROJECT["packs"]:
        problem = findings_problem(text)
        if problem:
            return False, f"{problem} in {artifact}"
    return True, f"{verdict} ({artifact})"


def marker_candidates(text: str):
    """The lines a verdict or findings line may be: outside fences, as CommonMark reads them (_fence_step: ``` and ~~~),
    and outside a multi-line HTML comment. A one-line comment is a candidate; the line that opens a multi-line comment,
    every line in it and the line that closes it are not, so a commented-out or quoted example never counts."""
    fence, in_comment = None, False
    for line in text.splitlines():
        if in_comment:  # as CommonMark reads an HTML block: inside it no fence counts, it ends at the first `-->`
            in_comment = _comment_after_close(line)
            continue
        fence, is_fence = _fence_step(line, fence)
        if is_fence or fence is not None:
            continue
        if _opens_comment(line):
            in_comment = True
            continue
        yield line


def _opens_comment(line: str) -> bool:
    """A `<!--` with no `-->` after it on its line opens a multi-line comment."""
    return "<!--" in line and "-->" not in line.rsplit("<!--", 1)[1]


def _comment_after_close(line: str) -> bool:
    """Inside a comment: whether one is still open after this line (no `-->`, or one that a later `<!--` reopens)."""
    if "-->" not in line:
        return True
    return _opens_comment(line.split("-->", 1)[1])


def findings_lines(text: str) -> list[re.Match[str]]:
    """Own-line findings comments, column 0, among the marker candidates, as verdict_lines reads verdicts."""
    found = []
    for line in marker_candidates(text):
        if line.startswith("<!--"):
            m = FINDINGS_LINE.fullmatch(line.rstrip())
            if m:
                found.append(m)
    return found


def findings_problem(text: str) -> str | None:
    lines = findings_lines(text)
    if len(lines) != 1:
        return (f"{'NO' if not lines else 'AMBIGUOUS'} FINDINGS: the G6 review needs exactly one own line "
                "'<!-- findings: high=<n> medium=<n> low=<n> info=<n> | rechecks: <n> -->' (the status pack reports it), "
                "at column 0, outside fenced code blocks and multi-line HTML comments")
    return None


# the G7 PR body names the docs it updated. Required from the issue number gates.docs_line_from (in
# .claude/project.json; 0 means every issue) on, and for any issue whose manifest is not on origin/main yet
# (every issue branch adds its own), so leaving the line out never switches the rule off. Earlier manifests
# are unaffected.
DOCS_LINE_FROM = PROJECT["gates"]["docs_line_from"]
G7_HEADING = "## G7 PR body draft"
DOCS_LINE = re.compile(r"^(?:\*\*)?Docs:(?:\*\*)?\s+(.+?)\s*$")
DOCS_PATH = re.compile(r"[A-Za-z0-9._/@+-]+")


def docs_required(n: int) -> tuple[bool, str | None]:
    """(required, failure). Condition 2 resolves origin/main by exit code and fails closed, in CI and
    locally, when it cannot. Never uses the branch."""
    if n >= DOCS_LINE_FROM:
        return True, None
    if git_rc("rev-parse", "--verify", "--quiet", "origin/main^{commit}") != 0:
        return True, "G7: origin/main is not available, so the Docs rule cannot be decided; run `git fetch origin`"
    return git_rc("cat-file", "-e", f"origin/main:docs/ai/pipeline/{int(n)}.md") != 0, None


_FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")


def _fence_step(line: str, fence: str | None) -> tuple[str | None, bool]:
    """(open fence after this line, whether the line is a fence line), as CommonMark reads it: a
    fence closes only on the same character, at least as long, with nothing after it."""
    m = _FENCE.match(line)
    if not m:
        return fence, False
    run, rest = m.groups()
    if fence is None:
        return run, True
    if run[0] == fence[0] and len(run) >= len(fence) and not rest.strip():
        return None, True
    return fence, False


def docs_line_problem(text: str) -> str | None:
    """The G7 section's Docs line, by string rules only: nothing named in it is opened or resolved."""
    lines, fence, headings = text.splitlines(), None, []
    for i, line in enumerate(lines):
        fence, is_fence = _fence_step(line, fence)
        if not is_fence and fence is None and line.rstrip() == G7_HEADING:
            headings.append(i)
    if len(headings) != 1:
        return f"G7: {'duplicate' if headings else 'no'} '{G7_HEADING}' section"
    found, fence, in_comment = [], None, False
    for line in lines[headings[0] + 1:]:
        if in_comment:  # as marker_candidates reads it: no fence inside a comment; a closing line may reopen one
            in_comment = _comment_after_close(line)
            continue
        fence, is_fence = _fence_step(line, fence)
        if is_fence or fence is not None:
            continue
        if "<!--" in line:  # a comment opening anywhere on the line, indented or mid-line
            in_comment = _opens_comment(line)
            continue
        if line.startswith("## "):
            break
        m = DOCS_LINE.match(line)
        if m:
            found.append(m.group(1))
    if not found:
        return "G7: PR body draft has no 'Docs:' line (files touched, or 'none: <reason>')"
    if len(found) > 1:
        return "G7: PR body draft has more than one 'Docs:' line"
    value = found[0]
    if value.startswith("<"):
        return "G7: the 'Docs:' line is still the template placeholder"
    if value.startswith("none:"):
        return None if value[5:].strip() else "G7: 'Docs: none:' needs a reason"
    for item in (p.strip().strip("`") for p in value.split(",")):
        if (not DOCS_PATH.fullmatch(item) or item.startswith("/") or re.match(r"^[A-Za-z]:", item)
                or ".." in item.split("/")):
            return f"G7: 'Docs:' lists '{item[:60]}', which is not a repository-relative path"
    return None


def g7_docs_problem(n: int) -> str | None:
    required, failure = docs_required(n)
    if failure:
        return failure
    if not required:
        return None
    return docs_line_problem(manifest_path(n).read_text(encoding="utf-8-sig"))


KNOWN_VERDICTS = {"PASS", "PASS-WITH-NOTES", "N/A", "BLOCK"}
# an agent gate's artifact must lie in its owner's write lane (.claude/boundaries.json).
GATE_OWNER = {"G1": "product-owner", "G2": "architect", "G3": "security-reviewer", "G5": "test-engineer", "G6": "security-reviewer"}


def _glob_to_regex(pattern: str) -> re.Pattern[str]:
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(f"^{out}$")


def artifact_path_problem(artifact: str, gate: str) -> str | None:
    """Relative POSIX path inside the repository, in the owner agent's lane."""
    if (re.match(r"^[A-Za-z]:", artifact) or artifact.startswith(("/", "\\")) or "\\" in artifact
            or ".." in artifact.split("/")):
        return f"INVALID PATH: {artifact} must be a relative POSIX path inside the repository"
    resolved = (ROOT / artifact).resolve()
    try:
        rel = resolved.relative_to(ROOT.resolve()).as_posix()
    except ValueError:
        return f"INVALID PATH: {artifact} resolves outside the repository"
    if resolved.exists() and not resolved.is_file():
        return f"INVALID PATH: {artifact} is not a regular file"
    owner = GATE_OWNER.get(gate)
    if owner:
        try:
            lanes = json.loads((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8-sig"))["agents"][owner]
        except (OSError, ValueError, KeyError):
            return f"INVALID PATH: cannot read {owner}'s lane from .claude/boundaries.json"
        # both the written path and its resolved target (a symlink in the lane may point out of it)
        if not all(any(_glob_to_regex(g).match(p) for g in lanes) for p in (artifact, rel)):
            return f"INVALID PATH: {artifact} is outside {owner}'s write lane ({', '.join(lanes)})"
    return None


def verdict_lines(text: str, gate: str) -> list[re.Match[str]]:
    """Own-line verdict comments for one gate: the whole line (column 0, trailing whitespace
    ignored) is the comment, among the marker candidates (outside ``` and ~~~ fences and outside a
    multi-line HTML comment). Indented, fenced, commented-out, quoted or mid-sentence copies never
    count as the verdict (and make the artifact AMBIGUOUS if they name the same gate and issue; see
    check_gate). Deviation from "line 1 only":
    G1 and G5 share the requirements file, so each gate's line may sit anywhere on its own line.
    """
    found = []
    for line in marker_candidates(text):
        if not line.startswith("<!--"):
            continue
        m = GATE_LINE.fullmatch(line.rstrip())
        if m and m.group(1) == gate:
            found.append(m)
    return found


def check(n: int, next_only: bool) -> int:
    if not manifest_path(n).exists():
        print(f"No manifest docs/ai/pipeline/{n}.md. Create it with: python .claude/scripts/gates.py init {n} --title \"...\" --class <class> --tier light|full")
        return 1
    rows = read_rows(n)
    failures = 0
    for gate, owner, what in GATES:
        ok, detail = check_gate(n, gate, rows.get(gate))
        if next_only and not ok:
            print(f"{gate} {owner}: {what} -> {detail}")
            return 0
        if not next_only:
            print(f"{'PASS' if ok else 'FAIL'}  {gate}  {owner:<26} {detail}")
        failures += not ok
    if next_only:
        print("done")
        return 0
    branch = current_branch()
    if not branch.startswith(f"issue/{n}-"):
        #: in CI a mismatch fails; it would skip the review-required check.
        print(f"{'FAIL' if in_ci() else 'WARN'}  branch '{safe(branch)}' is not issue/{n}-<slug>")
        failures += in_ci()
    else:
        files = changed_files()
        if files is None:
            problem = "cannot list the changed files (git diff origin/main...HEAD failed)"
        else:
            problem = review_required_problem(files, rows) or new_package_problem(n, files, rows)
        if problem:
            print(f"FAIL  {problem}")
            failures += 1
    print(f"{failures} gate(s) not passed" if failures else "all gates passed")
    return 1 if failures else 0


# a change to agent write lanes, agent tools, the hooks or the permission settings always needs
# a threat delta (G3) and a diff review (G6), whatever the change class. Checked on the issue's own
# branch against its diff, so it runs in CI's claude-config job for every issue PR.
# includes the checkers themselves, every file under hooks/ and agents/ (nested too),
# and the CI workflows that run these checks. Every module in scripts/, since any of
# them can shadow an import of lint.py, gates.py or roster.py; the fixtures that state
# what the Docker data must never allow, and the issue skill (gates, generated G4 routing).
# This core part is a literal and the floor; it holds .claude/project.json
# itself, so no edit of that file can drop its own review. The signers file that lock.signers_file names is added
# to the floor when the pattern is compiled (below). The project part, the project's own commit-message check, build
# guards, packaging metadata, release and handover tools, is gates.review_required_paths in .claude/project.json.
CORE_REVIEW_REQUIRED = [
    # The floor: a project's gates.review_required_paths adds to it and can never remove from it. It holds the
    # agent and hook machinery, the settings, the lock and pack data, the addenda that steer agents, and the
    # workflows that run these checks. Rulesets and the Dependabot file are the project's own additions.
    r"\.claude/(boundaries\.json|settings\.json|settings\.local\.json|base-settings\.json|project\.json|core\.lock\.json|core\.manifest"
    r"|agents/.+|addenda/.+|commands/.+|rules/.+|output-styles/.+|hooks/.+|packs/.+|scripts/[^/]+\.py"
    # every test of the hooks and checkers, not only some of them
    r"|tests/.+|skills/.+)",
    # CLAUDE.md at any depth: it steers every agent, the security-reviewer included
    r"(.+/)?CLAUDE(\.local)?\.md",
    r"\.github/workflows/[^/]+\.ya?ml",
    r"\.github/CODEOWNERS",
    r"\.mcp\.json",
    # a path a Windows or macOS checkout can write over another file (an 8.3 short name such as CLAUDE~1 or GITHUB~1,
    # or a code point macOS ignores in names): it could replace any file above, so it always needs the reviews
    r"(?:.+/)?[^/~.]{1,6}~[0-9]+(?:\.[^/.]{0,3})?(?:/.*)?",
    r".*[\u200c-\u200f\u202a-\u202e\u206a-\u206f\ufeff].*",
]
try:
    # Each item is wrapped on its own, so no item can reach into its neighbours; the match is case-insensitive
    # (a case variant of a path on a case-insensitive checkout is the same file), lets `.` match a newline, and
    # also covers a path with one trailing newline, so an exact file name cannot dodge the floor that way.
    # The floor also holds the signers file (lock.signers_file): its norma-release line decides which signed releases
    # the project installs, wherever the project keeps it and whatever its path classes are; and the revoked-keys
    # file (lock.revoked_file) when the project names one: emptying it would bring a revoked key back.
    TRUST_FILES = [PROJECT["lock"]["signers_file"]] + ([PROJECT["lock"]["revoked_file"]]
                                                       if "revoked_file" in PROJECT["lock"] else [])
    REVIEW_REQUIRED_PATHS = re.compile(
        "^(?:" + "|".join(f"(?:{p})" for p in CORE_REVIEW_REQUIRED + [re.escape(f) for f in TRUST_FILES]
                          + PROJECT["gates"]["review_required_paths"]) + r")\n?\Z",
        re.IGNORECASE | re.DOTALL)
except re.error:
    _stop(".claude/project.json gates.review_required_paths does not compile with the core list")


def current_branch() -> str:
    """The PR's head branch in CI (a PR checks out a detached merge commit, so `git rev-parse`
    returns `HEAD`; ci.yml exports HEAD_REF, GitHub sets GITHUB_HEAD_REF), else the local branch."""
    return os.environ.get("HEAD_REF") or os.environ.get("GITHUB_HEAD_REF") or git("rev-parse", "--abbrev-ref", "HEAD")


def review_required_problem(files: list[str], rows: dict[str, dict[str, str]]) -> str | None:
    touched = sorted(f for f in files if REVIEW_REQUIRED_PATHS.match(f))
    if not touched:
        return None
    skipped = [g for g in ("G3", "G6") if (rows.get(g) or {}).get("status") != "required" and
               (rows.get(g) or {}).get("status") != "passed"]
    if not skipped:
        if _g3_verdict(rows) == "N/A":  # the Light tier's G3 N/A never stands in for a threat delta here
            return ("G3 needs its threat delta, never N/A: this change touches a review-required path (agent lanes, "
                    f"tools, hooks, permissions or the signer line) ({', '.join(safe(t) for t in touched[:4])}"
                    f"{', ...' if len(touched) > 4 else ''})")
        return None
    return (f"{' and '.join(skipped)} must run: this change touches a review-required path (agent lanes, tools, hooks, permissions or the signer line) "
            f"({', '.join(safe(t) for t in touched[:4])}{', ...' if len(touched) > 4 else ''})")


# a new third-party package always gets G3, whatever the class (issue #32 item 18). Each changed package manifest,
# found by its own name (never by the project's dependency_paths, which may name fewer), is read whole at the merge
# base and in the working tree, and its declared packages are compared per ecosystem: a name the branch adds counts,
# a version bump does not. Lock files are not read: they list every indirect package too, and a locked restore fails
# on a package that only a lock file names. A requirements file that -r or -c includes under another name is not read.
MANIFEST_NAMES = re.compile(r"(?:^|/)(?:package\.json|pyproject\.toml|requirements[^/]*\.(?:txt|in)|packages\.config"
                            r"|[^/]+\.(?:csproj|fsproj|vbproj|props|targets))$", re.IGNORECASE)
MANIFEST_MAX_BYTES = 2_000_000
_XML_PACKAGE = re.compile(r"<\s*(?:PackageReference|PackageVersion|GlobalPackageReference|PackageDownload)\b[^>]*?\b(?:Include|Update)"
                          r"\s*=\s*([\"'])(.*?)\1|<\s*package\b[^>]*?\bid\s*=\s*([\"'])(.*?)\3",
                          re.IGNORECASE | re.DOTALL)
_REQUIREMENT_NAME = re.compile(r"\s*([A-Za-z0-9][A-Za-z0-9._-]*)")
_EGG = re.compile(r"#egg=([A-Za-z0-9][A-Za-z0-9._-]*)")
_NPM_SECTIONS = ("dependencies", "devDependencies", "peerDependencies", "optionalDependencies")


def _python_name(requirement: str) -> str:
    """A requirement's package name (`httpx[cli]>=0.27` gives httpx), or the whole text for a URL one."""
    egg = _EGG.search(requirement)
    if egg:
        return egg.group(1).lower().replace("_", "-")
    if "://" in requirement.split("@")[0] or requirement.lstrip().startswith(("git+", "http://", "https://", ".", "/",
                                                                              "file:")):
        return "url:" + requirement.strip()
    m = _REQUIREMENT_NAME.match(requirement)
    return m.group(1).lower().replace("_", "-") if m else ""


def declared_packages(path: str, data: bytes) -> set[tuple[str, str]] | None:
    """(ecosystem, name) for each package a manifest declares; None when the file cannot be read as its kind."""
    if len(data) > MANIFEST_MAX_BYTES:
        return None
    text = data.decode("utf-8-sig", errors="replace")
    name = path.rsplit("/", 1)[-1].lower()
    found: set[tuple[str, str]] = set()
    if name == "package.json":
        try:
            doc = json.loads(text)
        except (ValueError, RecursionError):
            return None
        for section in _NPM_SECTIONS:
            if isinstance(doc, dict) and isinstance(doc.get(section), dict):
                found |= {("npm", key.lower()) for key in doc[section] if isinstance(key, str)}
        return found
    if name == "pyproject.toml":
        try:
            doc = tomllib.loads(text)
        except (ValueError, RecursionError):
            return None
        project, tool = doc.get("project", {}), doc.get("tool", {})
        if not isinstance(project, dict) or not isinstance(tool, dict) \
                or not isinstance(doc.get("dependency-groups", {}), dict):
            return None  # not the shape a package manager reads: fail closed, never a traceback
        strings = list(project.get("dependencies", []) if isinstance(project, dict) else [])
        for table in ((project or {}).get("optional-dependencies", {}), doc.get("dependency-groups", {})):
            for group in (table.values() if isinstance(table, dict) else []):
                strings += group if isinstance(group, list) else []
        uv = tool.get("uv", {}) if isinstance(tool, dict) else {}
        strings += uv.get("dev-dependencies", []) if isinstance(uv, dict) else []
        found |= {("python", n) for s in strings if isinstance(s, str) and (n := _python_name(s))}
        poetry = tool.get("poetry", {}) if isinstance(tool, dict) else {}
        tables = [poetry.get("dependencies"), poetry.get("dev-dependencies")] if isinstance(poetry, dict) else []
        for group in (poetry.get("group", {}).values() if isinstance(poetry, dict) and isinstance(poetry.get("group"),
                                                                                                    dict) else []):
            if not isinstance(group, dict):
                return None
            tables.append(group.get("dependencies"))
        for table in tables:
            if isinstance(table, dict):
                found |= {("python", k.lower().replace("_", "-")) for k in table if isinstance(k, str)
                          and k.lower() != "python"}
        return found
    if name.startswith("requirements"):
        for raw in text.splitlines():
            line = raw.split(" #", 1)[0].strip()
            if not line or line.startswith("#") or (line.startswith("-") and not line.startswith(("-e ", "--editable"))):
                continue
            requirement = line.split(None, 1)[1] if line.startswith(("-e ", "--editable")) and " " in line else line
            if n := _python_name(requirement):
                found.add(("python", n))
        return found
    for m in _XML_PACKAGE.finditer(text):
        for item in (m.group(2) or m.group(4) or "").split(";"):  # Include="A;B" names two packages
            if item.strip():
                found.add(("nuget", item.strip().lower()))
    return found


def added_packages(files: list[str]) -> list[str] | None:
    """The packages the branch's manifests declare and its merge base with origin/main's did not, per ecosystem;
    None when the base or a manifest cannot be read (never a vacuous pass)."""
    paths = [f for f in files if MANIFEST_NAMES.search(f) and "node_modules/" not in f]
    if not paths:
        return []
    base = git("merge-base", "origin/main", "HEAD")
    if not re.fullmatch(r"[0-9a-f]{40,64}", base):
        return None
    before: set[tuple[str, str]] = set()
    after: set[tuple[str, str]] = set()
    for path in paths:
        try:
            old = subprocess.run(["git", "cat-file", "blob", f"{base}:{path}"], cwd=ROOT, capture_output=True,
                                 check=False)
        except OSError:
            return None
        if old.returncode == 0:
            before |= declared_packages(path, old.stdout) or set()  # unreadable at the base: every name counts
        target = ROOT / path
        if target.is_file():
            try:
                new = declared_packages(path, target.read_bytes())
            except OSError:
                return None
            if new is None:
                return None
            after |= new
    return sorted(f"{name} ({ecosystem})" for ecosystem, name in after - before)


def _g3_verdict(rows: dict[str, dict[str, str]]) -> str | None:
    """The verdict of G3's own line, when its row is required and its artifact is in the owner's lane and readable."""
    g3 = rows.get("G3") or {}
    artifact = g3.get("artifact", "")
    if g3.get("status") != "required" or not artifact or artifact_path_problem(artifact, "G3"):
        return None
    try:
        lines = verdict_lines((ROOT / artifact).read_text(encoding="utf-8-sig"), "G3")
    except (OSError, UnicodeDecodeError):
        return None
    return lines[0].group(2) if len(lines) == 1 else None


def new_package_problem(n: int, files: list[str], rows: dict[str, dict[str, str]]) -> str | None:
    packages = added_packages(files)
    if packages is None:
        return ("cannot read a package manifest at the merge base with origin/main or in the working tree, so a new "
                "package cannot be told")
    if not packages:
        return None
    named = ", ".join(safe(p)[:60] for p in packages[:4]) + (", ..." if len(packages) > 4 else "")
    if (rows.get("G3") or {}).get("status") != "required":
        return f"G3 must run: this change adds a third-party package ({named}); set its row to required"
    if _g3_verdict(rows) == "N/A":
        return f"G3 needs its threat delta, never N/A: this change adds a third-party package ({named})"
    return None


def safe(text: str) -> str:
    """Control and other non-printable characters escaped, so a branch or file name cannot hide or fake output
    lines."""
    return "".join(ch if ch.isprintable() else f"\\x{ord(ch):02x}" for ch in text)


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=False).stdout.strip()


def git_rc(*args: str) -> int:
    """The exit code, which git() drops. 128 when git itself cannot run."""
    try:
        return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=False).returncode
    except OSError:
        return 128


def in_ci() -> bool:
    return os.environ.get("GITHUB_ACTIONS") == "true"


def changed_files() -> list[str] | None:
    """None when the diff cannot be computed (a shallow clone or a missing
    origin/main must not pass the review-required check vacuously)."""
    git("fetch", "--quiet", "origin")
    if git_rc("diff", "--quiet", "origin/main...HEAD") not in (0, 1):
        return None
    # committed, uncommitted and untracked files by name, as prepush.py lists them.
    # --no-renames: a moved hook or test must count by its old path too; -z: no quoted paths.
    # Parsing `git status --porcelain` after git() strips the output cut the first character of the
    # first path (" M .claude/x" -> "claude/x").
    names: set[str] = set()
    for args in (("diff", "--name-only", "--no-renames", "-z", "origin/main...HEAD"),
                 ("diff", "--name-only", "--no-renames", "-z", "HEAD"),
                 ("ls-files", "-z", "--others", "--exclude-standard")):
        paths = git_paths(*args)
        if paths is None:  # a listing that fails makes the answer unknown, never an empty change
            return None
        names.update(paths)
    return sorted(names)


def git_paths(*args: str) -> list[str] | None:
    """NUL-separated path output of git, unstripped so no path loses a character, and decoded as UTF-8 (the locale
    codec, cp1252 on Windows, garbles non-ASCII paths); a byte that is not UTF-8 is replaced, never fatal. None when
    git fails or cannot run."""
    try:
        run = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8",
                             errors="replace", check=False)
    except OSError:
        return None
    if run.returncode != 0:
        return None
    return [p for p in run.stdout.split("\0") if p]


def classify(files: list[str]) -> str:
    if not files:
        return "unknown"
    if all(DOCS_ONLY.search(f) for f in files):
        return "docs-only"
    if all(DEPENDENCY.search(f) for f in files):
        return "dependency"
    if all(DOCS_ONLY.search(f) or CI_TOOLING.search(f) or DEPENDENCY.search(f) for f in files):
        return "ci-tooling"
    return "feature"


def init(n: int, title: str, cls: str, kind: str = "", phase: str = "", epic: str = "", tier: str = "full") -> int:
    if tier not in TIERS:
        print("--tier must be light or full (the tier the owner approved at G0; the issue skill's Tiers)", file=sys.stderr)
        return 2
    if epic and not EPIC.fullmatch(epic):
        print("--epic must be an epic id such as EP-01 (leave it out for an issue that serves no epic)", file=sys.stderr)
        return 2
    if cls not in CLASSES:
        print(f"--class must be one of: {', '.join(CLASSES)}", file=sys.stderr)
        return 2
    if "labels" in PROJECT:  # the delivery label set: the status pack reads kind and phase from the manifest
        if kind not in claudecfg.LABEL_KINDS:
            print(f"--kind must be one of: {', '.join(claudecfg.LABEL_KINDS)} (the label set is adopted)", file=sys.stderr)
            return 2
        if not claudecfg.PHASE_LABEL.fullmatch(f"phase:{phase}"):
            print("--phase must be the phase id of the issue's phase: label, such as 1 or 0.4", file=sys.stderr)
            return 2
    elif kind or phase:
        print("--kind and --phase need the label set (a labels section in .claude/project.json)", file=sys.stderr)
        return 2
    path = manifest_path(n)
    if path.exists():
        print(f"{path.relative_to(ROOT)} already exists; resume with: python .claude/scripts/gates.py {n} --next")
        return 1
    branch = git("rev-parse", "--abbrev-ref", "HEAD")
    lines = [
        f"# Pipeline – issue #{n}: {title}",
        "",
        f"- Branch: `{branch}`",
        f"- Change class: {cls}",
        f"- Tier: {tier}",
        *([f"- Kind: {kind}", f"- Phase: {phase}"] if kind else []),
        *([f"- Epic: {epic}"] if epic else []),
        f"- Created: {date.today().isoformat()}",
        "- Done when (from the issue): <copy the issue's \"Done when\" line>",
        "",
        "Gate rules and statuses: `.claude/skills/issue/SKILL.md`. Check with `python .claude/scripts/gates.py "
        f"{n}`.",
        "",
        "| Gate | Owner | Status | Artifact | Note |",
        "| --- | --- | --- | --- | --- |",
    ]
    for gate, owner, _ in GATES:
        if gate == "G0":
            lines.append(f"| G0 | {owner} | passed | docs/ai/pipeline/{n}.md | issue open; branch {branch}; class {cls} |")
        elif gate in CLASSES[cls]:
            lines.append(f"| {gate} | {owner} | required | | |")
        else:
            lines.append(f"| {gate} | {owner} | skipped | | reason: class {cls}; approved: PENDING |")
    lines += ["", "## G4 evidence", "", G7_HEADING, "", "Docs: <files touched, or none: reason>", ""]
    PIPELINE.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"created {path.relative_to(ROOT)}")
    return 0


def main(argv: list[str]) -> int:
    if argv[:1] == ["classify"]:
        files = changed_files()
        print(classify(files))
        return 0
    if argv[:1] == ["init"] and len(argv) >= 2 and argv[1].isdigit():
        title = argv[argv.index("--title") + 1] if "--title" in argv else ""
        cls = argv[argv.index("--class") + 1] if "--class" in argv else ""
        kind = argv[argv.index("--kind") + 1] if "--kind" in argv[:-1] else ""
        phase = argv[argv.index("--phase") + 1] if "--phase" in argv[:-1] else ""
        epic = argv[argv.index("--epic") + 1] if "--epic" in argv[:-1] else ""
        tier = argv[argv.index("--tier") + 1] if "--tier" in argv[:-1] else ""
        return init(int(argv[1]), title, cls, kind, phase, epic, tier)
    if argv == ["bot"]:
        return bot_check()
    if argv and argv[0].isdigit():
        return check(int(argv[0]), "--next" in argv)
    print(__doc__, file=sys.stderr)
    return 2


def bot_check() -> int:
    """`gates.py bot`: a dependency bot's pull request has no issue and no manifest. It is a bot's pull request only
    when its branch is under an entry's prefix in gates.dependency_bot_branches AND both its author and the run's
    actor are that entry's login: CI passes the author in PR_AUTHOR (from the pull request event) and the actor in
    PR_ACTOR (github.actor), through `env:`. Anyone can name a branch dependabot/..., only the bot opens a pull request
    as dependabot[bot], and a commit someone else pushes to the bot's branch starts the run with their login as actor.
    It passes only when every changed file is in the dependency class and none is a review-required path; anything
    else needs an issue of its own.
    Exit 0 = pass, 1 = fail, 2 = not a bot's pull request (the issue gate applies)."""
    branch = current_branch()
    author = os.environ.get("PR_AUTHOR", "")
    actor = os.environ.get("PR_ACTOR", "")
    entries = [b for b in PROJECT["gates"]["dependency_bot_branches"] if branch.startswith(b["branch"])]
    if not entries:
        print(f"bot: branch '{safe(branch)}' is not under a prefix in gates.dependency_bot_branches", file=sys.stderr)
        return 2
    for label, name, value in (("pull request's author", "PR_AUTHOR", author), ("run's actor", "PR_ACTOR", actor)):
        if not value or value != entries[0]["login"]:
            print(f"bot: the {label} ({f'not given in {name}' if not value else safe(value)}) is not the bot that "
                  f"branch prefix names; the issue gate applies", file=sys.stderr)
            return 2
    files = changed_files()
    if not files:
        print("FAIL  bot: the changed files cannot be listed, or there are none")
        return 1
    cls = classify(files)
    if cls != "dependency":
        others = [f for f in files if not DEPENDENCY.search(f)]
        print(f"FAIL  bot: the change is {cls}, not dependency-only "
              f"({', '.join(safe(f) for f in others[:4])}{', ...' if len(others) > 4 else ''}); open an issue for it")
        return 1
    problem = review_required_problem(files, {})
    if problem:
        print(f"FAIL  bot: {problem}; open an issue for it")
        return 1
    print(f"PASS  bot: {len(files)} dependency file(s), none review-required")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
