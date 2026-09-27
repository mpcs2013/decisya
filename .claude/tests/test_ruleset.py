"""#80: the `main` ruleset as code (.github/rulesets/main.json) agrees with the workflows
(docs/security/threat-models/main-ruleset.md, G4-80-01 to 07, 15).

Offline and pure (G4-80-07): it reads repository files only, never the live ruleset. Marco checks
the live state by hand (runbook, G4-80-11). Every red case below mutates an in-memory copy.
The workflow reader is deliberately small: it reads `on:` and `jobs:` by indentation and fails
closed on YAML it does not understand (anchors, aliases, merge keys, flow-style jobs, tabs).
"""
import copy
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RULESET = ROOT / ".github" / "rulesets" / "main.json"
WORKFLOWS = ROOT / ".github" / "workflows"

ACTIONS_APP = 15368  # the GitHub Actions app; checks from any other source do not count (G4-80-01)
FLOOR = {"build-test", "claude-config", "codeql", "realm-guard"}  # G4-80-02
# The only required job allowed a job-level `if` (G4-80-04); skipped counts as passed.
SKIP_SAFE = {"codeql": "GHAS: runs only on a public repository and on code changes (#28)"}
# G6-80-13: the only condition text the skip-safe job's if may carry.
SKIP_SAFE_CONDITION = {"codeql": "github.event.repository.visibility == 'public'"}
NEVER_CONDITIONAL = {"realm-guard", "claude-config"}  # G6-77-05
INPUT_KEYS = {"name", "target", "enforcement", "bypass_actors", "conditions", "rules"}


class WorkflowParseError(ValueError):
    pass


# GitHub's job-level keys; anything else at a job's first indent level fails closed (G6-80-01).
JOB_KEYS = {"name", "needs", "if", "runs-on", "permissions", "environment", "concurrency", "outputs", "env",
            "defaults", "steps", "timeout-minutes", "strategy", "continue-on-error", "container", "services",
            "uses", "with", "secrets"}


def _comment(line: str) -> bool:
    return line.lstrip().startswith("#")


def parse_workflow(text: str) -> dict:
    """{'on': {trigger: block text}, 'jobs': {id: {key: value text}}} from a GitHub workflow."""
    if "\t" in text:
        raise WorkflowParseError("tab character")
    lines = text.splitlines()
    for line in lines:
        code = line.split(" #", 1)[0]
        if re.search(r":\s+[&*][A-Za-z_][\w-]*\s*$|^\s*-\s+[&*][A-Za-z_]|<<:", code):
            raise WorkflowParseError(f"YAML anchor, alias or merge key: {line.strip()[:60]}")

    def block(start: int, indent: int) -> list[str]:
        out = []
        for line in lines[start + 1:]:
            if line.strip() and not line.lstrip().startswith("#") and len(line) - len(line.lstrip()) <= indent:
                break
            out.append(line)
        return out

    on, jobs = None, None
    for i, line in enumerate(lines):
        m = re.match(r"^(on|\"on\"|'on'|true|jobs):(.*)$", line)
        if not m:
            continue
        key, rest = m.group(1).strip("\"'"), m.group(2).split(" #", 1)[0].strip()
        if key in ("on", "true"):
            on = {}
            if rest.startswith("["):
                on = {t.strip(): "" for t in rest.strip("[]").split(",") if t.strip()}
            elif rest:
                on = {rest: ""}
            else:
                body = block(i, 0)
                for j, sub in enumerate(body):
                    tm = re.match(r"^  ([A-Za-z_]+):(.*)$", sub)
                    if tm:
                        nested = []
                        for more in body[j + 1:]:
                            if re.match(r"^  \S", more) and not _comment(more):  # G6-80-02
                                break
                            nested.append(more)
                        on[tm.group(1)] = tm.group(2) + "\n" + "\n".join(nested)
        else:
            if rest:
                raise WorkflowParseError("flow-style jobs")
            jobs = {}
            body = block(i, 0)
            current, expect_key = None, False
            for j, sub in enumerate(body):
                if not sub.strip() or _comment(sub):
                    continue  # comment-only lines carry no structure
                indent = len(sub) - len(sub.lstrip())
                jm = re.match(r"^  ([A-Za-z_][\w-]*):\s*(#.*)?$", sub)
                if jm:
                    current, expect_key = jobs.setdefault(jm.group(1), {}), True
                    continue
                # G6-80-01: fail closed on any job-level line the reader does not understand
                if indent < 4 or current is None:
                    raise WorkflowParseError(f"unexpected line in jobs: {sub.strip()[:60]}")
                if expect_key and indent != 4:
                    raise WorkflowParseError(f"a job's first key must be indented 4 spaces: {sub.strip()[:60]}")
                expect_key = False
                if indent > 4:
                    continue  # part of the previous key's nested value
                km = re.match(r"^    ([A-Za-z_][\w-]*):( |$)(.*)$", sub)
                if not km or km.group(1) not in JOB_KEYS:
                    raise WorkflowParseError(f"unknown or malformed job key: {sub.strip()[:60]}")
                value = km.group(3).split(" #", 1)[0].strip()
                nested = []
                for more in body[j + 1:]:
                    if re.match(r"^ {0,4}\S", more) and not _comment(more):  # G6-80-02
                        break
                    nested.append(more)
                current[km.group(1)] = (value + "\n" + "\n".join(nested)).strip()
    if on is None or jobs is None:
        raise WorkflowParseError("no top-level on: or jobs:")
    return {"on": on, "jobs": jobs}


