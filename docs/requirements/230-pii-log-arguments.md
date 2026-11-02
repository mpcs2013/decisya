# Requirements: keep personal data out of log arguments (issue #230, finding C-12)

Origin: register finding C-12 (Medium) in `docs/security/threat-models/system-baseline.md` ("Carry-forward: PII under an innocent name"), from #69 F-7 (`apphost-servicedefaults.md`, F-7), the Phase 0 exit review (#83) and #27 L-4 b (`[Sensitive]` on stored fields that hold `sub`). The record of #68 was not found under `docs/`; only the three sources above were read.
Role: the people who need this are the maintainers and the security reviewer (G6), who today read every log call by hand, and, later, tenants whose data would reach logs. It is a Phase 1 prerequisite before the first Ledger feature (D-012 point 2).
Plan gate: not a customer feature, so no entitlement feature key applies (`n/a`, platform control).
Behaviour change: none for users. For developers, a log call that passes personal data in plain form starts to fail the build or a test. Announcement: the PR body states the new rule, and the developer docs list the personal-data catalogue and the way to fix a failure (mask, drop, or use the hashed id). No runtime log output changes except that `UserId` values on `Membership` and `MemberDto` render masked if they are ever logged (nothing logs them today).

## Definitions

**Personal-data catalogue.** One checked-in list that names every category of personal data, and the check reads it. Where it lives is a G2 decision, but it MUST be a single file in the repository (not scattered in code) and it MUST be extendable without changing the check. Minimum categories:

| Category | Examples |
| --- | --- |
| User identifier | raw `sub` claim, `Membership.UserId`, `MemberDto.UserId`, `AuditRecord.ActorUserId`, `ICurrentCaller.UserId` |
| Contact | e-mail address, phone number |
| Name | person name, display name, payee name |
| Account number | IBAN, card number, account number, any bank identifier |
| Free text | transaction description, memo, notes, override reason |
| AI prompt and AI output | any prompt text or completion text (CLAUDE.md: log a hash and a token count only) |
| Secret material | token, cookie, password (already covered by the key deny-list; the check must not regress it) |

**Plain log argument.** A value passed as a template argument to any `ILogger` call (`Log*`, `Log(...)`, `BeginScope`) or to a parameter of a `[LoggerMessage]` method, whose static type or declaring member is in the catalogue and which is not masked. A value is masked only when it is one of: the owning object passed whole, where its personal-data members carry `[Sensitive]` (the masking processor then masks them); a `[Sensitive]` type; the output of `UserIdHasher.Hash`; a listed redacting type. A string read from a `[Sensitive]` member is NOT masked at run time (`SensitiveDataMaskingProcessor`, G2 D3): a scalar read from a `[Sensitive]` member is a finding. The check judges the value, not the placeholder name: `{Payee}`, `{Value}` and `{X}` all fail if the argument is a description. The same applies to values reached through string interpolation or concatenation into the message, and to the exception message when it is built from a catalogue value.

## Story 1: a log call with personal data fails the check

As a maintainer, I want the build or a test to fail when a log call passes personal data as a plain argument, so that review no longer depends on a person noticing it.

```gherkin
Scenario: a raw user id is passed to ILogger
  Given a source file with logger.LogInformation("Seen {Who}", caller.UserId)
  When the build or the check test runs
  Then it fails
  And the failure names the file, the line and the personal-data category "User identifier"

Scenario Outline: each catalogue category is caught under an innocent placeholder name
  Given a log call whose placeholder is "{Value}" and whose argument is a <category> value of plain type string
  When the build or the check test runs
  Then it fails and names the category "<category>"
  Examples:
    | category          |
    | User identifier   |
    | Contact           |
    | Name              |
    | Account number    |
    | Free text         |
    | AI prompt         |

Scenario: a [LoggerMessage] method parameter carries personal data
  Given a [LoggerMessage] partial method with a string parameter fed from tx.Description
  When the build or the check test runs
  Then it fails

Scenario: personal data reaches the message by interpolation
  Given logger.LogWarning($"Failed for {member.UserId}")
  When the build or the check test runs
  Then it fails

Scenario: a masked value passes
  Given a log call whose argument is an object passed whole whose personal-data members carry [Sensitive]
  And a log call whose argument is a [Sensitive] type
  And a log call whose argument is the output of UserIdHasher.Hash
  And a log call whose argument is a listed redacting type
  And a log call whose arguments are only ids of non-personal kinds, codes and counts
  When the build or the check test runs
  Then it passes with zero findings

Scenario: a scalar read from a [Sensitive] member is a finding
  Given a log call whose argument is record.ActorUserId, a string read from a member carrying [Sensitive]
  When the build or the check test runs
  Then it fails and names the category "User identifier"

Scenario: the check is proven red
  Given a deliberate fixture violation kept in the check's own tests
  When the check's test suite runs
  Then the fixture is reported as a failure by the check
  And removing the fixture violation makes the check pass
```

