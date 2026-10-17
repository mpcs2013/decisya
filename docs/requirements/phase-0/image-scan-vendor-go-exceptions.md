# Phase 0 – Time-boxed Go exceptions for vendor-binary images (ADR-0015 item 4)

## Issue #142 — image scan: ADR-0015 item 4 (Caddy) and collector 0.163.1

### Scope note (role framing)

No tenant-facing capability. The actors are **Marco as maintainer/operator** and **the CI pipeline**
(`image-scan`). **Entitlement plan: N/A** for every story (CI, supply-chain policy and deploy
tooling; no `module.feature` key applies), as in `docs/requirements/phase-0/dependabot-ci.md`.

Decision source: Marco, 2026-10-17 (`docs/ai/pipeline/142.md`, five limits; reverses the
2026-10-10 "no exceptions" call and implements "way forward 1" of the 2026-10-10 G3 BLOCK). Item 4
revives the `VENDOR_BINARY_ALIASES` option withdrawn by G4-120-07; the new G3 must supersede
G4-120-07 for this case only. No open questions.

Facts (data): GO-2026-6603 to 6613 (published 2026-10-08, fixed in Go 1.26.9 and x/net 0.60.0)
hit `library/caddy:2.11.7-alpine` (stdlib go1.26.8, x/net v0.59.0); Caddy has no fixed release on
2026-10-17. The collector 0.163.1 (released 2026-10-16) is fixed. Under strict D-2 the fixed
`go-module` findings cannot be excepted.

Terms: **item-4 exception** = an `exceptions.json` entry for alias `caddy` whose finding is a fixed
`go-module` finding. **Rule** = the item-4 evaluation in `.github/scripts/image_scan.py`.

---

### Story 1 — Only Caddy go-module findings can be excepted (limit 1)

As Marco (maintainer), I want the item-4 rule to cover fixed `go-module` findings of the `caddy`
alias and nothing else, so that the strict D-2 stays in force everywhere a vendor fix is not
known to be missing. (Done when 3)

```gherkin
Feature: Item 4 scope is one alias and one package type

  Background:
    Given the code constant VENDOR_BINARY_ALIASES in image_scan.py
    And an active exception with a 14-day span for each finding below

  Scenario: A fixed go-module finding in caddy is covered
    Given the "caddy" alias has a finding CVE-2026-3333 of type "go-module", fixed in "0.60.0", severity Critical
    When image_scan.py evaluates the scan
    Then the finding is covered by the exception and the alias does not fail

  Scenario Outline: Anything else keeps strict D-2 and still blocks
    Given a fixed finding <id> of type <type> in <target> with an active exception
    When image_scan.py evaluates the scan
    Then the exception is void ("bump the image") and the scan fails

    Examples:
      | id            | type         | target                 |
      | CVE-2026-3333 | go-module    | alias "keycloak"       |
      | CVE-2026-3333 | go-module    | alias "otelcollector"  |
      | CVE-2026-3333 | go-module    | the release image      |
      | CVE-2026-1111 | java-archive | alias "caddy"          |
      | CVE-2026-2222 | binary       | alias "caddy"          |

    # Finding ids in exceptions.json are CVE or GHSA ids (ID_RE); GO-2026 ids are not accepted.
    # The ids in these examples are illustrative.

  Scenario: The alias set is a code constant
    Then VENDOR_BINARY_ALIASES equals exactly {"caddy"}
    And adding an alias to exceptions.json, a workflow input or an environment variable does not change it
    And a test asserts the exact set, so a change to it needs an ADR amendment and a G3 review

  Scenario: apk, deb and rpm findings keep item 3 unchanged
    Given a fixed "apk" finding in alias "caddy" with a 30-day exception and an unchanged tag
    When image_scan.py evaluates the scan
    Then item 3 decides the outcome exactly as before this issue
    And a 14-day cap is not applied to it, and item 4 is not consulted
```

### Story 2 — Item-4 exceptions are short-lived (limit 2)

As Marco (maintainer), I want an item-4 exception to last at most 14 days, with no probe for a
newer release, so that an unseen vendor fix is caught by expiry, Dependabot and the cooldown
rather than by a check that cannot see it. (Done when 3, 5)

