# Phase 0 – Realm-guard scope and CI/pre-push trigger widening (issue #77)

## Scope note

G1 (product-owner) was skipped for this issue, with Marco's approval recorded in
`docs/ai/pipeline/77.md` ("no user-facing behaviour"). There is no Gherkin requirements
file to trace, so this artifact traces the two things the manifest names instead:

- **(a)** each "Done when" bullet of `docs/ai/pipeline/77.md`;
- **(b)** each G3 MUST requirement `G4-77-01` to `G4-77-11`, plus the taken SHOULDs
  `G4-77-12`, `13`, `14`, from `docs/security/threat-models/realm-guard-scope.md`
  (verdict PASS-WITH-NOTES).

Each row is marked **Direct** (a test exercises the exact behaviour), **Indirect** (a
test exercises it as a side effect of testing something else), or **Manual** (with a
reason — verified by reading code/config or by a live check, not by `dotnet test` /
`python -m unittest`).

Platform invariants that do not apply to this issue: it adds no persisted aggregate
(no `ITenantScoped` type, so no two-tenant isolation test applies), and it touches no
authentication code path (no expired-token/wrong-audience/`alg=none`/missing-antiforgery
negative test applies). It is CI and pre-push tooling plus a test-project helper class.

## Test run evidence (test-engineer, 2026-09-27)

- `dotnet test --project tests/Decisya.Identity.Tests --filter-class "Decisya.Identity.Tests.RealmGuardTests"`:
  **41 of 41 passed** (39 `[Theory]` rows from `realm-guard-cases.json` +
  `The_exemption_list_is_exactly_the_expected_set` + `No_file_in_the_working_tree_offends_the_scoped_guard`).
  identity-dev closed this gate's gap 1 (below) by adding three rows to
  `realm-guard-cases.json` — `LICENSE`, `.github/ISSUE_TEMPLATE/bug.md` and
  `.github/ISSUE_TEMPLATE/config.yml`, each naming the realm file, `offends: false`,
  `in_scope: false` — raising the theory count from 36 to 39 and the class total from
  38 to 41, re-confirmed by this run.
- `python -m unittest discover -s .claude/tests -p "test_*.py"`: **Ran 171 tests, OK
  (skipped=2)**. Unchanged (the new rows are read by `test_realm_guard_trigger.py`'s
  shared-table tests, not added as separate Python tests), and re-confirmed here.
