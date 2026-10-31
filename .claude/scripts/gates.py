#!/usr/bin/env python3
"""Pipeline gate checker for one GitHub issue.


Usage (from the repository root):
  python .claude/scripts/gates.py <n>              check every gate; exit 1 if any is not passed
  python .claude/scripts/gates.py <n> --next       print the first gate that is not passed ("done" if none)
  python .claude/scripts/gates.py init <n> --title "<title>" --class <class>
                                                   create docs/ai/pipeline/<n>.md from the template
  python .claude/scripts/gates.py classify         print the change class of the current branch
  python .claude/scripts/gates.py bot              a dependency bot's branch (gates.dependency_bot_branches): pass
                                                   only for a dependency-only change with no review-required path

The manifest docs/ai/pipeline/<n>.md holds one table row per gate:
  | G1 | product-owner | required | <artifact path> | |
Status values:
  required  an agent gate (G1, G2, G3, G5, G6): its artifact must exist and contain
            <!-- gate: G1 | verdict: PASS|PASS-WITH-NOTES|N/A | issue: #<n> -->
            (N/A also needs "reason: ..." inside the comment); BLOCK never passes
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
    return f"implementer (by path: {', '.join(agents)})"


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
ROW = re.compile(r"^\|\s*(G\d)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|([^|]*)\|\s*$")


def manifest_path(n: int) -> Path:
    return PIPELINE / f"{n}.md"


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
        return False, f"NO VERDICT: {artifact} has no own-line '<!-- gate: {gate} | verdict: ... -->' comment (column 0, outside code blocks)"
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
    if verdict in ("PASS", "PASS-WITH-NOTES"):
        return True, f"{verdict} ({artifact})"
    if verdict == "N/A":
        return (True, f"N/A ({artifact})") if "reason:" in rest else (False, "N/A without 'reason:'")
    return False, f"{verdict} ({artifact})"


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
        fence, is_fence = _fence_step(line, fence)
        if is_fence or fence is not None:
            continue
        if in_comment:
            in_comment = "-->" not in line
            continue
        if "<!--" in line:  # a comment opening anywhere on the line, indented or mid-line
            in_comment = "-->" not in line.rsplit("<!--", 1)[1]
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
    ignored) is the comment, outside ``` fences. Indented, fenced, quoted or mid-sentence copies
    never count as the verdict (and make the artifact AMBIGUOUS if they name the same gate and
    issue; see check_gate). Deviation from "line 1 only":
    G1 and G5 share the requirements file, so each gate's line may sit anywhere on its own line.
    """
    found, fenced = [], False
    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            fenced = not fenced
            continue
        if fenced or not line.startswith("<!--"):
            continue
        m = GATE_LINE.fullmatch(line.rstrip())
        if m and m.group(1) == gate:
            found.append(m)
    return found


def check(n: int, next_only: bool) -> int:
    if not manifest_path(n).exists():
        print(f"No manifest docs/ai/pipeline/{n}.md. Create it with: python .claude/scripts/gates.py init {n} --title \"...\" --class <class>")
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
            problem = review_required_problem(files, rows)
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
    # the project installs, wherever the project keeps it and whatever its path classes are.
    REVIEW_REQUIRED_PATHS = re.compile(
        "^(?:" + "|".join(f"(?:{p})" for p in CORE_REVIEW_REQUIRED + [re.escape(PROJECT["lock"]["signers_file"])]
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
        return None
    return (f"{' and '.join(skipped)} must run: this change touches a review-required path (agent lanes, tools, hooks, permissions or the signer line) "
            f"({', '.join(safe(t) for t in touched[:4])}{', ...' if len(touched) > 4 else ''})")


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


def init(n: int, title: str, cls: str) -> int:
    if cls not in CLASSES:
        print(f"--class must be one of: {', '.join(CLASSES)}", file=sys.stderr)
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
        return init(int(argv[1]), title, cls)
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
