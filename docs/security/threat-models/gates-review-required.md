<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #148 -->
# Threat delta: review-required check misses renames, leading-zero branches and the first local path (issue #148)

- Scope: how `changed_files()` and `check()` in `.claude/scripts/gates.py` collect the changed paths and handle a branch mismatch, plus the branch regex in the Pipeline gates step of `.github/workflows/ci.yml` (line 317). Tooling only. No Decisya application trust boundary changes.
- Baselines: `claude-config.md` (cc:T-09, cc:T-11), `pre-push-ci-parity.md` (G4-59-xx, G6-59-08), and the #74 review-required rule (G6-74-04, G6-74-05). New threats use T148-xx. Requirements use G4-148-xx.
- Adversary model: unchanged from cc:T-11. A same-repository PR controls the gate scripts that judge it, so this check guards against **mistakes and agent drift**, not against a determined author. Severities below are rated on that basis.
- Boundary: **host** run (manifest). G4 is the main session.
- ASVS 5.0 is mapped by analogy at section level, as in the baselines: V2.2 input validation, V15.3 defensive coding, V16.5 error handling (fail secure). Check the numbers against the official 5.0 text before copying them into a compliance artefact.
- Reviewer: security-reviewer agent, 2026-10-12. Mode: G3, before G4. Evidence comes from reading the code only. No commands were run.

## Verdict

**PASS-WITH-NOTES.** The planned change is the right fix. It uses `--no-renames -z` on both diffs, adds `ls-files -z --others --exclude-standard`, fails when the base diff fails, fails on a branch mismatch in CI mode, and anchors the branch regex. Three MUSTs below close the gaps the plan leaves open: the other two git calls must also fail closed and decode explicitly, CI mode must cover every mismatch, and the tests must be hermetic. Two Low findings are fix-now in `gates.py` (S-1, S-2). There is no High.

## Evidence (code reading, 2026-10-12)

- `gates.py:347-348`. `git()` drops the exit code, decodes with the locale encoding (`text=True`, with no `encoding=`) and calls `.strip()` on the whole output.
- `gates.py:359-364`. `changed_files()`:
  - `git diff --name-only origin/main...HEAD` runs with rename detection on (the Git default), so a rename lists only its destination. `core.quotePath` quotes non-ASCII paths as `"..\303\251.."`, and the leading `"` defeats `^` in `REVIEW_REQUIRED_PATHS`. **This also happens in CI**, not only locally.
  - In the porcelain loop, `.strip()` removes the first line's leading status space, so `line[3:]` cuts the first path (` M .claude/settings.json` becomes `claude/settings.json`). A rename keeps only the part after `->`.
  - A failed diff returns `""`, which becomes an empty set, so the check passes.
- `gates.py:295-302`. On a branch mismatch `check()` prints WARN and skips `review_required_problem()`. `ci.yml:317` extracts `n` with `^issue/([0-9]+)-`. So `issue/0148-x` gives `"0148"`, `int()` turns it into 148, the prefix `issue/148-` does not match, and the rule is skipped. The PR then rides on any manifest whose G3 and G6 are skipped or passed.
- `ci.yml:264`. `.claude/tests` runs inside GitHub Actions, on `pull_request` too. **The tests inherit `GITHUB_ACTIONS=true` and `GITHUB_HEAD_REF=issue/148-…`.** On the host they inherit neither. When a pre-push hook runs them through `prepush.py`, they may inherit `GIT_DIR` and related variables (G6-59-08).
- `prepush.py:71-82, 100-103` already shows the target pattern: argv only, `encoding="utf-8", errors="replace"`, `--no-renames -z`, and a split on NUL that keeps everything except empty entries. `test_prepush.py:80-92` shows the hermetic repository setup: `GIT_*` stripped, a temporary repo, local user and gpgsign config, and `origin/main` made with `update-ref`.
- `REVIEW_REQUIRED_PATHS` (`gates.py:314-325`) uses `.+` in several branches (`agents/.+`, `hooks/.+`, `skills/.../(scripts|assets)/.+`, `rulesets/.+`, `image-scan/.+`, `release/.+`). It has no flags.
- Draft point dropped: "gates.py accepts the issue number only as ASCII without a leading zero". With G4-148-02 it adds nothing. A non-ASCII or leading-zero `n` is turned back into an int by `int()`, and the prefix `issue/{n}-` then fails to match the real branch. In CI that is a FAIL.