```gherkin
Feature: Item 4 is bounded by expiry only

  Scenario Outline: The span from added to expires is checked
    Given an item-4 exception with added "<added>" and expires "<expires>"
    When exceptions.json is validated and the scan is evaluated on "<added>"
    Then the result is "<result>"

    Examples:
      | added      | expires    | result                                           |
      | 2026-10-17 | 2026-10-31 | covered (span 14 days)                           |
      | 2026-10-17 | 2026-11-01 | rejected, span 15 days exceeds the item-4 limit  |

  Scenario: An expired entry does not apply
    Given an item-4 exception with expires "2026-10-31"
    When the scan is evaluated on "2026-11-01"
    Then the finding is not covered and the scan fails

  Scenario: No newer-release probe
    When the scan evaluates an item-4 exception
    Then image_scan.py makes zero registry or network calls for it
    And only the dates decide whether it applies
```

### Story 3 — stackctl refuses to start an image with an item-4 exception (limit 3)

As Marco (operator), I want `stackctl up` to refuse while any item-4 exception is present in
`exceptions.json`, expired or not, so that a known-vulnerable vendor binary is never started by
accident and the guard needs no clock. (Done when 4; Marco, 2026-10-17, stricter than "active")

```gherkin
Feature: Deploy guard for item-4 exceptions

  Scenario: Start is refused while an item-4 entry is present
    Given exceptions.json holds an item-4 entry for alias "caddy" (expired or not)
    And the stack includes the caddy image
    When I run "stackctl up"
    Then the command exits non-zero before any container is started
    And the message names the image, the finding ids and the expiry date, and says the image has a known unfixed vulnerability

  Scenario: An expired item-4 entry still makes up refuse
    Given exceptions.json holds an item-4 entry for alias "caddy" with expires "2026-10-31"
    And today is "2026-11-05"
    When I run "stackctl up"
    Then the command exits non-zero before any container is started
    And the guard did not read the clock to decide

  Scenario: Start proceeds only once no item-4 entry remains
    Given the Caddy bump PR has removed every item-4 entry from exceptions.json
    When I run "stackctl up"
    Then the guard passes and the stack starts as before

  Scenario: The guard fails closed
    Given exceptions.json is missing, unreadable or invalid
    When I run "stackctl up"
    Then the command refuses to start with a message that names the file
```

### Story 4 — The rule has a sunset (limit 4)

As Marco (maintainer), I want the rule to stop applying on 2026-11-30 and a test to go red, so
that a temporary exception cannot become permanent policy unnoticed. (Done when 5)

```gherkin
Feature: Item 4 sunset

  Scenario Outline: The rule applies only up to the sunset date
    Given the code constant VENDOR_BINARY_SUNSET is "2026-11-30"
    And an otherwise valid 14-day item-4 exception
    When the scan is evaluated on "<today>"
    Then the finding is "<result>"

    Examples:
      | today      | result                    |
      | 2026-11-30 | covered                   |
      | 2026-12-01 | not covered, scan fails   |

  Scenario: A test turns red after the sunset
    Given the clock is 2026-12-01 or later
    When the .claude test suite runs
    Then a test fails with a message that item 4 must be removed or decided again
    And the sunset date cannot be moved by exceptions.json, a workflow input or an environment variable
```

### Story 5 — Collector pinned fixed; Caddy exceptions are attributable (limit 5 and Done when 1-2)

As Marco (maintainer), I want the collector on 0.163.1 with no exception and every Caddy
exception to name its approval, so that the audit trail is complete and the fallback date is
visible. (Done when 1, 2)

```gherkin
Feature: Pins and exception provenance

  Scenario: The collector is pinned without an exception
    Given ContainerImages.cs
    Then the OtelCollector tag is "0.163.1" with digest "sha256:f0e23036dce3db3f9cb80d907c75a0d034cd32e93511a1943482d46129b9ae9c"
    And the regenerated docker-compose.yaml carries the same pin
    And exceptions.json holds zero entries for alias "otelcollector" that cover go-module findings
    And the scan of the collector reports no fixed High or Critical finding

  Scenario: Every Caddy item-4 exception records its approval
    Given each exceptions.json entry for alias "caddy" of type "go-module"
    Then its justification contains "#142", "Marco" and the approval date "2026-10-17"
    And an entry missing any of the three is rejected by validation

  Scenario: ADR-0015 records the accepted item 4
    Then ADR-0015 carries item 4 with the five limits, including the 2026-10-31 fallback
      (open a self-build issue that day if Caddy has no fixed release)
```

---

### NFR and security notes