def needs_of(job: dict) -> list[str]:
    raw = job.get("needs", "")
    if not raw:
        return []
    if raw.startswith("["):
        return [n.strip() for n in raw.split("\n", 1)[0].strip("[]").split(",") if n.strip()]
    if "\n" in raw or raw.startswith("-"):
        return [re.sub(r"^\s*-\s*", "", ln).strip() for ln in raw.splitlines() if ln.strip().startswith("-")]
    return [raw]


def ruleset_problems(rs: dict) -> list[str]:
    """G4-80-01 and 02: the file is a valid, fully pinned PUT body."""
    p = []
    extra = set(rs) - INPUT_KEYS
    if extra:
        p.append(f"G4-80-01: read-only or unknown keys {sorted(extra)} (only {sorted(INPUT_KEYS)})")
    if "bypass_actors" not in rs or rs.get("bypass_actors") != []:
        p.append("G4-80-01: bypass_actors must be present and exactly []")
    if rs.get("enforcement") != "active":
        p.append(f"G4-80-01: enforcement must be 'active', not {rs.get('enforcement')!r}")
    if rs.get("target") != "branch" or rs.get("conditions") != {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}}:
        p.append("G4-80-01: target must be branch and conditions exactly ~DEFAULT_BRANCH")
    rules = {r.get("type"): r.get("parameters", {}) for r in rs.get("rules", [])}
    for required in ("deletion", "non_fast_forward", "pull_request", "required_status_checks"):
        if required not in rules:
            p.append(f"G4-80-01: rule {required} is missing")
    pr = rules.get("pull_request", {})
    if pr and (pr.get("required_approving_review_count") != 0 or pr.get("required_review_thread_resolution") is not True
               or pr.get("dismiss_stale_reviews_on_push") is not True):
        p.append("G4-80-01: pull_request needs 0 approvals (solo author), thread resolution and stale-review dismissal")
    rsc = rules.get("required_status_checks", {})
    if rsc:
        if rsc.get("strict_required_status_checks_policy") is not True:
            p.append("G4-80-01: strict_required_status_checks_policy must be true")
        if rsc.get("do_not_enforce_on_create", False) is not False:
            p.append("G4-80-01: do_not_enforce_on_create must be false")
        for check in rsc.get("required_status_checks", []):
            if check.get("integration_id") != ACTIONS_APP:
                p.append(f"G4-80-01: {check.get('context')} must carry integration_id {ACTIONS_APP} (GitHub Actions)")
    missing = FLOOR - set(contexts(rs))
    if missing:
        p.append(f"G4-80-02: floor contexts missing from the ruleset: {sorted(missing)}")
    return p


def contexts(rs: dict) -> list[str]:
    for rule in rs.get("rules", []):
        if rule.get("type") == "required_status_checks":
            return [c.get("context") for c in rule.get("parameters", {}).get("required_status_checks", [])]
    return []