## Story 2: the check covers the whole of `src/` and cannot be bypassed silently

As the security reviewer, I want the check to run on every PR over all product code, so that a new module cannot skip it.

```gherkin
Scenario: all product projects are covered
  Given every project under src/
  When the check runs in CI on a pull request
  Then each project is examined
  And a new project is examined without editing the check

Scenario: a suppression is visible
  Given a log call that is a verified false positive
  When the developer suppresses the finding
  Then the suppression requires a written justification at the call site
  And the suppression count is reported in the check output so G6 can review each one

Scenario: the catalogue is extended
  Given a new category is added to the catalogue file
  When the check runs against a fixture using that category
  Then the fixture is reported, with no change to the check's code

Scenario: today's code is clean
  Given the current src/ tree
  When the check runs
  Then it reports zero findings (the G6 reviews 21 to 26 found none)
  And any finding the check raises on existing code is fixed in this PR, not suppressed
```

## Story 3: user-id properties are `[Sensitive]` and masked

As a maintainer, I want `Membership.UserId` and `MemberDto.UserId` to carry `[Sensitive]`, so that the raw `sub` is masked if someone logs a membership or a member list.

```gherkin
Scenario: the attributes are present
  Given the types Membership and MemberDto
  When their UserId properties are inspected
  Then each carries [Sensitive]

Scenario: a Membership value is logged as structured state
  Given a Membership whose UserId is the canary "sub-canary-7f3a"
  When it is passed as a log argument through the logging pipeline
  Then no emitted stdout log line contains "sub-canary-7f3a"
  And the user-id member renders as the mask

Scenario: a MemberDto list is logged
  Given a list of MemberDto values with the canary user id
  When it is logged
  Then no emitted line contains the canary

Scenario: ToString does not leak
  Given a Membership and a MemberDto holding the canary
  When ToString() is called and the result is logged
  Then the canary does not appear in the output (the synthesized record ToString of MemberDto must not render UserId)

Scenario: persistence and API output are unchanged
  Given the Tenancy endpoints that return MemberDto
  When GET members is called by an owner
  Then the JSON response still contains the real user id
  And the database column and the migration snapshot are unchanged
```

`AuditRecord.ActorUserId` is the other stored field that holds the raw `sub`. It already carries `[Sensitive]` (`src/Modules/Audit/Decisya.Modules.Audit/Domain/AuditRecord.cs:41`), so no attribute edit is needed. Marco's decision (2026-11-02, option A) puts it in scope as one masking test, next to `Membership.UserId` and `MemberDto.UserId`.

```gherkin
Scenario: an AuditRecord is logged as structured state
  Given an AuditRecord whose ActorUserId is the canary "sub-canary-7f3a"
  When it is passed whole as a log argument through the logging pipeline
  Then no emitted stdout log line contains "sub-canary-7f3a"
  And the actor user-id member renders as the mask
```

## Story 4: the #15 masking and logging tests are revisited

As the security reviewer, I want the existing masking and logging tests re-read against the new rule, so that the old residual (F-7, T-13) is closed and no earlier guarantee is lost.

```gherkin
Scenario: the old residual is covered by a test
  Given the #15 case LogInformation("Paid {Payee}", tx.Description)
  When it runs through the check
  Then the check fails it, and a test records that case by name

Scenario: earlier guarantees still hold
  Given the tests in tests/Decisya.ServiceDefaults.Tests/Logging (SensitiveDataMaskingProcessorTests, MaskingLogRecordProcessorTests, DecisyaJsonConsoleFormatterTests, UserIdHasherTests)
  When the full suite runs
  Then all earlier tests still pass
  And the deny-list fragments, JWT and bearer shape masking, and fail-closed behaviour are unchanged

Scenario: the revisit is recorded
  Given the PR body
  Then it lists each #15 logging test file reviewed, and any test added, changed or found obsolete, each with a reason
  And NFR-11 and the baseline text "Control until the analyzer exists" are updated to say the control now exists
```