- **Fail closed on any unknown state:** unparsable dates, a missing `added`/`expires`, an unknown
  alias, unknown package type, an unreadable `exceptions.json`, or a clock error means no exception
  applies (scan red; `stackctl up` refuses).
- **Rule is code, never configuration:** the alias set, the 14-day bound, the sunset date and the
  package type are constants in `image_scan.py`; `exceptions.json`, workflow inputs and environment
  variables cannot widen, extend or disable them (same principle as `BINARY_ONLY_ALIASES` and
  `OS_PACKAGE_TYPES`).
- The release-image path keeps strict D-2. No cooldown bypass is added; the one-off Caddy waiver of
  item 2 is unchanged and sets no precedent.
- New measurable targets: NFR-57 and NFR-58 in `docs/requirements/nfr.md`.

### Plan mapping

| Story | Entitlement feature key | Plan |
| --- | --- | --- |
| 1 to 5 | N/A (CI and tooling) | N/A |

### Traceability

G5, 2026-10-17. Paths are relative to the repo root. `SCAN` = `.claude/tests/test_image_scan.py`, `CTL` =
`deploy/tests/test_stackctl.py`, `PARITY` = `tests/Decisya.Identity.Tests/ContainerImageParityTests.cs`.
Python suites were run by the main session (G4 evidence in `docs/ai/pipeline/142.md`); the .NET parity
class was run by the test engineer (13 passed, 0 failed). Ids in the Gherkin are illustrative (S-7): the
tests use CVE-shaped ids (`CVE-2026-78663` and similar), never `GO-2026-*`.