def workflow_problems(rs: dict, workflows: dict[str, str]) -> list[str]:
    """G4-80-03 to 06: every required context is one real, unconditional-enough PR job."""
    p, parsed = [], {}
    for name, text in workflows.items():
        try:
            parsed[name] = parse_workflow(text)
        except WorkflowParseError as exc:
            p.append(f"G4-80-04: {name} cannot be read safely ({exc}); simplify the YAML")
        # G4-80-06: nothing may forge a status, and no privileged PR trigger
        if re.search(r"""\b["']?(checks|statuses)["']?\s*:\s*["']?write\b|\bwrite-all\b""", text):  # G6-80-03: quoted too
            p.append(f"G4-80-06: {name} grants checks/statuses write or write-all (a job could forge a required status)")
        if not re.search(r"^permissions:", text, re.MULTILINE):
            p.append(f"G4-80-06: {name} has no top-level permissions: and would inherit the repository default (G6-80-03)")
        if re.search(r"\bpull_request_target\b", text):
            p.append(f"G4-80-06: {name} triggers on pull_request_target")
    required = contexts(rs)
    check_names = {}
    for name, wf in parsed.items():
        for job_id, job in wf["jobs"].items():
            if "${{" in job.get("name", ""):  # G6-80-04: a rendered name could equal a required check
                p.append(f"G4-80-05: {name}:{job_id} has an expression in name:, which can render to a required check name")
            check_names.setdefault(job.get("name", job_id).strip("\"'") or job_id, []).append(f"{name}:{job_id}")
    pr_jobs = {}
    for name, wf in parsed.items():
        if "pull_request" not in wf["on"]:
            continue
        trigger = wf["on"]["pull_request"]
        for job_id, job in wf["jobs"].items():
            pr_jobs.setdefault(job_id, []).append((name, job, trigger))
    for context in required:
        owners = check_names.get(context, [])
        if len(owners) != 1:  # G4-80-05
            p.append(f"G4-80-05: required check {context} is reported by {len(owners)} jobs ({owners}); exactly one")
        found = pr_jobs.get(context, [])
        if len(found) != 1:  # G4-80-04
            p.append(f"G4-80-04: required check {context} has {len(found)} jobs with that id in pull_request workflows; "
                     "a renamed or removed job leaves the check waiting forever")
            continue
        name, job, trigger = found[0]
        if job.get("name", context).strip("\"'") != context:
            p.append(f"G4-80-04: {context} sets name: {job['name']}; the check name must equal the job id")
        if "strategy" in job and "matrix" in job["strategy"]:
            p.append(f"G4-80-04: {context} uses a matrix (the check name changes per leg)")
        if "uses" in job:
            p.append(f"G4-80-04: {context} is a reusable-workflow job")
        condition = job.get("if")
        if condition is not None:
            if re.fullmatch(r"(\$\{\{\s*)?false(\s*\}\})?", condition.split("\n", 1)[0].strip()):
                p.append(f"G4-80-04: {context} has a constant-false if (it never runs, and skipped counts as passed)")
            elif context not in SKIP_SAFE:
                p.append(f"G4-80-04: {context} has a job-level if; only {sorted(SKIP_SAFE)} may (skipped counts as passed)")
            elif SKIP_SAFE_CONDITION[context] not in condition:  # G6-80-13: the skip-safe reason is pinned
                p.append(f"G4-80-04: {context}'s if no longer contains {SKIP_SAFE_CONDITION[context]!r}")
        if "continue-on-error" in job:  # G6-80-13: a failing job would still report success
            p.append(f"G4-80-04: {context} sets continue-on-error")
        if context in NEVER_CONDITIONAL and ("needs" in job or condition is not None):
            p.append(f"G4-80-04: {context} must have neither needs nor if (G6-77-05)")
        if re.search(r"\b(paths|paths-ignore|branches|branches-ignore|types)\b", trigger):  # types: G6-80-13
            p.append(f"G4-80-04: {name}'s pull_request trigger is filtered; required checks would wait forever")
        # G4-80-03 (option a): a skipped dependency must not turn into a green merge
        seen, todo = set(), needs_of(job)
        while todo:
            dep = todo.pop()
            if dep in seen:
                continue
            seen.add(dep)
            if dep not in required:
                p.append(f"G4-80-03: {context} needs {dep}, which is not required; if {dep} fails, {context} is skipped and "
                         "a skipped required check counts as passed")
            for _, dep_job, _ in pr_jobs.get(dep, []):
                todo.extend(needs_of(dep_job))
    return p