## Story 5: the failure is actionable

As a developer, I want a failing log call to tell me how to fix it, so that I do not suppress it by habit.

```gherkin
Scenario: the failure message helps
  Given a failing log call
  When the check reports it
  Then the message names the category, the file and line, and the three fixes: mask with [Sensitive], log the hashed id, or drop the value
```

## Quality targets

New row in `docs/requirements/nfr.md`: NFR-62 (Security).

| Target | Value |
| --- | --- |
| Catalogue categories each proven red by a fixture | 7 of 7, 100% |
| Plain personal-data arguments in `src/` at merge | 0 |
| Undetected cases among the fixtures (false negatives) | 0 |
| Added build or check time on a full `dotnet build` / unit run | at most 10% over the pre-change baseline measured on the same host, G5 records both numbers |
| Earlier #15 logging tests failing after the change | 0 |
| `UserId` properties masked in structured log output | 100% (3 of 3 named properties, canary test) |

## Out of scope

- Runtime scrubbing of arbitrary strings (the masking processor stays the runtime backstop; value-shape detection of e-mail or IBAN text is not required).
- Retention, erasure and export of logs (baseline points 2 to 4, #29 and #83).
- Traces, metrics attributes and exception text beyond the exception message built from a catalogue value.
- Adding `[Sensitive]` to `AuditRecord.ActorUserId` (already present; only its masking test is in scope).

## Notes for G2 and the owner (not blocking)

1. Mechanism (analyzer, architecture test or other) is G2's. Any new package needs Marco's approval and a one-line justification (CLAUDE.md).
2. `AuditRecord.ActorUserId` already carries `[Sensitive]`; Marco decided (2026-11-02, option A) that its masking test joins this PR.
3. Recommendation: the catalogue is judged on static type and declaring member, not on the placeholder name, because the placeholder name is the very thing C-12 says cannot be trusted. Name-based hints may be added only as extra signals.
4. The #68 record could not be found in the repository; if it holds a requirement not listed here, the orchestrator should pass it to G2.

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #230 -->

## Traceability

G5, test-engineer, 2026-11-02. Rule tests are in `tests/Decisya.ArchitectureTests` (`PersonalDataLogRuleTests`, `PersonalDataCatalogueTests`, both `Category=Architecture`, written in G4). The masking tests are behaviour tests written in G5, `Category=Unit`, using the real `SensitiveDataMaskingProcessor` and the `decisya-json` formatter (helper `TestSupport/MaskedLogLine.cs` in each module test project, so the "emitted line" is the formatter's stdout line, with the formatter's own naive `ToString` message path present). The canary is `sub-canary-7f3a`. Test names are abbreviated to the part before the first `_` run where a theory covers several cases; the file holds the full name.

In the tests below, `TUM` is `tests/Modules/Decisya.Modules.Tenancy.Tests/TenancyUserIdMaskingTests.cs`, `AAM` is `tests/Modules/Decisya.Modules.Audit.Tests/AuditActorUserIdMaskingTests.cs`, `PDLR` is `PersonalDataLogRuleTests` and `PDC` is `PersonalDataCatalogueTests`.

### Acceptance criteria

| Story / scenario | Test(s) | Lane |
| --- | --- | --- |
| 1: a raw user id is passed to ILogger | `PDLR.R1_F1_the_caller_user_id_under_an_innocent_placeholder_is_reported_with_file_line_and_category` | Architecture |
| 1: each catalogue category is caught under an innocent placeholder (outline) | `PDLR.R1_F2_to_F8_each_of_the_seven_categories_is_reported_under_a_plain_Value_placeholder` (theory: seven rows incl. `secret-material`), `PDLR.R1_every_category_of_the_catalogue_has_a_red_fixture` | Architecture |
| 1: a [LoggerMessage] parameter carries personal data | `PDLR.R1_F9_a_LoggerMessage_parameter_fed_from_a_description_is_reported_at_the_call_site`, `PDLR.R4a_the_generated_body_of_a_LoggerMessage_method_is_skipped_but_its_call_site_is_not` | Architecture |
| 1: personal data reaches the message by interpolation | `PDLR.R1_F10_an_interpolated_template_is_reported`, `PDLR.R3_a_record_with_a_Sensitive_member_and_a_synthesized_ToString_is_reported_when_interpolated` | Architecture |
| 1: a masked value passes | `PDLR.R3_green_the_owning_object_a_Sensitive_type_and_a_list_of_owning_objects_pass` (owning object, `[Sensitive]` type, list), `PDLR.R4_the_compliant_fixtures_have_zero_findings_except_the_pass_through_masker_which_needs_its_catalogue_entry`, `PDLR.R4_masker_output_is_clean_only_when_the_masker_is_listed_so_the_green_is_not_vacuous` (`UserIdHasher.Hash`-shaped masker, non-personal ids/codes/counts). The "listed redacting type" clause has no fixture: see note N-3 | Architecture |
| 1: a scalar read from a [Sensitive] member is a finding | `PDLR.R3_a_scalar_read_from_a_Sensitive_member_is_reported_and_labelled_by_its_category_or_Marked_Sensitive` | Architecture |
| 1: the check is proven red | `PDLR.R1_*` fixtures in `Fixtures/TenancyViolations/PersonalDataLogs` (red) against `PDLR.R4_*` in `Fixtures/TenancyCompliant/PersonalDataLogs` (green) | Architecture |
| 2: all product projects are covered; a new project needs no edit | `PDLR.R9_LogScope_is_every_src_project_with_no_exclusion_and_the_AppHost_and_Migrator_are_read_from_disk`, `PDLR.R9_a_missing_or_stale_build_output_fails_and_names_the_project` | Architecture |
| 2: a suppression is visible (justification, count reported) | `PDLR.R6_*` (nine tests: counted suppression, pinned entry, stale entry, blank/short/unknown, `secret-material`, A does not hide B, seven attributes), `PDLR.R7_today_src_has_zero_findings_and_the_suppression_list_equals_the_catalogue_which_is_empty` (prints the count) | Architecture |
| 2: the catalogue is extended with no code change | `PDC.R5_extension_a_new_category_with_a_name_absent_from_the_real_file_reports_a_fixture_that_uses_it_with_no_code_change` | Architecture |
| 2: today's code is clean | `PDLR.R7_today_src_has_zero_findings_and_the_suppression_list_equals_the_catalogue_which_is_empty`, `PDLR.R7_the_green_run_is_not_vacuous_every_LoggerMessage_call_site_and_the_known_sources_were_examined` | Architecture |
| 3: the attributes are present | `TUM.The_attributes_are_present_on_Membership_UserId_and_MemberDto_UserId`; `AAM.ActorUserId_carries_Sensitive` (the third member lives in the Audit project, which must not reference Tenancy) | Unit |
| 3: a Membership value is logged as structured state | `TUM.A_Membership_value_is_logged_as_structured_state_and_no_emitted_line_contains_the_canary`, `TUM.A_Membership_passed_whole_to_the_masking_processor_renders_the_user_id_member_as_the_mask` | Unit |
| 3: a MemberDto list is logged | `TUM.A_MemberDto_list_is_logged_and_no_emitted_line_contains_the_canary`, `TUM.A_MemberDto_value_is_logged_and_no_emitted_line_contains_the_canary` | Unit |
| 3: ToString does not leak | `TUM.ToString_and_interpolation_of_a_MemberDto_and_a_Membership_do_not_leak_the_canary` | Unit |
| 3: persistence and API output are unchanged | JSON: `TUM.The_PrintMembers_override_leaves_the_MemberDto_value_and_the_JSON_unchanged` (serialized `userId` still the real value). Endpoint: existing `TenancyEndpointScenarioTests.The_Owner_lists_her_tenants_members_and_never_sees_another_tenants_members` and `TenancyPipelineIntegrationTests.A_different_caller_for_an_existing_tenant_is_refused_on_both_routes_and_creates_no_membership` assert `members[0].userId == <real sub>` (both `Integration`, unchanged, no edit needed). Model: `TUM.The_Tenancy_model_has_no_pending_changes_so_no_migration_is_needed`; Audit: existing `AuditModelAndRegistrationTests.The_migration_snapshot_matches_the_model_with_no_pending_changes` | Unit, Integration |
| 3 (option A): an AuditRecord is logged as structured state | `AAM.An_AuditRecord_is_logged_as_structured_state_and_no_emitted_line_contains_the_canary`, `AAM.An_AuditRecord_passed_whole_to_the_masking_processor_renders_the_actor_user_id_member_as_the_mask` | Unit |
| 4: the old residual is covered by a test, by name | `PDLR.R1_F11_the_15_case_LogInformation_Paid_Payee_with_a_description_is_reported` (asserts the finding's category is `free-text`) | Architecture |
| 4: earlier guarantees still hold | no file under `tests/Decisya.ServiceDefaults.Tests` or `src/Decisya.ServiceDefaults` changed (`git status`); the orchestrator's full fast run passed 2086 of 2086 at G4. See "Story 4 review list" | Unit |
| 4: the revisit is recorded | NFR-11 wording updated in `docs/requirements/nfr.md` (this gate); baseline text "Control until the analyzer exists" is G6's edit (G2 split). PR body list: `manual`, the G7 PR body carries it (a document, not behaviour) | manual |
| 5: the failure message is actionable | `PDLR.R8_the_failure_text_holds_the_file_the_line_the_category_name_and_all_three_fixes`, plus S-02 output hygiene `PDLR.R8_S_02_*` | Architecture |

### Story 4 review list (the #15 logging tests, re-read against the new rule)

None changed, none added, none found obsolete by name. I did not re-read each file line by line; their subject is runtime masking, which #230 leaves unchanged. #68 is context only.

| File | Verdict |
| --- | --- |
| `tests/Decisya.ServiceDefaults.Tests/Logging/SensitiveDataMaskingProcessorTests.cs` | unchanged; deny-list, shape and fail-closed behaviour untouched |
| `.../MaskingLogRecordProcessorTests.cs` | unchanged |
| `.../DecisyaJsonConsoleFormatterTests.cs` | unchanged; its `FormattedLogState` shape is the model for the new `MaskedLogLine` helper |
| `.../UserIdHasherTests.cs`, `LogEnrichmentContextTests.cs` | unchanged; `UserIdHasher::Hash` is the one catalogue masker, pinned by `PDLR.R10_S_04_*` |
| `.../ServiceDefaultsLoggingWiringTests.cs`, `DecisyaObservabilityOptionsValidatorTests.cs`, `UserIdHashKeyProvisionerTests.cs` | unchanged |
| `tests/Modules/Decisya.Modules.Entitlements.Tests/EntitlementsReasonConfidentialityTests.cs` | unchanged; its G4-23-05 test is the pattern for the new masking tests |

### G3 MUSTs

| Id | Proof | Status |
| --- | --- | --- |
| G4-230-01 taint survives a Decisya object | `PDLR.R4a_G4_230_01_taint_survives_a_Decisya_object_and_non_string_personal_members_are_caught` (theory: `PaymentNote` whole and interpolated, `View` initializer, `Iban` value object, `Guid UserId`; greens: owning object, field-level taint, `[Sensitive]` value object), guard pair `R4_logging_only_FailureReason_*` (green) and `R4a_logging_Subject_*` (red) | PASS |
| G4-230-02 catalogue completeness for today's sources | `PDLR.R4a_G4_230_02_the_framework_readers_of_tokens_cookies_and_jwt_payloads_are_sources` (red: `TryGetPayloadValue("sub")`, `Request.Cookies.TryGetValue`, `GetTokenValue("refresh_token")`, `HttpContext.GetTokenAsync("access_token")`; green: `expires_at`); `PDC.R5_every_entry_exists_*` | PASS |
| G4-230-03 Console, Debug, Trace are sinks | `PDLR.R4a_G4_230_03_Console_Debug_and_Trace_writes_are_sinks` (red: `Console`, `Console.Error`, `Debug`, `Trace`; green: a constant to stderr) | PASS |
| G4-230-04 exemptions counted and security-owned | `PDLR.R4a_G4_230_04_a_hand_written_GeneratedCode_method_is_examined_as_usual`, `PDLR.R6_*`, `PDC.R5_secret_material_is_pinned_as_never_suppressible_and_it_is_the_only_one` | PASS |

### G3 SHOULDs

| Id | Disposition | Proof |
| --- | --- | --- |
| S-01 existence of third-party entries | fix-now, done | `PDC.R5_every_entry_exists_in_LogScope_or_in_a_referenced_assembly_and_unreferenced_ones_are_listed_not_failed`, `PDC.R5_an_unknown_name_a_namespace_or_a_wildcard_fails_and_names_the_entry`, `PDC.R5_S_01_a_wrong_nameParameter_an_unknown_fallback_category_and_a_bad_suppression_category_fail` |
| S-02 output hygiene | fix-now, done | `PDLR.R8_S_02_the_output_has_no_drive_letter_no_users_segment_and_no_il_string_literal`, `PDLR.R8_S_02_a_path_outside_the_repository_prints_the_file_name_only_and_a_ci_prefix_is_stripped` |
| S-03 narrowings listed with green fixtures | G4 evidence | listed in `docs/ai/pipeline/230.md` (N-01, N-02 and eight carve-outs); guards `R4_logging_only_FailureReason_*` and `R4a_logging_Subject_*`; no red fixture was edited, per the manifest |
| S-04 only one `ILogEnrichmentContext` | fix-now, done | `PDLR.R10_S_04_LogEnrichmentContext_is_the_only_ILogEnrichmentContext_and_Begin_passes_userId_only_to_the_hasher` |
| S-05 closed-world dispatch | backlog #243 | none; residual accepted by G3 |
| S-06 Activity and Meter tag sinks | backlog #243 | none |
| S-07 structural record `ToString` rule | backlog #243 (Marco) | none; the use-site rule R3 covers `src` sinks |
| S-08 CA1848 and CA2254 as errors | backlog #243 (Marco) | none |
| S-09 review-required paths in `.claude/project.json` | backlog #243 | none; not a #230 file |
| S-10 framework-callback parameter sources | backlog #243 | none |

### NFR-62 targets

| Target | Evidence |
| --- | --- |
| 7 of 7 categories proven red | `R1_F2_to_F8_*`, `R1_every_category_of_the_catalogue_has_a_red_fixture` |
| 0 plain personal-data arguments in `src/` at merge | `R7_today_src_has_zero_findings_*` (15 assemblies, about 2290 methods, 43 `[LoggerMessage]` call sites) |
| 0 undetected cases among the fixtures | every red fixture is asserted red; the theories enumerate them. Residual false negatives are the G2/G3 accepted list, not fixture gaps |
| Earlier #15 logging tests failing: 0 | ServiceDefaults tests unchanged; full fast suite 2086 of 2086 at G4 |
| `UserId` properties masked: 3 of 3 | `TUM.*` (2 members) and `AAM.*` (1 member), canary `sub-canary-7f3a` |
| Added build or unit-run time at most 10% | Measured by the orchestrator at G4: `ArchitectureTests` 12.8 s in the parallel fast suite (9 s alone, 254 tests), against a suite wall clock of 231 s set by `Bff.Tests` at 3 min 40 s. The rule sits off the critical path, so the wall clock is not lengthened. **No pre-change number exists for either measure** (neither the old `ArchitectureTests` duration nor a before-run of the suite was recorded), so the 10% ratio cannot be computed; recorded as note N-1 |

### Notes

- **N-1 (NFR-62 baseline).** The "at most 10%" target was recorded by G5 as a measurement, not a ratio: the before value is missing. To close it, run the fast lane once on `main` and compare `ArchitectureTests` and total wall clock. The first figure rises by the rule's cost (roughly 9 s of 12.8 s at most, as the other arch rules take seconds); the second is dominated by `Bff.Tests` and unaffected. Not blocking in my view; Marco decides.
- **N-2 (not run).** I ran no tests (no command in my patterns). The new G5 tests are compiled-by-eye against the existing patterns and have never been executed: the orchestrator must run `Decisya.Modules.Tenancy.Tests` and `Decisya.Modules.Audit.Tests` (fast lane), and a build, before G6. The Integration lane (Docker) stays to be run before the PR for the two cited JSON tests.
- **N-3 (redacting types).** Story 1's "a listed redacting type" has no behavioural fixture, because the catalogue lists none; only the entry validity check exists (`PersonalDataWorld`, `redactingTypes` must carry `[Sensitive]` or its own `ToString`), reached only with a non-empty list. Backlog (#243), Low.
- **N-4 (Story 3 split).** The three members cannot share one reflection test: Audit must not reference Tenancy (ADR-0005), so the Tenancy test checks two members and the Audit test the third.
- **N-5 (trait on legacy classes).** `tests/Decisya.ServiceDefaults.Tests/Logging/*` classes carry no `[Trait("Category", …)]` at class level in the files I read; out of #230's scope, not changed.
- **N-6 (G1 text).** The G1 "masked" scenario already reads in the owning-object form, and Note 2 already says `ActorUserId` carries `[Sensitive]`; no further G1 edit was needed. NFR-62 already lists seven categories and the module test projects.

<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #230 -->