- Re-run in isolation, to confirm which sub-tests actually execute on this host (bash
  and openssl are both present, so the parity tests run rather than skip):
  - `.claude/tests/test_realm_guard_trigger.py`: 7 of 7 passed, including
    `test_every_in_scope_path_triggers_ci` and both `BehaviourParityTests` methods
    (not skipped).
  - `.claude/tests/test_prepush.py`: 16 of 16 passed.
  - `.claude/tests/test_gates.py`: 34 tests, 1 skipped (`test_symlink_in_lane_to_another_lane`,
    needs Developer Mode or admin on Windows — pre-existing, unrelated to #77).

No test was added or changed by this gate directly. identity-dev closed this gate's
first-pass gap 1 (the untested `LICENSE`/`.github/ISSUE_TEMPLATE/**` exemption
branches) by adding the three case-table rows above, with a mutation check: removing
the `LICENSE` and `.github/ISSUE_TEMPLATE/` branches from
`RealmGuard.IsUnconditionallyExempt` failed exactly those 2 rows (39 of 41); restored,
41 of 41 pass. The unit lane is now 522 tests (up from 519, the three new rows); the
`.claude` suite stays 171 tests, 2 skipped. Gap 2 (the `keycloak-realm.md` pointer)
remains, deferred to G6 as before (see "Gaps").

## (a) Done-when → tests

| # | Done when (`docs/ai/pipeline/77.md`) | Test(s) | Class |
| --- | --- | --- | --- |
| 1 | "Option chosen (both: scope the guard and widen the trigger), with G3 on the threat delta for G4-17-12." | `docs/security/threat-models/realm-guard-scope.md` (verdict `PASS-WITH-NOTES`, issue #77) records the G3 threat delta for G4-17-12 (T-02); the two changes it approves are implemented and tested by the `G4-77-01/02/03` rows (scope) and the `G4-77-05/06` rows (trigger) below | Manual — a gate artifact, not a `dotnet test`/`python -m unittest` case; verified by reading the verdict line and cross-checking that both named changes are covered below |
| 2 | "A test proves the realm name inside `.claude/` is allowed by the scoped guard, and that a `.claude`-only change runs the .NET unit tests in CI and in the pre-push hook." | Allowed inside `.claude/`: `RealmGuardTests.Offends_matches_the_expected_verdict_for_every_shared_case` rows for `.claude/tests/test_x.py` (name only), `.claude/skills/k/SKILL.md` (prose) and `.claude/agents/a.md` (prose) — all `offends: false`. Runs .NET on a `.claude`-only change: `test_realm_guard_trigger.GuardScopeTriggerTests.test_now_triggering_paths` (`prepush.needs_dotnet` true for `.claude/scripts/lint.py`, `.claude/tests/test_x.py`, `.claude/skills/k/SKILL.md`) and `.test_every_in_scope_path_triggers_ci` (same paths through the real `ci.yml` lanes block) | Direct |
| 3 | "CI and the pre-push hook agree on the rule (`test_ci_changes.py` / `test_prepush.py` parity)." | The manifest's own name is imprecise: `test_ci_changes.py` covers an unrelated concern (workflow-command injection in the `print-changed` block, #39) and was not changed for #77. The actual parity tests are `test_realm_guard_trigger.BehaviourParityTests.test_same_decision` (runs the real `ci.yml` lanes block and `prepush.needs_dotnet` on every shared-table row plus the `EXTRA`/`EXPECTED_DIFFERENCE` rows and asserts equal decisions, or the documented Web-lane difference), `.test_unknown_base_runs_everything`, `.test_docs_only_stays_fast_in_both`, plus `test_prepush.CiParityTests.test_ignore_regex_matches_ci` (string parity of `ignore`/`ignore_md` against `ci.yml`) and `.test_unit_filters_match_ci` | Direct (behaviour-parity); the manifest's file name should read `test_realm_guard_trigger.py`, not `test_ci_changes.py` — noted, not corrected here since the manifest text is not this gate's artifact to edit |
| 4 | "Every step is documented for both VS 2026 and the CLI." | `.claude/skills/keycloak/SKILL.md`'s table gained a row: "No new launch path names the realm file (#17 G4-17-12, #77) \| `RealmGuardTests`, `realm-guard-cases.json` \| `dotnet test ...`; VS 2026: *Test Explorer*, filter `RealmGuardTests`, *Run*". `docs/runbooks/issue-pipeline.md`'s pre-push paragraph and `prepush.py`'s docstring state the new rule in prose (CLI-only content; the pre-push hook has no VS 2026 UI step, consistent with the rest of that runbook) | Manual — documentation content, not a test; read directly (`git diff HEAD -- .claude/skills/keycloak/SKILL.md docs/runbooks/issue-pipeline.md`) |

## (b) G3 requirements → tests

| Id | Requirement (summary) | Test(s) | Class |
| --- | --- | --- | --- |
| G4-77-01 | Narrow `.claude` exemption to exactly `.claude/**/*.md` and `.claude/tests/**`; nothing else | `RealmGuardTests.The_exemption_list_is_exactly_the_expected_set` (pins the two `.claude` rules and nothing more); case-table rows proving the boundary: `.claude/hooks/x.py`, `.claude/scripts/x.py`, `.claude/skills/k/scripts/x.py`, `.claude/skills/module-scaffold/assets/Module.csproj`, `.claude/settings.json`, `.claude/settings.local.json`, `.claude/boundaries.json` all `offends: true` | Direct |
| G4-77-02 | Executable Markdown stays in scope: fails inside YAML frontmatter or on a line containing `` !` `` | Case rows: `.claude/agents/a.md` with the name in `tools:` frontmatter → `offends: true`; `.claude/skills/k/SKILL.md` with the name on a `` !`echo …` `` line → `offends: true`; the same file with plain prose → `offends: false` | Direct |
| G4-77-03 | Full working-tree scan (`.git/` skip only, no kind allow-list); pure function; separator-anchored relative-path exemption matching (closes T77-11) | `RealmGuard.Offends` is the pure function under test by every theory row; `RealmGuardTests.No_file_in_the_working_tree_offends_the_scoped_guard` drives the real scan; the T77-11 fix: `tests-x/y.txt` → `offends: true` (was wrongly exempt under the old unanchored prefix match) | Direct — `docs.old/y.md` is a **documented deviation**, not a fix miss: it is `offends: false` because it is Markdown outside `.claude/`, which G4-77-05/07 require the guard to exempt (scope ⊆ trigger); recorded as a deviation in the G3 threat model's own "Deviations for G6" section, not a G5 gap |
| G4-77-03/05 (`LICENSE`/`.github/ISSUE_TEMPLATE/` exemption branches) | The guard's `LICENSE` (exact) and `.github/ISSUE_TEMPLATE/` (separator-anchored prefix) unconditional exemptions in `RealmGuard.IsUnconditionallyExempt`, so guard scope for these two paths stays exactly equal to what the CI/pre-push trigger already ignores | **Closed in this gate's second pass.** identity-dev added three case-table rows: `LICENSE`, `.github/ISSUE_TEMPLATE/bug.md` and `.github/ISSUE_TEMPLATE/config.yml`, each naming the realm file, all `offends: false`/`in_scope: false`; `config.yml` is the row that proves the `ISSUE_TEMPLATE` branch specifically, since `bug.md` is also exempt as Markdown outside `.claude/` on its own. Mutation check (identity-dev): removing the `LICENSE` and `ISSUE_TEMPLATE` branches from `IsUnconditionallyExempt` failed exactly those 2 rows (39 of 41 passed); restored, 41 of 41 pass — re-confirmed by this gate's own run above | Direct — closes "Gap 1" from this gate's first pass, superseded below |
| G4-77-04 | `.claude/tests/**` fails only when it also carries a launch marker | Case rows: `.claude/tests/test_x.py` with `--import-realm` → `offends: true`; with "docker compose" text → `offends: true`; with the name alone → `offends: false` | Direct |
| G4-77-05 | CI `changes` job: `.claude/**` (incl. `*.md`), `.vscode/**` and `claude-review.yml` all count as code; `docs/`, `LICENSE`, issue templates, `dependabot.yml`, Markdown outside `.claude/` stay ignored; `changed=ALL` still runs everything | `test_realm_guard_trigger.GuardScopeTriggerTests.test_every_in_scope_path_triggers_ci` (runs the real `# lanes:` block of `ci.yml` in bash) covers `.claude/skills/k/SKILL.md`, `.vscode/tasks.json`, `.github/workflows/claude-review.yml` (all in-scope, `dotnet=true`); `BehaviourParityTests.test_unknown_base_runs_everything` covers `ALL`; `.test_docs_only_stays_fast_in_both` covers `LICENSE` and `.github/dependabot.yml` staying `dotnet=false` on both sides | Direct |
| G4-77-06 | `prepush.py` matches CI for every path, `.claude/**/*.md` included; `test_docs_only_push_skips_dotnet` inverted for `.claude/scripts/lint.py` | `test_prepush.CiParityTests.test_docs_only_push_skips_dotnet` (asserts `.claude/scripts/lint.py` now **triggers**, and a mixed `docs/` + `.claude/` list triggers); `test_realm_guard_trigger.GuardScopeTriggerTests.test_now_triggering_paths` (`lint.py`, `test_x.py`, `SKILL.md`, `tasks.json`, `claude-review.yml` all true; `docs/x.md` stays false) | Direct |
| G4-77-07 | One shared path table drives both a .NET test and a Python test; a row that is in scope but untriggered fails | `realm-guard-cases.json` is read by both `RealmGuardTests.Cases()` (.NET) and `test_realm_guard_trigger.cases()` (Python); `GuardScopeTriggerTests.test_every_in_scope_path_triggers_prepush` and `.test_every_in_scope_path_triggers_ci` fail on any `in_scope: true` row that does not trigger; `.test_case_table_is_shared_and_complete` guards the table's shape (>20 rows, exact key set) | Direct |
| G4-77-08 | Behaviour parity: run the lanes block in bash for the shared table plus the named extra rows (`ALL`, `docs/x.md`, `README.md`, `src/Decisya.Api/Program.cs`, `.claude/skills/k/SKILL.md`, `.vscode/tasks.json`); Web-filter divergence recorded, not hidden; keep or replace `test_ignore_regex_matches_ci` | `test_realm_guard_trigger.BehaviourParityTests.test_same_decision` (all cases + `EXTRA` + `EXPECTED_DIFFERENCE`), `.test_unknown_base_runs_everything`, `.test_docs_only_stays_fast_in_both`; `EXPECTED_DIFFERENCE` documents `src/Decisya.Web/**` as `(ci=False, hook=True)` rather than hiding it; `test_prepush.CiParityTests.test_ignore_regex_matches_ci` was kept (string parity of `ignore`/`ignore_md`), satisfying "keep … do not drop parity" | Direct. The mutation run ("patch `needs_dotnet` to ignore `^\.claude/` again, show 10 `BehaviourParityTests` cases fail, revert") is recorded as a one-off G4 evidence run, per the G3 requirement's own wording ("record … a mutation run … reverted in-process"); it is not a permanent regression test, so it is **Manual** (G4 evidence: "10 cases fail"), not re-run at G5 |
| G4-77-09 | Pinned exemption list; failure message names the rule and prints offending paths relative to root | `RealmGuardTests.The_exemption_list_is_exactly_the_expected_set` (`BeEquivalentTo` with strict ordering against the exact expected array, using `RealmGuard.PinnedExemptionRuleDescription` as the failure message); `.No_file_in_the_working_tree_offends_the_scoped_guard` prints `offendingFiles` (repo-relative, `/`-separated) in the same message on failure | Direct |
| G4-77-10 | Red-then-green tests recorded in G4 evidence (six items) | The **green** side is reproduced by this gate's own runs above (41/41 .NET — up from 38/38 in this gate's first pass, by the 3 rows that closed `G4-77-03/05` above — and 171 Python). The **red** side (pre-#77 rules failing 9/38 .NET rows and 34 Python rows) is G4 evidence recorded in `docs/ai/pipeline/77.md`'s "Red" section, produced by temporarily swapping in the old rules — not reproducible at G5 without reverting the fix on disk. The follow-up mutation for `LICENSE`/`ISSUE_TEMPLATE` (39 of 41, then 41 of 41 restored) is additional G4 evidence for the same item, recorded above | Direct (green, re-confirmed); Manual (red, G4 evidence) |
| G4-77-11 | Docs updated: `keycloak/SKILL.md`, `keycloak-realm.md` pointer, `prepush.py` docstring, `ci.yml` comment; every step has both a VS 2026 path and a CLI command | `.claude/skills/keycloak/SKILL.md` (new table row, both paths), `.claude/scripts/prepush.py`'s docstring (item 4, rewritten), `.github/workflows/ci.yml`'s `# lanes:` comment (rewritten) — all confirmed by reading the diff. `docs/security/threat-models/keycloak-realm.md`'s G4-17-12 pointer is explicitly deferred to G6 per the G4 evidence ("in security-reviewer's lane, so it is asked for at G6") — not a G5 gap, flagged here so G6 does not miss it | Manual — prose, no test asserts it |
| G4-77-12 (SHOULD, taken) | Web lane / always-run guard, so SPA-only changes still run the guard | `ci.yml`'s `realm-guard` job carries no `if:` and no `needs: changes`, so it runs on every PR and push regardless of the lanes decision (read directly); `test_realm_guard_trigger.EXPECTED_DIFFERENCE` documents that `src/Decisya.Web/**` still skips the *lane* (`ci_dotnet` false) while `needs_dotnet` (hook) stays true, and `test_same_decision` fails if that pair ever silently changes | Direct (job structure, and the documented-divergence test) + Manual ("CI runs `realm-guard` on every PR" is confirmed on a real PR's Checks tab, not locally) |
| G4-77-13 (SHOULD, taken) | Case-insensitive needle; also flag `/opt/keycloak/data/import` | Case rows: `compose.yaml` with `Decisya-Realm.JSON` (mixed case) → `offends: true`; `Dockerfile` with `RUN mkdir -p /opt/keycloak/data/import` (no file name at all) → `offends: true` | Direct |
| G4-77-14 (SHOULD, taken) | `gates.py` `REVIEW_REQUIRED_PATHS` covers the guard files, the case table, and `.claude/skills/*/(scripts|assets)/**` | `test_gates.ReviewRequiredTests.test_every_script_and_the_docker_fixtures_need_review` includes `tests/Decisya.Identity.Tests/RealmGuard.cs`, `RealmGuardTests.cs`, `realm-guard-cases.json`, `.claude/skills/module-scaffold/scripts/scaffold.py`, `.claude/skills/module-scaffold/assets/Module.csproj` | Direct |

## Gaps

- **Gap 1 — CLOSED.** This gate's first pass found that `LICENSE` and
  `.github/ISSUE_TEMPLATE/**` had no case-table row exercising `RealmGuard.Offends`'s
  own `IsUnconditionallyExempt` branches for those two paths (only the pinned
  string-list meta-test and the Python-side trigger test covered them, weakly).
  identity-dev closed it by adding the three rows to `realm-guard-cases.json` described
  in `G4-77-03/05` above, with a mutation check proving the branches are now covered
  (removing them failed exactly those 2 rows, 39 of 41; restored, 41 of 41). Re-run and
  confirmed by this gate: `dotnet test --project tests/Decisya.Identity.Tests --filter-class "Decisya.Identity.Tests.RealmGuardTests"`
  → 41 of 41.
- **Gap 2 — open by design, stays with G6.**
  `docs/security/threat-models/keycloak-realm.md`'s G4-17-12 pointer (part of
  G4-77-11) is intentionally not yet added — the G4 evidence assigns it to G6
  (security-reviewer's lane). Flagged here so G6 does not treat its absence as a
  regression.
- **The mutation run for G4-77-08** (the CI/pre-push trigger parity mutation, distinct
  from Gap 1's `LICENSE`/`ISSUE_TEMPLATE` mutation) is recorded once in the G4 evidence,
  not as a permanent automated test (the requirement's own wording asks for a one-off
  record, reverted in-process). No gap: nothing on disk would let it be re-run without
  manually re-introducing the old `^\.claude/` ignore line.

None of the above blocks the verdict: every MUST (`G4-77-01` to `07`, `09`, `10`) and
every taken SHOULD (`G4-77-12` to `14`) has a passing Direct test; Gap 1 is closed, and
the one remaining item (the doc pointer) is explicitly deferred to G6 and does not
reopen any High from the threat model.

<!-- gate: G5 | verdict: PASS | issue: #77 -->