def real_inputs() -> tuple[dict, dict[str, str]]:
    rs = json.loads(RULESET.read_text(encoding="utf-8"))
    workflows = {p.name: p.read_text(encoding="utf-8") for p in sorted(WORKFLOWS.glob("*.y*ml"))}
    return rs, workflows


def problems(rs: dict, workflows: dict[str, str]) -> list[str]:
    return ruleset_problems(rs) + workflow_problems(rs, workflows)


class RealRepositoryTests(unittest.TestCase):
    def test_ruleset_file_and_workflows_agree(self):
        rs, workflows = real_inputs()
        self.assertEqual(problems(rs, workflows), [])

    def test_changes_is_required(self):
        """Marco's decision for T80-03: option (a)."""
        self.assertIn("changes", contexts(real_inputs()[0]))

    def test_gate_step_is_injection_safe_and_narrow(self):
        """G4-80-13, G6-80-05: values only through env; Dependabot exempt only as author, actor and branch."""
        text = real_inputs()[1]["ci.yml"]
        step = text.split("- name: Pipeline gates", 1)[1].split("\n  codeql:", 1)[0]
        run = step.split("run: |", 1)[1]
        self.assertNotIn("${{", run)
        for needed in ('"$PR_AUTHOR" = \'dependabot[bot]\'', '"$ACTOR" = \'dependabot[bot]\'', "dependabot/*)"):
            self.assertIn(needed, run)
        self.assertNotIn("if:", step.split("run: |", 1)[0].replace("if: github.event_name == 'pull_request'", ""))

    def test_offline(self):
        """G4-80-07: no network or subprocess in this test."""
        source = Path(__file__).read_text(encoding="utf-8")
        for banned in ("import subprocess", "import socket", "urllib", "http.client", "requests"):
            self.assertNotIn(banned, source.replace('"' + banned + '"', ""))


class RedHelpers:
    """Shared fixtures for the red-case classes (not a TestCase, so nothing runs twice)."""

    def setUp(self):
        self.rs, self.wf = real_inputs()

    def assertFlags(self, rule, rs=None, wf=None):
        found = problems(rs if rs is not None else self.rs, wf if wf is not None else self.wf)
        self.assertTrue(any(rule in msg for msg in found), f"expected a {rule} problem, got {found}")
        return found

    def ci(self, old, new, count=1):
        wf = dict(self.wf)
        self.assertIn(old, wf["ci.yml"])
        wf["ci.yml"] = wf["ci.yml"].replace(old, new, count)
        return wf

    def rules(self, rs=None):
        return {r["type"]: r for r in (rs or self.rs)["rules"]}


