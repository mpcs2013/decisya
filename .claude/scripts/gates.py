#!/usr/bin/env python3
"""Pipeline gate checker for one GitHub issue.

Usage (from the repository root):
  python .claude/scripts/gates.py <n>              check every gate; exit 1 if any is not passed
  python .claude/scripts/gates.py <n> --next       print the first gate that is not passed ("done" if none)
  python .claude/scripts/gates.py init <n> --title "<title>" --class <class>
                                                   create docs/ai/pipeline/<n>.md from the template
  python .claude/scripts/gates.py classify         print the change class of the current branch

The manifest docs/ai/pipeline/<n>.md holds one table row per gate:
  | G1 | product-owner | required | docs/requirements/phase-0/x.md | |
Status values:
  required  an agent gate (G1, G2, G3, G5, G6): its artifact must exist and contain
            <!-- gate: G1 | verdict: PASS|PASS-WITH-NOTES|N/A | issue: #<n> -->
            (N/A also needs "reason: ..." inside the comment); BLOCK never passes
  passed    an orchestrator gate (G0, G4, G7) the main session completed; Note holds the evidence
  skipped   Note must contain "reason: ..." and "approved: Marco YYYY-MM-DD"
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PIPELINE = ROOT / "docs" / "ai" / "pipeline"

GATES = [
    ("G0", "orchestrator", "Issue open, branch issue/<n>-<slug>, manifest, change class"),
    ("G1", "product-owner", "Requirements with Gherkin acceptance criteria"),
    ("G2", "architect", "Architecture note, or N/A with a reason"),
    ("G3", "security-reviewer", "Threat model (threat delta)"),
    ("G4", "implementer (by path: backend/platform/identity/frontend-dev)", "Code and tests; build and tests green (re-run by the orchestrator)"),
    ("G5", "test-engineer", "Traceability: every acceptance criterion mapped to a test"),
    ("G6", "security-reviewer", "Security diff review"),
    ("G7", "orchestrator", "PR body: Closes #<n>, one line per new package, Docs: line"),
]
ORCHESTRATOR_GATES = {"G0", "G4", "G7"}

# Gates run per change class; the others start as "skipped" pending Marco's approval.
CLASSES = {
    "feature": {"G0", "G1", "G2", "G3", "G4", "G5", "G6", "G7"},
    "docs-only": {"G0", "G7"},
    "ci-tooling": {"G0", "G3", "G6", "G7"},
    "dependency": {"G0", "G6", "G7"},
}

# Paths that count as docs for the change class. Wider than the "not code" list in
# ci.yml's changes job: since #77, CI treats .claude/ and .vscode/ as code.
DOCS_ONLY = re.compile(r"^docs/|\.md$|^\.claude/|^\.vscode/|^LICENSE$|^\.github/(ISSUE_TEMPLATE/|dependabot\.yml$)")
CI_TOOLING = re.compile(r"^\.github/|^\.devcontainer/|^Directory\.Build\.props$|^global\.json$|^dotnet-tools\.json$|^\.config/|^\.pre-commit-config\.yaml$|^\.gitattributes$|^\.editorconfig$|^BannedSymbols\.txt$")
DEPENDENCY = re.compile(r"^Directory\.Packages\.props$|(^|/)package(-lock)?\.json$")

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


def check_gate(n: int, gate: str, row: dict[str, str] | None) -> tuple[bool, str]:
    if row is None:
        return False, "no row in manifest"
    status, artifact, note = row["status"], row["artifact"], row["note"]
    if status == "skipped":
        if re.search(r"reason:\s*\S", note) and re.search(r"approved:\s*Marco\s+\d{4}-\d{2}-\d{2}", note):
            return True, f"skipped ({note})"
        return False, "skipped without 'reason:' and 'approved: Marco YYYY-MM-DD'"
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
    # G4-39-19: any other complete comment for the same gate and issue, even fenced, indented,
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


# #76 D4: the G7 PR body names the docs it updated. Required from issue #76 on, and for any issue
# whose manifest is not on origin/main yet (every issue branch adds its own), so leaving the line
# out never switches the rule off. Earlier manifests (#14 to #74, all on main) are unaffected.
DOCS_LINE_FROM = 76
G7_HEADING = "## G7 PR body draft"
DOCS_LINE = re.compile(r"^(?:\*\*)?Docs:(?:\*\*)?\s+(.+?)\s*$")
DOCS_PATH = re.compile(r"[A-Za-z0-9._/@+-]+")


def docs_required(n: int) -> tuple[bool, str | None]:
    """(required, failure). Condition 2 resolves origin/main by exit code and fails in CI when it
    cannot (G4-76-19; a CI check must not degrade to a warning, G6-74-04). Never uses the branch."""
    if n >= DOCS_LINE_FROM:
        return True, None
    if git_rc("rev-parse", "--verify", "--quiet", "origin/main^{commit}") != 0:
        if os.environ.get("GITHUB_ACTIONS") == "true":
            return True, "G7: origin/main is not available in CI, so the Docs rule cannot be decided"
        print(f"WARN  origin/main not available; Docs rule applies to n >= {DOCS_LINE_FROM} only")
        return False, None
    return git_rc("cat-file", "-e", f"origin/main:docs/ai/pipeline/{int(n)}.md") != 0, None


_FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")


def _fence_step(line: str, fence: str | None) -> tuple[str | None, bool]:
    """(open fence after this line, whether the line is a fence line), as CommonMark reads it: a
    fence closes only on the same character, at least as long, with nothing after it (G6-76-04)."""
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
    """The G7 section's Docs line, by string rules only: nothing named in it is opened or resolved
    (G4-76-20, 21)."""
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
        if "<!--" in line:  # G6-76-04: a comment opening anywhere on the line, indented or mid-line
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
# G4-39-22: an agent gate's artifact must lie in its owner's write lane (.claude/boundaries.json).
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
    """G4-39-21/22: relative POSIX path inside the repository, in the owner agent's lane."""
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
    issue; see check_gate). Deviation from G4-39-18 "line 1 only", recorded in the #39 manifest:
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
        print(f"WARN  branch '{branch}' is not issue/{n}-<slug>")
    else:
        problem = review_required_problem(changed_files(), rows)
        if problem:
            print(f"FAIL  {problem}")
            failures += 1
    print(f"{failures} gate(s) not passed" if failures else "all gates passed")
    return 1 if failures else 0