## Threats

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 id | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T148-01 | Base and working-tree diff → review rule | T, E | Rename detection lists only the destination. A hook, agent, lane or workflow renamed (or `git mv`-ed and edited) out of a review-required path skips G3 and G6. | Medium | `--no-renames` on both diffs, so both sides are listed (G4-148-01). | V15.3 | Mitigated by requirements |
| T148-02 | Non-ASCII path in `git diff --name-only` (CI too) | T | The quoted path begins with `"` and never matches `^\.claude/…` or `^\.github/…`. | Medium | `-z` (no quoting) and explicit UTF-8 decoding (G4-148-01). | V2.2, V15.3 | Mitigated by requirements |
| T148-03 | Base diff fails (no `origin/main`, shallow checkout, no merge base) | T, D | The empty output reads as "no changes" and the check passes. A PR that sets `fetch-depth: 1` on `claude-config` would hide its own `ci.yml` change this way. | Medium | Any failed listing call returns None, and `check()` fails (G4-148-01). | V16.5 | Mitigated by requirements |
| T148-04 | Branch → issue number (`ci.yml:317`, `check()`) | S, E | A leading-zero branch (`issue/0148-x`), or any other mismatch, turns the check into a WARN and the review rule never runs. | Medium | The anchored regex in `ci.yml`, plus a FAIL on **any** mismatch when `GITHUB_ACTIONS=true` (G4-148-02). | V2.2, V16.5 | Mitigated by requirements |
| T148-05 | Porcelain parsing on the host | T | `.strip()` cuts the first path, and `->` drops the rename source. This happens locally only, because CI has a clean tree. | Low | The porcelain loop is replaced by `diff HEAD -z` and `ls-files -z` (G4-148-01). | V15.3 | Mitigated by plan |
| T148-06 | Locale decoding on Windows | T, D | With cp1252, UTF-8 path bytes turn into mojibake, or the call raises `UnicodeDecodeError`. Results then differ between the host and CI. | Low | `encoding="utf-8", errors="replace"`, as prepush does (G4-148-01). | V15.3 | Mitigated by requirements |
| T148-07 | Verbatim paths with `-z` → `REVIEW_REQUIRED_PATHS` | T | `.` does not match `\n`, so `.claude/agents/a\nb.md` misses the rule. Quoting missed it before as well, so this is not a regression. A case variant (`.Claude/agents/x.md`) also misses the rule, and on a case-insensitive checkout (Marco's Windows host, ADR-0011) Git writes it into `.claude/agents/`, where Claude Code loads it. Both need deliberate paths (cc:T-11). | Low | S-1 (fix-now, one line). | V2.2 | Open: fix in this PR |
| T148-08 | Printed paths in the FAIL message | R | With `-z`, control characters such as CR or ANSI escapes reach the CI log verbatim and can fake or hide lines. The exit code still stands. | Low | S-2 (fix-now). | V16.5 | Open: fix in this PR |
| T148-09 | New tests → Marco's repository, network, environment | T, D | If the tests are built carelessly: real `git fetch`/`diff` on Marco's repo, an inherited `GIT_DIR` from the pre-push hook acting on his repository, global hooks or signing, or results that change with CI's `GITHUB_ACTIONS` and `GITHUB_HEAD_REF`. | Medium (if built that way) | G4-148-03. | V15.3 | Mitigated by requirements |

## Requirements for G4 (main session)

MUST. Tests go in `.claude/tests/` (`test_gates.py`). Record red and green runs in the manifest's G4 evidence.

- **G4-148-01 (collection fails closed and reads raw).**
  - `changed_files(cwd: Path = ROOT) -> list[str] | None` uses its own runner. The runner takes an argv list (no shell), captures output, decodes with `encoding="utf-8", errors="replace"`, and returns the exit code and the **unstripped** stdout. An `OSError` counts as a failure. It does not use `git()`.
  - `git fetch --quiet origin` stays best-effort, because the host may be offline. The three listing calls each must succeed, or the function returns None: `diff --name-only --no-renames -z origin/main...HEAD`, `diff --name-only --no-renames -z HEAD`, and `ls-files -z --others --exclude-standard`.
  - Output is split on `"\0"`, and only empty entries are dropped.
  - On None, `check()` prints a FAIL (for example "could not determine the changed files") and counts it, on the host and in CI.
  - *Check* (temporary repo, see G4-148-03):
    - a committed `git mv .claude/hooks/a.py docs/a.py` lists both paths, and `check()` fails with G3 and G6 skipped;
    - the same move left uncommitted (staged) lists both paths;
    - a modified tracked file that sorts first in the list comes back whole (the first-path regression);
    - a non-ASCII path such as `.claude/agents/é.md` comes back exactly, as `str` equality;
    - with `refs/remotes/origin/main` deleted, the result is None and `check()` returns 1.
- **G4-148-02 (CI mode: every mismatch fails; the regex is anchored).**
  - When `os.environ.get("GITHUB_ACTIONS") == "true"` and the branch does not start with `issue/{n}-`, `check()` prints FAIL and counts it. This includes `HEAD` (detached, `HEAD_REF` and `GITHUB_HEAD_REF` unset) and an empty value. Outside CI it stays a WARN.
  - `ci.yml:317` becomes `sed -nE 's#^issue/([1-9][0-9]*)-[a-z0-9][a-z0-9-]*$#\1#p'`. Only that line changes in the step.
  - *Check:*
    - `check(148)` with `HEAD_REF=issue/0148-x` and `GITHUB_ACTIONS=true` returns 1 with FAIL, and returns WARN without it;
    - a detached `HEAD` in CI mode fails;
    - a test reads the Pipeline gates step's `n=` line from `ci.yml` and runs it under bash (`shutil.which("bash")`, as `test_ci_changes.py` does; skip if bash is missing). `issue/0148-x`, `issue/148-`, `issue/148-X` and `issue/1a-x` give an empty value, and `issue/148-gates-review-required` gives `148`.
- **G4-148-03 (hermetic tests, never Marco's repository).**
  - Each git test strips every `GIT_*` variable from the environment (G6-59-08). It runs in a `tempfile` repo with local `user.name`, `user.email` and `commit.gpgsign=false`. For `origin`, it uses either `update-ref refs/remotes/origin/main` with no remote (the fetch fails harmlessly) or a local bare repository. Nothing uses the network, Marco's working tree or `git stash`.
  - No test calls `changed_files()` without `cwd`.
  - **Every** test that calls `check()` sets or removes `GITHUB_ACTIONS`, `HEAD_REF` and `GITHUB_HEAD_REF` explicitly, so it gives the same result on the host and in CI's `pull_request` run (evidence: `ci.yml:264`). This applies to the existing `test_check_runs_the_review_rule_on_a_detached_ci_checkout` and `test_ci_failure_reaches_check_exit_code` too.

### SHOULD

- **S-1 (fix-now, Low, T148-07).** Compile `REVIEW_REQUIRED_PATHS` with `re.DOTALL | re.IGNORECASE`. It only widens the match, which is the fail-safe direction. Add a test with a newline path and a case variant. Use `review_required_problem` directly on the strings, or put the path in the index only with `git update-index --add --cacheinfo`, because Windows cannot create a file name containing a newline.
- **S-2 (fix-now, Low, T148-08).** Escape control characters in the paths that `review_required_problem` prints, as `prepush.safe()` does (G4-59-11). It can be a local copy, because `gates.py` should not import from `prepush.py`.
- **S-3 (G4 check, no code).** Before merge, check that no open PR branch has a slug outside `[a-z0-9-]`, because the new anchored regex rejects one. If one exists, rename the branch rather than loosening the regex.
- **S-4 (backlog #83).** `classify` treats None as "unknown". Make it print "could not determine the changed files" instead.

## Residual risk after #148

- cc:T-11 still holds. A PR can edit `gates.py` or `ci.yml` and so judge itself. Those edits are review-required and visible in the diff, and Marco merges.
- A failing best-effort fetch on the host leaves `origin/main` stale. The diff against a stale base is a superset, so files can only be added, never hidden.
- Outside CI a branch mismatch stays a WARN. The required `claude-config` check in CI is the authority.