class RedCaseTests(RedHelpers, unittest.TestCase):
    """G4-80-15: each defect fails with a message naming its rule."""

    def test_01_renamed_job(self):
        self.assertFlags("G4-80-04", wf=self.ci("\n  realm-guard:", "\n  realm-guard2:"))

    def test_02_removed_job(self):
        text = self.wf["ci.yml"]
        start = text.index("\n  realm-guard:")
        end = text.index("\n  claude-config:")
        self.assertFlags("G4-80-04", wf={**self.wf, "ci.yml": text[:start] + text[end:]})

    def test_03_floor_context_removed(self):
        rs = copy.deepcopy(self.rs)
        checks = self.rules(rs)["required_status_checks"]["parameters"]["required_status_checks"]
        checks[:] = [c for c in checks if c["context"] != "realm-guard"]
        self.assertFlags("G4-80-02", rs=rs)
        text = self.wf["ci.yml"]
        start, end = text.index("\n  realm-guard:"), text.index("\n  claude-config:")
        self.assertFlags("G4-80-02", rs=rs, wf={**self.wf, "ci.yml": text[:start] + text[end:]})

    def test_04_enforcement(self):
        for value in ("disabled", "evaluate"):
            with self.subTest(value=value):
                self.assertFlags("G4-80-01", rs={**self.rs, "enforcement": value})

    def test_05_bypass(self):
        self.assertFlags("G4-80-01", rs={**self.rs, "bypass_actors": [{"actor_id": 5, "actor_type": "RepositoryRole",
                                                                       "bypass_mode": "always"}]})
        rs = dict(self.rs)
        del rs["bypass_actors"]
        self.assertFlags("G4-80-01", rs=rs)

    def test_06_approvals(self):
        rs = copy.deepcopy(self.rs)
        self.rules(rs)["pull_request"]["parameters"]["required_approving_review_count"] = 1
        self.assertFlags("G4-80-01", rs=rs)

    def test_07_deletion_and_force_push(self):
        for rule in ("deletion", "non_fast_forward"):
            with self.subTest(rule=rule):
                rs = copy.deepcopy(self.rs)
                rs["rules"] = [r for r in rs["rules"] if r["type"] != rule]
                self.assertFlags("G4-80-01", rs=rs)

    def test_08_integration_id(self):
        for mutate in (lambda c: c.pop("integration_id"), lambda c: c.update(integration_id=1)):
            rs = copy.deepcopy(self.rs)
            mutate(self.rules(rs)["required_status_checks"]["parameters"]["required_status_checks"][0])
            self.assertFlags("G4-80-01", rs=rs)

    def test_09_not_strict(self):
        rs = copy.deepcopy(self.rs)
        self.rules(rs)["required_status_checks"]["parameters"]["strict_required_status_checks_policy"] = False
        self.assertFlags("G4-80-01", rs=rs)

    def test_10_conditional_jobs(self):
        self.assertFlags("G4-80-04", wf=self.ci("\n  build-test:\n", "\n  build-test:\n    if: github.actor != 'x'\n"))
        rs = copy.deepcopy(self.rs)
        self.rules(rs)["required_status_checks"]["parameters"]["required_status_checks"].append(
            {"context": "zap-baseline", "integration_id": ACTIONS_APP})
        self.assertFlags("G4-80-04", rs=rs)

    def test_11_changes_not_required(self):
        rs = copy.deepcopy(self.rs)
        checks = self.rules(rs)["required_status_checks"]["parameters"]["required_status_checks"]
        checks[:] = [c for c in checks if c["context"] != "changes"]
        self.assertFlags("G4-80-03", rs=rs)

    def test_12_name_collisions(self):
        extra = "on:\n  pull_request:\njobs:\n  realm-guard:\n    runs-on: ubuntu-latest\n    steps:\n      - run: exit 0\n"
        self.assertFlags("G4-80-05", wf={**self.wf, "evil.yml": extra})
        self.assertFlags("G4-80-05", wf=self.ci("\n  codeql:\n", "\n  codeql:\n    name: realm-guard\n"))

    def test_13_forgery(self):
        self.assertFlags("G4-80-06", wf=self.ci("\n  realm-guard:\n", "\n  realm-guard:\n    permissions: { checks: write }\n"))
        self.assertFlags("G4-80-06", wf=self.ci("on:\n  pull_request:\n", "on:\n  pull_request_target:\n  pull_request:\n"))

    def test_14_filtered_trigger(self):
        self.assertFlags("G4-80-04", wf=self.ci("on:\n  pull_request:\n", "on:\n  pull_request:\n    paths-ignore: ['docs/**']\n"))

    def test_15_read_only_key(self):
        self.assertFlags("G4-80-01", rs={**self.rs, "id": 23835975})

    def test_16_parser_fails_closed(self):
        self.assertFlags("G4-80-04", wf=self.ci("\n  realm-guard:\n", "\n  realm-guard: &guard\n"))
        self.assertFlags("G4-80-04", wf=self.ci("    runs-on: ubuntu-latest", "\truns-on: ubuntu-latest"))