| Story / scenario | Test(s) |
| --- | --- |
| 1 / A fixed go-module finding in caddy is covered | `SCAN::VendorBinaryItem4Tests::test_a_fixed_caddy_go_module_finding_is_excepted_under_item_4` (stdlib and golang.org/x/net; also `test_item_4_ignores_the_tag_state`) |
| 1 / Outline: go-module in keycloak, otelcollector | `SCAN::VendorBinaryItem4Tests::test_other_aliases_keep_strict_d2_for_go_modules` (unchanged tag, 14-day entry) |
| 1 / Outline: go-module in the release image | `SCAN::NarrowD2Tests::test_8_the_release_path_without_a_tag_state_keeps_strict_d2` (alias `api`); `SCAN::NarrowD2Tests::test_4_a_fixed_go_module_finding_still_blocks_with_an_unchanged_tag` and `SCAN::VendorBinaryItem4Tests::test_without_the_scanned_reference_item_4_never_applies` (no scanned reference, no item 4); `test_a_repointed_caddy_reference_loses_item_4` |
| 1 / Outline: java-archive in caddy | `SCAN::VendorBinaryItem4Tests::test_other_package_types_in_caddy_keep_strict_d2`; `SCAN::NarrowD2Tests::test_5_a_fixed_java_finding_still_blocks_with_an_unchanged_tag` |
| 1 / Outline: binary in caddy | `SCAN::VendorBinaryItem4Tests::test_other_package_types_in_caddy_keep_strict_d2` (`binary`, plus `python`); `test_a_go_module_name_without_a_slash_is_not_covered` |
| 1 / The alias set is a code constant | `SCAN::VendorBinaryItem4Tests::test_the_rule_is_code_with_exact_values` (exact set, bound, sunset, `ENTRY_KEYS`; no `os.environ`, `getenv`, `_http_`, `tag_state` in the rule's source) |
| 1 / apk, deb, rpm keep item 3 | `SCAN::NarrowD2Tests::test_1_...`, `test_2_...`, `test_3_...`, `test_3b_...`, `test_6_an_exception_longer_than_30_days_keeps_strict_d2`; `SCAN::VendorBinaryItem4Tests::test_non_item_4_entries_are_not_held_to_item_4`; `SCAN::VendorBinaryItem4Tests::test_a_caddy_os_package_stays_under_item_3_when_the_reference_is_passed` (caddy apk zlib, 27-day entry, item 3 applies, rule is not item 4; blocks when moved) |
| 2 / Outline: 14-day span covered | `SCAN::VendorBinaryItem4Tests::test_a_fixed_caddy_go_module_finding_is_excepted_under_item_4` (default entry spans 2026-10-17 to 2026-10-31); `test_the_validator_rejects_every_broken_item_4_entry` (accepts the valid one) |
| 2 / Outline: 15-day span rejected | `SCAN::VendorBinaryItem4Tests::test_the_validator_rejects_every_broken_item_4_entry` ("15-day span"); `test_a_caddy_entry_breaking_a_bound_blocks_even_with_an_unchanged_tag` (15- and 30-day cases block in `evaluate`) |
| 2 / An expired entry does not apply | `SCAN::VendorBinaryItem4Tests::test_an_expired_item_4_entry_no_longer_covers` (covered on 2026-10-31, not on 2026-11-01, with ref and unchanged tag); the guard still sees expired entries: `test_the_guard_entry_point_returns_expired_entries_and_never_a_partial_list` |
| 2 / No newer-release probe | `SCAN::VendorBinaryItem4Tests::test_the_rule_is_code_with_exact_values` (no `_http_`, `resolve_tag_state`, `tag_state` in the item-4 source); `test_item_4_ignores_the_tag_state` |
| 3 / Start is refused while an item-4 entry is present | `CTL::VendorBinaryGuardTests::test_an_item_4_entry_makes_up_refuse_with_zero_runner_calls_and_names_image_ids_and_expiry`; `test_check_reports_the_same_problem` |
| 3 / An expired entry still makes up refuse | `CTL::VendorBinaryGuardTests::test_an_expired_entry_still_refuses_and_the_message_still_names_its_expiry`; `test_the_guard_keys_on_presence_so_no_date_lifts_it` (injected clock on expires, expires + 1, sunset + 1) |
| 3 / Start proceeds once no item-4 entry remains | `CTL::VendorBinaryGuardTests::test_removal_is_the_only_way_through`; `test_entries_the_predicate_does_not_name_do_not_refuse` |
| 3 / The guard fails closed | `CTL::VendorBinaryGuardTests::test_a_missing_unreadable_or_invalid_file_refuses_and_names_the_file`; `test_a_module_that_cannot_be_loaded_refuses`; `SCAN::VendorBinaryItem4Tests::test_the_guard_entry_point_returns_expired_entries_and_never_a_partial_list` |
| 4 / Outline: 2026-11-30 covered | `SCAN::VendorBinaryItem4Tests::test_the_sunset_ends_item_4` |
| 4 / Outline: 2026-12-01 not covered | `SCAN::VendorBinaryItem4Tests::test_the_sunset_ends_item_4`; `test_a_caddy_entry_breaking_a_bound_blocks_even_with_an_unchanged_tag` ("expires after the sunset") |
| 4 / A test turns red after the sunset | `SCAN::VendorBinaryItem4Tests::test_sunset_tripwire` (red from 2026-12-01 by design); `test_the_rule_is_code_with_exact_values` (sunset and bounds are not read from data or environment) |
| 5 / The collector is pinned without an exception | `PARITY::The_collector_is_pinned_to_0_163_1_with_its_digest_in_ContainerImages_the_sandbox_list_and_Compose` (new at G5: tag, digest, sandbox list and `deploy/compose/docker-compose.yaml`); `PARITY::The_sandbox_image_list_has_an_otelcollector_alias_matching_ContainerImages`; `deploy/tests/test_compose_guards.py::...::test_each_keycloak_production_and_image_rule_can_fail` (Compose image must match `ContainerImages.cs`); `SCAN::VendorBinaryItem4Tests::test_the_committed_142_entries_are_caddy_item_4_entries_only` (no collector Go entry). Last clause (scan reports no fixed High/Critical for the collector): manual, see below |
| 5 / Every Caddy item-4 exception records its approval | `SCAN::VendorBinaryItem4Tests::test_the_committed_142_entries_are_caddy_item_4_entries_only` (committed data: `#142`, approval date 2026-10-17); `test_the_validator_rejects_every_broken_item_4_entry` ("issue missing", "no approval", "two approvals", "not a real date", "approval after added") |
| 5 / ADR-0015 records the accepted item 4 | `PARITY::ADR_0015_records_item_4_with_the_fallback_and_the_sunset` (new at G5: "item 4", 2026-10-31, 2026-11-30); the five limits in prose: manual, reviewed at G6 |

**Manual checks (justified).**
- Story 5, "the scan of the collector reports no fixed High or Critical finding": it needs Grype, Docker and the network, which no unit lane runs. Evidence: the local scan with the pinned Grype v0.120.0 exited 0 on 2026-10-17 (otelcollector 0.163.1: 0 matches), and the `image-scan` job on the PR is the standing check (M-10).
- Story 5, ADR wording of the five limits: prose review at G6; the test above pins the item, the fallback date and the sunset date only.
- The 2026-10-31 fallback (open a self-build issue) is a process step (S-6); no code can check it.

**G3 MUSTs M-6 to M-10.**

| MUST | Test(s) | Status |
| --- | --- | --- |
| M-6 conjunction in `evaluate()` | reference: `test_without_the_scanned_reference_item_4_never_applies`, `test_a_repointed_caddy_reference_loses_item_4`; type: `test_other_package_types_in_caddy_keep_strict_d2`; predicate: `test_a_go_module_name_without_a_slash_is_not_covered`; span, approval + 14, sunset: `test_a_caddy_entry_breaking_a_bound_blocks_even_with_an_unchanged_tag`, `test_the_sunset_ends_item_4`; no entry: `test_a_fixed_caddy_go_module_finding_without_an_entry_blocks`; no registry helper, no env, `tag_state` ignored: `test_the_rule_is_code_with_exact_values`, `test_item_4_ignores_the_tag_state`; exact constants and `ENTRY_KEYS`: `test_the_rule_is_code_with_exact_values`; item 3 and `release_images.py` unchanged: `SCAN::NarrowD2Tests::test_1_...` to `test_9_...`, `test_8_...` | Covered (all in `VendorBinaryItem4Tests` unless noted) |
| M-7 validation fails closed | `test_the_validator_rejects_every_broken_item_4_entry` (span, issue, exactly one approval, real date, approval <= added, approval + 14, sunset); `test_the_guard_entry_point_returns_expired_entries_and_never_a_partial_list` (raises, no partial list); `test_non_item_4_entries_are_not_held_to_item_4` | Covered. Exit 2 in CI is not asserted by a test (the raise is) |
| M-8 deploy guard | zero Runner calls and message: `CTL::VendorBinaryGuardTests::test_an_item_4_entry_makes_up_refuse_with_zero_runner_calls_...`; `check` reports it: `test_check_reports_the_same_problem`; presence, no clock: `test_the_guard_keys_on_presence_so_no_date_lifts_it`; single source and no override: `test_no_override_flag_no_environment_variable_and_the_predicate_is_the_scans_own`; fail closed naming the file: `test_a_missing_unreadable_or_invalid_file_refuses_and_names_the_file`, `test_a_module_that_cannot_be_loaded_refuses`; reads through `--repo`: `test_the_guard_reads_through_the_repo_argument_not_the_live_file`; fixture repo, injected date: all tests | Covered |
| M-9 the data | `SCAN::VendorBinaryItem4Tests::test_the_committed_142_entries_are_caddy_item_4_entries_only` (16 caddy entries, stdlib or x/net, CVE or GHSA ids, `#142`, added 2026-10-17, expires <= 2026-10-31, approval date, no collector Go entry, file valid) | Covered. Not asserted: that the `$comment`-style M-3 wording (reachable once deployed, void once Caddy runs, Windows-only on 6604) is present; the validator checks only the approval phrase and the issue. Reviewed at G6 |
| M-10 collector pin | `PARITY::The_collector_is_pinned_to_0_163_1_...` (new), `PARITY::The_sandbox_image_list_has_an_otelcollector_alias_...`; Compose regeneration is covered by the drift check in `deploy/tests/test_ci_workflow.py::...::test_the_drift_check_runs_the_built_apphost_and_compares_with_the_committed_file` and `CTL::AssembleTests::test_drift_between_published_and_committed_stops_assemble` | Covered. Cooldown end 2026-10-23 and the no-merge-before rule: process, checked at G6/G7 |

**Gaps N-1 and N-2: closed 2026-10-17.** The main session added `test_an_expired_item_4_entry_no_longer_covers` and `test_a_caddy_os_package_stays_under_item_3_when_the_reference_is_passed` (`test_image_scan.py` 41 OK, 1 skipped; full `.claude/tests` OK).

**Residuals for G6 (not test gaps):** the M-9 justification wording (M-3 statements) is not asserted by a test; the ADR wording of the five limits; the collector cooldown end 2026-10-23 and the no-merge-before rule; the 2026-10-31 fallback issue.

Test engineer additions (this gate): two tests and a `[Trait("Category", "Unit")]` on the class in `tests/Decisya.Identity.Tests/ContainerImageParityTests.cs`. `dotnet test --project tests/Decisya.Identity.Tests --filter-method "*ContainerImageParityTests*"`: 13 passed, 0 failed.

<!-- gate: G1 | verdict: PASS | issue: #142 -->
<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #142 -->
