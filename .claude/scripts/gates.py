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
    ("G4", "backend-dev / frontend-dev", "Code and tests; build and tests green (re-run by the orchestrator)"),
    ("G5", "test-engineer", "Traceability: every acceptance criterion mapped to a test"),
    ("G6", "security-reviewer", "Security diff review"),
    ("G7", "orchestrator", "PR body: Closes #<n>, one line per new package"),
]
ORCHESTRATOR_GATES = {"G0", "G4", "G7"}

# Gates run per change class; the others start as "skipped" pending Marco's approval.
CLASSES = {
    "feature": {"G0", "G1", "G2", "G3", "G4", "G5", "G6", "G7"},
    "docs-only": {"G0", "G7"},
    "ci-tooling": {"G0", "G3", "G6", "G7"},
    "dependency": {"G0", "G6", "G7"},
}

# Same "not code" list as the changes job in .github/workflows/ci.yml.
DOCS_ONLY = re.compile(r"^docs/|\.md$|^\.claude/|^\.vscode/|^LICENSE$|^\.github/(ISSUE_TEMPLATE/|dependabot\.yml$|workflows/claude-review\.yml$)")
CI_TOOLING = re.compile(r"^\.github/|^\.devcontainer/|^Directory\.Build\.props$|^global\.json$|^dotnet-tools\.json$|^\.config/|^\.pre-commit-config\.yaml$|^\.gitattributes$|^\.editorconfig$|^BannedSymbols\.txt$")
DEPENDENCY = re.compile(r"^Directory\.Packages\.props$|(^|/)package(-lock)?\.json$")

GATE_LINE = re.compile(r"<!--\s*gate:\s*(G\d)\s*\|\s*verdict:\s*([A-Z/-]+)\s*\|\s*issue:\s*#(\d+)(.*?)-->")
ROW = re.compile(r"^\|\s*(G\d)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|([^|]*)\|\s*$")


def manifest_path(n: int) -> Path:
    return PIPELINE / f"{n}.md"


def read_rows(n: int) -> dict[str, dict[str, str]]:
    rows = {}
    for line in manifest_path(n).read_text(encoding="utf-8").splitlines():
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
            return True, note
        return False, "not completed" if status != "passed" else "passed without evidence in Note"
    if status != "required":
        return False, f"unknown status '{status}'"
    if not artifact:
        return False, "MISSING: no artifact recorded"
    path = ROOT / artifact
    if not path.exists():
        return False, f"MISSING: {artifact} does not exist"
    found = verdict_lines(path, gate)
    if not found:
        return False, f"NO VERDICT: {artifact} has no '<!-- gate: {gate} | verdict: ... -->' line"
    if len(found) > 1:
        return False, f"AMBIGUOUS: {artifact} has {len(found)} verdict lines for {gate}; keep exactly one"
    m = found[0]
    verdict, issue, rest = m.group(2), int(m.group(3)), m.group(4)
    if issue != n:
        return False, f"verdict line names issue #{issue}, expected #{n}"
    if verdict in ("PASS", "PASS-WITH-NOTES"):
        return True, f"{verdict} ({artifact})"
    if verdict == "N/A":
        return (True, f"N/A ({artifact})") if "reason:" in rest else (False, "N/A without 'reason:'")
    return False, f"{verdict} ({artifact})"


def verdict_lines(path: Path, gate: str) -> list[re.Match[str]]:
    """Verdict lines for one gate: a line that starts with the comment, outside ``` fences.

    Quoted or example verdict lines (inside code blocks or mid-sentence) never count, so an
    artifact cannot pass by quoting someone else's PASS line.
    """
    found, fenced = [], False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.lstrip().startswith("```"):
            fenced = not fenced
            continue
        if fenced or not line.lstrip().startswith("<!--"):
            continue
        m = GATE_LINE.match(line.strip())
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
    branch = git("rev-parse", "--abbrev-ref", "HEAD")
    if not branch.startswith(f"issue/{n}-"):
        print(f"WARN  branch '{branch}' is not issue/{n}-<slug>")
    print(f"{failures} gate(s) not passed" if failures else "all gates passed")
    return 1 if failures else 0


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=False).stdout.strip()


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
    lines += ["", "## G4 evidence", "", "## G7 PR body draft", ""]
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