class G6BypassTests(RedHelpers, unittest.TestCase):
    """The G6 reviewer's constructed bypasses (docs/security/reviews/80.md); each must now be flagged."""

    def test_g6_80_01_misindented_or_quoted_keys(self):
        cases = {
            "keys indented 3": ("\n  realm-guard:\n", "\n  realm-guard:\n   if: false\n"),
            "keys indented 6": ("\n  realm-guard:\n", "\n  realm-guard:\n      if: false\n"),
            "quoted if": ("\n  build-test:\n", "\n  build-test:\n    \"if\": false\n"),
            "spaced if": ("\n  build-test:\n", "\n  build-test:\n    if : false\n"),
        }
        for label, (old, new) in cases.items():
            with self.subTest(case=label):
                self.assertFlags("G4-80-04", wf=self.ci(old, new))

    def test_g6_80_01_second_workflow_indented_jobs(self):
        extra = ("permissions: { contents: read }\non:\n  pull_request:\njobs:\n    realm-guard:\n"
                 "      runs-on: ubuntu-latest\n")
        self.assertFlags("G4-80-04", wf={**self.wf, "evil.yml": extra})

    def test_g6_80_02_comment_does_not_hide_a_filter(self):
        self.assertFlags("G4-80-04", wf=self.ci("on:\n  pull_request:\n", "on:\n  pull_request:\n  # note\n    paths-ignore: ['x']\n"))

    def test_g6_80_03_quoted_write_and_missing_permissions(self):
        self.assertFlags("G4-80-06", wf=self.ci("\n  realm-guard:\n", "\n  realm-guard:\n    permissions: { checks: 'write' }\n"))
        self.assertFlags("G4-80-06", wf=self.ci("\n  realm-guard:\n", "\n  realm-guard:\n    permissions: { \"statuses\": \"write\" }\n"))
        self.assertFlags("G4-80-06", wf=self.ci("\npermissions:", "\n# permissions removed\nx-permissions:"))

    def test_g6_80_04_expression_name(self):
        # on a job that is not required, so nothing else flags it
        self.assertFlags("G4-80-05", wf=self.ci("\n  zap-baseline:\n", "\n  zap-baseline:\n    name: ${{ 'realm' }}-guard\n"))

    def test_g6_80_13_codeql_condition_and_continue_on_error(self):
        self.assertFlags("G4-80-04", wf=self.ci("github.event.repository.visibility == 'public' &&", "${{ 1 == 2 }} &&"))
        self.assertFlags("G4-80-04", wf=self.ci("\n  realm-guard:\n", "\n  realm-guard:\n    continue-on-error: true\n"))
        self.assertFlags("G4-80-04", wf=self.ci("on:\n  pull_request:\n", "on:\n  pull_request:\n    types: [opened]\n"))


class ParserTests(unittest.TestCase):
    def test_reads_triggers_jobs_and_needs(self):
        wf = parse_workflow("name: x\non:\n  pull_request:\n  push:\n    branches: [main]\njobs:\n  a:\n    runs-on: u\n"
                            "  b:\n    needs: [a]\n    if: always()\n  c:\n    needs:\n      - a\n      - b\n")
        self.assertEqual(set(wf["on"]), {"pull_request", "push"})
        self.assertEqual(needs_of(wf["jobs"]["b"]), ["a"])
        self.assertEqual(needs_of(wf["jobs"]["c"]), ["a", "b"])
        self.assertEqual(wf["jobs"]["b"]["if"], "always()")
        commented = parse_workflow("on: [pull_request]\njobs:\n  # a comment between jobs: and the first job\n  a:\n"
                                   "    # and one inside a job\n    runs-on: u\n")
        self.assertEqual(set(commented["jobs"]), {"a"})

    def test_flow_triggers_and_fail_closed_forms(self):
        self.assertEqual(set(parse_workflow("on: [push, pull_request]\njobs:\n  a:\n    runs-on: u\n")["on"]),
                         {"push", "pull_request"})
        for bad in ("on: push\njobs: {a: {runs-on: u}}\n", "on: push\njobs:\n  a: *x\n", "on: push\njobs:\n  a:\n    <<: *d\n",
                    "on: push\n", "jobs:\n  a:\n    runs-on: u\n"):
            with self.subTest(bad=bad), self.assertRaises(WorkflowParseError):
                parse_workflow(bad)


if __name__ == "__main__":
    unittest.main()