# #74: a change to agent write lanes, agent tools, the hooks or the permission settings always needs
# a threat delta (G3) and a diff review (G6), whatever the change class. Checked on the issue's own
# branch against its diff, so it runs in CI's claude-config job for every issue PR.
# G6-74-05: includes the checkers themselves, every file under hooks/ and agents/ (nested too),
# and the CI workflows that run these checks. #76 G4-76-22: every module in scripts/, since any of
# them can shadow an import of lint.py, gates.py or roster.py; G4-76-23: the fixtures that state
# what the Docker data must never allow, and the issue skill (gates, generated G4 routing).
REVIEW_REQUIRED_PATHS = re.compile(
    r"^(\.claude/(boundaries\.json|settings\.json|agents/.+|hooks/.+|scripts/[^/]+\.py"
    r"|tests/test_(agent_roster|hooks)\.py|skills/issue/SKILL\.md|skills/[^/]+/(scripts|assets)/.+)"
    r"|\.github/workflows/[^/]+\.ya?ml"
    # #80 G4-80-09: the main ruleset as code and its drift test
    r"|\.github/rulesets/.+\.json|\.claude/tests/test_ruleset\.py"
    r"|\.github/dependabot\.yml"  # G6-80-12: its commit-message and grouping decide what passes the gate
    r"|\.github/scripts/[^/]+\.py|\.github/image-scan/.+"  # #28: the dependency canary and the image-scan policy
    r"|\.github/release/.+|Directory\.Build\.targets|\.dockerignore|release-please-config\.json"  # #119: release pins and image policy
    r"|\.github/zap/.+"  # #123: the ZAP pin, scope context, hook and exceptions
    # #77 G4-77-14: the realm guard (G4-17-12) and its shared case table; skill scripts and assets
    # above are executable (scaffold.py copies assets into src/).
    r"|tests/Decisya\.Identity\.Tests/(RealmGuard|RealmGuardTests)\.cs|tests/Decisya\.Identity\.Tests/realm-guard-cases\.json)$")


def current_branch() -> str:
    """The PR's head branch in CI (a PR checks out a detached merge commit, so `git rev-parse`
    returns `HEAD`; ci.yml exports HEAD_REF, GitHub sets GITHUB_HEAD_REF), else the local branch
    (G6-74-04)."""
    return os.environ.get("HEAD_REF") or os.environ.get("GITHUB_HEAD_REF") or git("rev-parse", "--abbrev-ref", "HEAD")


def review_required_problem(files: list[str], rows: dict[str, dict[str, str]]) -> str | None:
    touched = sorted(f for f in files if REVIEW_REQUIRED_PATHS.match(f))
    if not touched:
        return None
    skipped = [g for g in ("G3", "G6") if (rows.get(g) or {}).get("status") != "required" and
               (rows.get(g) or {}).get("status") != "passed"]
    if not skipped:
        return None
    return (f"{' and '.join(skipped)} must run: this change touches agent lanes, tools, hooks or permissions "
            f"({', '.join(touched[:4])}{', ...' if len(touched) > 4 else ''})")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=False).stdout.strip()


def git_rc(*args: str) -> int:
    """The exit code, which git() drops (G4-76-19). 128 when git itself cannot run."""
    try:
        return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=False).returncode
    except OSError:
        return 128


def changed_files() -> list[str]:
    git("fetch", "--quiet", "origin")
    names = set(git("diff", "--name-only", "origin/main...HEAD").splitlines())
    for line in git("status", "--porcelain").splitlines():
        names.add(line[3:].split(" -> ")[-1].strip('"'))
    return sorted(n for n in names if n)


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
    if argv and argv[0].isdigit():
        return check(int(argv[0]), "--next" in argv)
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
