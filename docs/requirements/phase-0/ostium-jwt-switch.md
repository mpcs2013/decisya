# Phase 0 – Identity switch 1: Ostium Jwt and Backchannel packages

## Issue #164 – adopt `Ostium.Authentication.Jwt`, `Ostium.Authentication.Jwt.Testing` and `Ostium.Backchannel` 0.2.0

### Scope note

This is a **behaviour-preserving switch** (ADR-0019, decision 3). No user-visible
behaviour of `Decisya.Api`'s JWT validation or of the back-channel trust changes.
The original behaviour is specified in `docs/requirements/phase-0/api-jwt-validation.md`
(issue #20, "the original"). This file references the original's scenarios instead of restating
them, and adds only what the switch introduces: the supply-chain chain, the fail-closed
configuration, the kept Decisya-owned parts, telemetry and logging, and two retargeted tests.

Inputs read as data: the manifest `docs/ai/pipeline/164.md`, ADR-0019,
`docs/architecture/ostium-dependency.md`, the original, and the supplier items O-005, O-006
and O-010 in the project exchange.

**Roles.** Stories are written "As a tenant user" where the control protects her identity and
tenant (same framing as the original), "As the platform operator" for start-up and
observability properties, and "As the maintainer (Marco)" for the supply chain.

**Entitlement plan:** N/A for every story. Authenticating API calls, the restore chain and
telemetry are core platform infrastructure, identical on every subscription plan. No
entitlement feature key (`module.feature`) gates any story. Nothing here is financial advice,
so no disclaimer criterion applies.

**Out of scope.**
- The BFF, the Keycloak test fixture and the realm guard invariants: later switches
  (ADR-0019, decision 3). The BFF keeps its own code and trust until its switch.
- Ostium's own bump to .NET 10.0.13 (a separate exchange item). G4 only checks that the
  resolved graph is 10.0.13 under central transitive pinning.
- The ASVS V6/V7 report: no supplier report exists for 0.1.1 or 0.2.0 (O-010 point 6; ADR-0019 A4:
  the 0.2.0 checksums have no `file` line). Recorded as a known gap, not a test; it stays open for
  the Phase 0 exit audit.
- Any change to the original's 60 s clock skew, `sub` name claim, fallback policy or
  `/api/whoami`.

**Deliverable independently in one PR:** the issue is the single PR. Stories 1 to 7 land
together because Story 2 needs the packages restored by Story 1; each is separately testable.

---

### Story 1 – The packages are restored only from the pinned feed, in locked mode, and match the supplier's signed hashes

As the maintainer, I want every `Ostium.*` package restored at exactly 0.2.0 from the private
`mpcs2013` feed only, in locked mode, with hashes equal to the supplier's signed values, so that
a swapped, repointed or tampered package fails the restore instead of running in the auth path.

#### Acceptance criteria

```gherkin
Feature: Ostium packages are pinned by version, source and content hash

  Scenario: Pins, source mapping and lock files are in the repository
    Given the repository at the head of the issue branch
    Then Directory.Packages.props pins Ostium.Authentication.Jwt, Ostium.Authentication.Jwt.Testing and Ostium.Backchannel at exactly "0.2.0", with no range and no floating version
    And NuGet.config starts with <clear/> and declares the GitHub Packages source of "mpcs2013" plus nuget.org
    And the package source mapping sends "Ostium.*" to the mpcs2013 source only, and every other package id to nuget.org only
    And every restorable project has a committed packages.lock.json

  Scenario: Locked restore in CI and in the pre-push check
    Given the CI workflow for this issue's pull request
    And its permissions include "packages: read"
    When the restore step runs with GITHUB_TOKEN as the feed credential and no personal access token
    Then "dotnet restore --locked-mode" succeeds
    And the pre-push check runs the same locked-mode restore
    And a deliberately edited lock-file hash makes that restore fail with a nonzero exit code and no fallback to another source

  Scenario: Lock-file hashes equal the supplier's published values
    Given the committed packages.lock.json files
    Then every "Ostium.*" entry resolves to version "0.2.0"
    And the contentHash of Ostium.Authentication.Jwt equals "KdmXfluauUZXFFLg3J/Z5A1U7ii7eplb9Yd+/v8omjEI3ZliSvmoXQ4dxniTCPh1sPgPEwkuFC9ciYvWVxsBQA=="
    And the contentHash of Ostium.Authentication.Jwt.Testing equals "W35sXHu8TbbU4tDDSIMI2lfr6O8nBbLR0UOC/1MgoLqWL82/zIps/AgOBRSlBaAALOtm6gLSVRi+noWhmejebw=="
    And the contentHash of Ostium.Backchannel equals "Fd2d9osM5x/L0743J6g0Q3YEZKPvhksOO5+HOamEz2Vjue0zJc+xBDbVBEoMTtk8cnHz65LZ1Q9oWRzqalfFMg=="
    And version "0.1.0" is referenced nowhere

  Scenario: The signer line is pinned and Marco's check passes before merge
    Given the file ".github/allowed-signers/ostium" committed on the branch
    Then it holds exactly one line, ending in LF, with no comment line
    And that line is exactly "release@ostium namespaces=\"git,ostium-checksums\" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAILkewFGdwh1FFykHc8gI0B6/hzjHppYG1CUOR20orqgX ostium-release-tags"
    And its key fingerprint is "SHA256:rRaNoF/hJgpjYhrZ61pZ7jnRdOnQYYvDo/eEZpWCctI"
    When Marco runs the supplier's check script from his Ostium clone on this branch after a locked restore
    Then the block he pastes into the PR ends with "RESULT: PASS"
    And the PR is not merged while the block ends with "RESULT: FAIL" or is absent
```

NFR: NFR-59 (new).

---

### Story 2 – A tenant user's token is validated exactly as before

As a tenant user, I want `Decisya.Api` to accept and reject bearer tokens exactly as it did
before the switch, so that moving the mechanism into a shared package neither locks me out nor
lets anyone borrow my identity or tenant.

#### Acceptance criteria

```gherkin
Feature: JWT validation behaviour is unchanged when served by Ostium.Authentication.Jwt

  Scenario: Every acceptance scenario of the original still holds against the packages
    Given Decisya.Api authenticates through Ostium.Authentication.Jwt with Api:Jwt:Audience "decisya-api"
    When the scenarios of Stories 1 to 4 in docs/requirements/phase-0/api-jwt-validation.md run unchanged in expectation
    Then each passes with the same status codes, bodies and headers
    And the rejection set (alg=none, HS256 random secret, HS256 with the realm public key, wrong aud, wrong iss, expired, missing, non-Bearer scheme, malformed) returns 401 with an empty body and a bare "WWW-Authenticate: Bearer"
    And an RS256 and an ES256 token authenticate, and no other algorithm does
    And NFR-26 and NFR-27 still hold (60 s skew; a token 45 s past exp is accepted and 75 s past exp is rejected)
    And access tokens older than the 300 s maximum lifespan of NFR-15 are not accepted beyond the 60 s skew
    And the full identity suite (Integration with Keycloak, realm guards, architecture tests) passes with no test expectation weakened

  Scenario: A roles string array reaches the API as a JSON array and maps as before
    Given a test token issued with IssueAccessToken("dev-admin-sub", additionalClaims) where additionalClaims holds tenant_id, acr and roles as the string array ["tenant-user", "platform-admin"]
    When a GET request is sent to an admin-gated endpoint with that token
    Then the "roles" claim in the token payload is a JSON array of two strings, not a single joined string
    And the principal is in both roles, and the role-based result equals the result before the switch
    And if the claim shape is wrong, the test fails and the defect is reported to the supplier in the exchange, with no adapter or workaround in Decisya
```

---

### Story 3 – The API refuses to start with a missing or unsafe audience or event setting

As the platform operator, I want `Decisya.Api` to refuse to start when the audience or the
token-rejected event setting is missing or unsafe, so that a configuration slip can never leave
the API accepting tokens for the wrong audience or rejecting tokens silently.

#### Acceptance criteria

```gherkin
Feature: Required JWT configuration fails closed at start-up

  Scenario: The shipped configuration carries the required values
    Given Decisya.Api's configuration as committed
    Then "Api:Jwt:Audience" equals "decisya-api"
    And "Api:Jwt:Events:TokenRejected:Name" equals "auth.token.rejected"
    And "Api:Jwt:Events:TokenRejected:Id" equals 2003

  Scenario Outline: A bad audience stops start-up
    Given Decisya.Api is started with "Api:Jwt:Audience" set to "<value>"
    When the host builds
    Then start-up fails with an exception before any request is served
    And the failure message names the setting key and no token or claim value

    Examples:
      | value                |
      | (not set)            |
      | (empty)              |
      | (three spaces)       |
      | " decisya-api" (padded) |
      | *                    |

  Scenario Outline: A missing event setting stops start-up
    Given Decisya.Api is started with "<setting>" missing or empty
    When the host builds
    Then start-up fails with an exception before any request is served

    Examples:
      | setting                                |
      | Api:Jwt:Events:TokenRejected:Name      |
      | Api:Jwt:Events:TokenRejected:Id        |
```

NFR: NFR-60 (new).

---

### Story 4 – Decisya's own parts are kept and unchanged

As a tenant user, I want the caller-context and admin-MFA behaviour that belongs to Decisya to
stay as it is, so that tenancy and admin protection keep working after the generic mechanism
moved out.

#### Acceptance criteria

```gherkin
Feature: Decisya-owned identity behaviour survives the switch

  Scenario: Caller-context and tenant exposure are unchanged
    Given Decisya's caller-context building stays in Decisya.Api (not moved into Ostium's ICallerContextHook)
    When the Story 3 scenarios of the original and the tenancy tests run
    Then userId comes from "sub" and tenantId from "tenant_id" of the validated token only
    And a client-supplied X-Tenant-Id or X-User-Id header changes nothing

  Scenario: Admin MFA enforcement and its event are unchanged
    Given a validated admin token whose acr lacks the MFA value
    When a request is sent to /api/admin/**
    Then the response is 403 with a generic body, zero database commands and zero audit records
    And exactly one Warning is logged with event name "auth.admin.mfa_required" and id 2004, emitted by Decisya's code, not by Ostium
    And NFR-44 still holds
```

---

### Story 5 – Telemetry and logs keep working and keep masking

As the platform operator, I want the packages' traces, metrics and logs collected, and their
sensitive fields masked, so that observability and the no-secrets-in-logs rule survive the move.

#### Acceptance criteria

```gherkin
Feature: Ostium telemetry is collected and masked

  Scenario: Ostium sources and meters are registered
    Given a Decisya.ServiceDefaults-instrumented host
    Then its OpenTelemetry configuration subscribes to the "Ostium.*" ActivitySource and Meter names
    And NFR-12 still holds (OTLP exporter only)

  Scenario: Ostium's sensitive marker is masked and the log category changed once
    Given a value logged by Ostium.Authentication.Jwt carrying Ostium's sensitive-data marker
    When the line is written to stdout
    Then the value appears masked, never in plaintext, and NFR-11 still holds
    And a marked value whose maskability cannot be determined is also masked (fail closed)
    And the JWT block logs under category "Ostium.Authentication.Jwt", not "Decisya.Api.Authentication.JwtBearerOptionsSetup"
    And no log query, alert or document still filters on the old category
```

---

### Story 6 – The back-channel trust test targets the package type while the BFF keeps its own trust

As the maintainer, I want the kept Caddy root-trust test to exercise `Ostium.Backchannel.BackchannelRoot`, and the BFF to stay untouched, so that trust behaviour is proven against the shipped code and the frozen BFF is not changed early.

#### Acceptance criteria

```gherkin
Feature: Back-channel trust is retargeted for the API only

  Scenario: CaddyRootTrustTests runs against Ostium.Backchannel.BackchannelRoot
    Given the kept CaddyRootTrustTests
    When it runs
    Then it references Ostium.Backchannel.BackchannelRoot and no Decisya copy of the type
    And its trust expectations (a configured root is trusted, any other chain is rejected, nothing is trusted when unconfigured) are unchanged

  Scenario: The architecture tests follow the move and the BFF is untouched
    Given ApiBoundaryTests, including Only_Decisya_Api_Authentication_depends_on_JwtBearer_or_IdentityModel and the Api csproj package-reference rule
    Then both are retargeted to the Ostium packages and still fail on a deliberate violation
    And no file under src/Decisya.Bff is changed by this issue
    And the BFF still uses its own back-channel trust until its own switch issue
```

---

## Open questions for Marco

None. Assumptions taken from the sources, flagged for G2 rather than blocking:
- The old Decisya copy of the moved types is deleted in this PR (ADR-0019: Decisya keeps no reusable identity mechanism); G2 decides the exact form.
- Dependabot lock-file support must be checked before merge (manifest, FU-59-1); G2 records the result.

## NFRs added

NFR-59 (Security, supply chain) and NFR-60 (Reliability, fail-closed start-up), appended to `docs/requirements/nfr.md`. NFR-11, -12, -15, -26, -27 and -44 are referenced, not changed.

<!-- gate: G1 | verdict: PASS | issue: #164 -->

## Traceability

Gate G5 (test-engineer), issue #164. Paths are relative to the repository root. `Api` = `tests/Decisya.Api.Tests`,
`Arch` = `tests/Decisya.ArchitectureTests`, `SD` = `tests/Decisya.ServiceDefaults.Tests`.
Tests added by G5 are marked **(G5)**. No NuGet package or project reference was added by G5.

2026-10-18: Marco switched #164 to 0.2.0 before merge. 0.2.0's only change is the opt-in `resource_metadata` challenge, which Decisya does not use. Run results below and the test names that still say 0.1.1 describe the earlier 0.1.1 run.

**Run results (G5, host, 2026-10-17).**
- `dotnet test tests/Decisya.Api.Tests --filter-not-trait "Category=Integration"`: 373 passed, 0 failed (337 before G5, so 36 new).
- `dotnet test tests/Decisya.Api.Tests --filter-trait "Category=Integration"`: 68 passed, 0 failed (66 before G5, plus the 2 `CaddyRootTrustTests`).
- `dotnet test tests/Decisya.ArchitectureTests --filter-not-trait "Category=Integration"`: 97 passed, 0 failed (90 before G5, so 7 new).
- `dotnet build tests/Decisya.Api.Tests -warnaserror`: 0 warnings, 0 errors.

### Story 1 – Pins, source, lock files, hashes, signer

| Scenario | Test or check |
| --- | --- |
| Pins, source mapping and lock files are in the repository | `.claude/tests/test_build_policy.py::BuildPolicyTests::test_ostium_pins_are_exactly_0_2_0`, `::test_nuget_config_maps_ostium_to_the_feed_only`, `::test_lock_files_and_locked_mode_switch`, `::test_restore_hardening_errors`; `Arch/OstiumSupplyChainTests.cs::OstiumSupplyChainTests::Every_project_under_src_and_tests_has_a_sibling_packages_lock_file` **(G5)** |
| Locked restore in CI and in the pre-push check | `deploy/tests/test_ci_workflow.py::test_a_locked_restore_runs_before_the_first_build_and_the_job_stays_unconditional`, `::test_every_dotnet_restore_is_locked_and_no_source_is_overridden`, `::test_locked_restore_is_declared_once_at_workflow_level_and_never_redefined`; `.claude/tests/test_prepush.py::test_dotnet_steps_restore_locked_first_and_all_run_locked`. **Manual:** the first CI run's `dotnet restore --locked-mode` with `GITHUB_TOKEN` (CI cannot be run from here). **Manual (S-8, steps below):** the tampered hash gives NU1403 and a nonzero exit |
| Lock-file hashes equal the supplier's values; 0.1.0 nowhere | `Arch/OstiumSupplyChainTests.cs::OstiumSupplyChainTests::Every_Ostium_lock_entry_resolves_0_2_0_with_the_signed_content_hash` **(G5)** (three constants from O-006/O-010, every lock file), `::Version_0_1_0_of_an_Ostium_package_is_referenced_nowhere` **(G5)**; `.claude/tests/test_build_policy.py::BuildPolicyTests::test_ostium_pins_are_exactly_0_2_0` |
| The signer line is pinned and Marco's check passes | `.claude/tests/test_build_policy.py::BuildPolicyTests::test_pinned_signer_line_is_byte_exact`. **Manual (Marco):** the supplier's check script ends in `RESULT: PASS`, pasted in the PR with the checked head commit (M-4). Never merge on `RESULT: FAIL` |

### Story 2 – Validation unchanged

| Scenario | Test |
| --- | --- |
| Every acceptance scenario of the original still holds | `Api/Authentication/TokenValidationTests.cs::TokenValidationTests::Invalid_tokens_are_rejected`, `::No_Authorization_header_at_all_is_rejected`, `::A_non_Bearer_scheme_is_rejected`, `::An_empty_or_malformed_bearer_value_is_rejected`, `::A_genuine_RS256_token_authenticates`, `::A_genuine_ES256_token_authenticates`, `::A_token_45_seconds_past_expiry_is_accepted_and_75_seconds_past_expiry_is_rejected`; `Api/Authentication/ChallengeResponseTests.cs::ChallengeResponseTests::All_rejections_are_identical`; `Api/Authentication/JwtBearerOptionsPinnedTests.cs::JwtBearerOptionsPinnedTests::Validation_parameters_are_pinned`, `::The_valid_issuer_is_the_authority_with_the_trailing_slash_trimmed`, `::Identity_model_PII_and_artifact_logging_are_off_at_run_time`; `Api/Authentication/ApiJwtOptionsTests.cs` (environment rows); the `PS256`, `RS512`, `alg=none`, HS256, embedded `jwk`, unknown `kid`, no `exp`, `typ` rows are in `InvalidTokenCaseCatalog.cs` (G4-retargeted, expectation unchanged) |
| 300 s maximum lifespan of NFR-15 | **Not tested, on purpose (G3 S-7).** The API does not check `exp - iat` (#20 B-2, still open). No test implies it; only the 60 s skew is proven (the 45 s and 75 s row above) |
| Full identity suite passes, no expectation weakened | **Manual / CI:** `tests/Decisya.Identity.Tests` Integration (G4 evidence: 147/147, locked) and `Api` Integration (G5 re-run: 68/68, including the Keycloak rows `ApiKeycloakAuthenticationTests`, `AdminKeycloakTests`, `AdminMfaProductionRealmTests`) |
| A roles string array reaches the API as a JSON array and maps as before | `Api/Authentication/RolesClaimShapeTests.cs::RolesClaimShapeTests::A_roles_string_array_is_a_JSON_array_of_two_strings_in_the_token_payload` **(G5)**, `::The_principal_is_a_platform_admin_and_the_admin_endpoint_succeeds_with_no_tenant` **(G5)**, `::With_a_tenant_id_next_to_the_admin_role_the_result_is_the_same_403_as_before` **(G5)**, `::A_single_role_array_without_the_admin_role_is_refused` **(G5)**. All pass: the shape is correct, so there is **no supplier gap**. (The "GET" in the scenario is a POST in practice: the admin endpoints are `POST|PUT|DELETE /api/admin/tenants/{id}/...`.) |

### Story 3 – Fail-closed start-up

| Scenario | Test |
| --- | --- |
| Shipped configuration carries the required values | `Api/Authentication/ApiJwtConfigurationTests.cs::ApiJwtConfigurationTests::The_shipped_configuration_carries_the_required_values` **(G5)**, `::The_test_audience_constant_equals_the_shipped_audience` **(G5)** |
| A bad audience stops start-up (not set, empty, three spaces, padded, `*`) | `::A_bad_audience_stops_start_up_and_the_message_names_the_key_only` **(G5)** (6 rows: null, empty, three spaces, leading pad, trailing pad, `*`). Direct-start pattern (`ApiStartHarness`, in-memory configuration, `UseTestServer`, `StartAsync`; no `WebApplicationFactory<Program>`, so not the flaky O-004 pattern). The message contains `Api:Jwt:Audience` and, where the value is not a bare `*` or blank, does not contain the value |
| A missing event setting stops start-up | `::A_missing_or_empty_event_setting_stops_start_up` **(G5)** (4 rows: Name and Id, each unset and empty), `::An_unusable_event_setting_stops_start_up` **(G5)** (3 rows: Id 0, Id -1, Name with a space); `::A_complete_configuration_starts_the_host` **(G5)** is the positive control so no row passes vacuously |

### Story 4 – Decisya's own parts

| Scenario | Test |
| --- | --- |
| Caller context and tenant exposure unchanged | `Api/Authentication/CallerIdentityTests.cs`, `CallerContextTests.cs`, `TenantClaimForbiddenTests.cs::TenantClaimForbiddenTests::A_malformed_tenant_id_claim_gives_403_before_routing_with_no_database_access`, `AdminAuthorizationTests.cs::AdminAuthorizationTests::A_forged_admin_header_query_or_cookie_never_grants_the_role`, `TenancyEndpointScenarioTests.cs`; `Api/Architecture/ApiBoundaryTests.cs::ApiBoundaryTests::No_Decisya_Api_type_implements_ICallerContextHook`, `::Only_CallerContextMiddleware_mints_a_tenant_resolution_in_Decisya_Api` |
| Admin MFA and event 2004 unchanged | `Api/Authentication/AdminMfaPolicyTests.cs::AdminMfaPolicyTests::An_admin_with_acr_2_is_accepted_and_one_without_it_is_refused_with_the_generic_403_and_one_warning`, `::The_refused_admin_warning_carries_the_hashed_user_id_through_the_enrichment_and_no_raw_sub`, `::AddDecisyaApiAuthentication_wires_the_fail_closed_validator_into_ValidateOnStart_and_defaults_to_true`; `AdminMfaCouplingTests.cs::AdminMfaCouplingTests::Every_api_admin_endpoint_of_the_real_host_carries_both_policies`; `AdminMfaProductionRealmTests.cs` (Integration) |

### Story 5 – Telemetry and logs

| Scenario | Test |
| --- | --- |
| Ostium sources and meters are registered; OTLP only | `SD/Telemetry/OstiumSubscriptionTests.cs::OstiumSubscriptionTests::An_Ostium_source_reaches_the_tracer_and_a_name_without_the_dot_does_not` (G4); `SD/Telemetry/TelemetryConfigurationTests.cs` (G4, `Ostium.*` wildcard) |
| Marker is masked; category changed once; fail closed | **Marker part: N/A for 0.1.1.** The restored `Ostium.Authentication.Jwt` and `Ostium.Backchannel` 0.1.1 declare no sensitive-data marker type (manifest "Package facts"), so there is nothing to recognise and no `OstiumMarkerMaskingTests` was written; the masker is unchanged. Story 5's marker scenario is therefore **not applicable to 0.1.1** and the supplier is to be asked for a marker in the exchange (G2 D3). A synthetic marker was not written: G3 M-3 says a synthetic type does not count. Category part: `Api/Authentication/TokenRejectionEventTests.cs::TokenRejectionEventTests::Each_invalid_token_row_logs_exactly_one_warning_with_its_closed_reason_and_stays_a_bare_401`, `::A_malformed_token_logs_the_malformed_reason`, `::A_token_of_another_type_logs_exactly_one_bad_type_event`; no-leak part: `LogCaptureTests.cs::LogCaptureTests::No_token_or_claim_reaches_any_log` (captures `Ostium` at Debug), `AuthLogCanaryTests.cs::AuthLogCanaryTests::No_canary_reaches_any_log_record_at_Debug_under_the_real_logging_configuration`. "No log query, alert or document filters on the old category": a repository search by G2 found none; the three living documents are updated by security-reviewer |

### Story 6 – Back-channel trust and boundaries

| Scenario | Test |
| --- | --- |
| `CaddyRootTrustTests` runs against `Ostium.Backchannel.BackchannelRoot` | `Api/Trust/CaddyRootTrustTests.cs::CaddyRootTrustTests::The_exported_caddy_root_is_trusted_and_a_second_caddys_root_is_not` **(G5, Integration, Docker)** and `::The_trust_type_under_test_is_the_Ostium_package_type` **(G5, Integration)**; both pass. The BFF copy `tests/Decisya.Bff.Tests/Trust/CaddyRootTrustTests.cs` is unchanged. Stack contract: `Api/Authentication/ApiTrustedRootWiringTests.cs::ApiTrustedRootWiringTests::The_Api_trust_goes_through_the_Ostium_Backchannel_package` **(G5)**, `::A_bad_trusted_root_file_stops_start_up_naming_the_key_and_never_the_path` **(G5, 7 bad-file kinds)**, `::With_no_trusted_root_configured_the_host_starts_on_system_trust` **(G5)**, `::A_good_root_makes_the_back_channel_trust_a_server_signed_by_it_and_no_other` **(G5)** |
| Architecture tests follow the move; BFF untouched | `Api/Architecture/ApiBoundaryTests.cs::ApiBoundaryTests::Decisya_Api_does_not_depend_on_JwtBearer_or_IdentityModel`, `::Only_Decisya_Api_Authentication_and_Program_depend_on_Ostium`, `::No_Decisya_Api_type_implements_ICallerContextHook`, `::Decisya_Api_csproj_has_exactly_the_ServiceDefaults_Tenancy_Entitlements_Audit_and_Admin_ProjectReferences_and_only_the_Ostium_Jwt_package` (G4); `Arch/OstiumBoundaryTests.cs::OstiumBoundaryTests::Modules_and_Contracts_assemblies_do_not_reference_an_Ostium_assembly` **(G5)**; `Arch/OstiumSupplyChainTests.cs::OstiumSupplyChainTests::The_BFF_and_the_identity_tests_have_no_Ostium_entry_in_project_or_lock_file` **(G5)**, `::Under_src_only_Decisya_Api_references_an_Ostium_package` **(G5)**, `::Only_test_projects_reference_the_Ostium_test_kit` **(G5)**. **Manual:** `git diff --stat -- src/Decisya.Bff` shows only `packages.lock.json` (G2's resolution of the Story 1 / Story 6 conflict). **Not done (open):** "each rule fails on a deliberate violation" (G2, NetArchTest rules) was not mutation-checked by G5 in this run; see Gaps |

### G3 MUSTs M-1 to M-5, S-7, S-8

| MUST | Covered by | Status |
| --- | --- | --- |
| M-1 locked restore that cannot be switched off silently | Static: `.claude/tests/test_build_policy.py::BuildPolicyTests::test_restore_hardening_errors` (DECISYA0007, 0008, 0009 text), `::test_lock_files_and_locked_mode_switch`; `deploy/tests/test_ci_workflow.py::test_locked_restore_is_declared_once_at_workflow_level_and_never_redefined`, `::test_every_dotnet_restore_is_locked_and_no_source_is_overridden`, `::test_no_job_that_may_restore_has_a_package_cache`; `.claude/tests/test_prepush.py::test_dotnet_steps_restore_locked_first_and_all_run_locked`. Behavioural: G4 probe table (global-property form of DECISYA0009, `msbuild -t:DecisyaCentralPackagePolicy`) | Covered statically. **Manual evidence pending (main session):** NU1403 on a tampered hash and the csproj-body form of DECISYA0009 (steps below) |
| M-2 host-level proof of the #20 mitigations | `JwtBearerOptionsPinnedTests` (all three), `ApiJwtOptionsTests` (Production and Development rows), `AuthenticationSchemeSetTests` **(G5)**: `::Exactly_one_scheme_Bearer_is_registered`, `::Bearer_is_the_default_authenticate_and_challenge_scheme`, `::The_default_policy_is_untouched_and_the_fallback_requires_an_authenticated_caller_with_a_sub_claim_on_Bearer_only`, `::No_other_scheme_is_registered_by_name` (3 rows); `InvalidTokenCaseCatalog` rows; `FallbackPolicyTests`, `ChallengeResponseTests` | Covered. G6 still checks by reading that `AddDecisyaApiAuthentication` and `Program.cs` call no `AddAuthentication`, `AddScheme`, `AddPolicyScheme` or `AddCookie` (I verified `DecisyaApiAuthenticationExtensions.cs` by reading it: it adds only an additive `AddPolicy` and no scheme) |
| M-3 no token, claim or marked value in a log | `LogCaptureTests::No_token_or_claim_reaches_any_log` (`Ostium` category captured), `TokenRejectionEventTests` (2003, no claim value), `AuthLogCanaryTests`, `JwtBearerOptionsPinnedTests::Identity_model_PII_and_artifact_logging_are_off_at_run_time` | Covered for tokens and claims. **`OstiumMarkerMaskingTests` N/A: no marker exists in 0.1.1** (above) |
| M-4 merge gate binds the signed values to the merging commit | `Arch/OstiumSupplyChainTests::Every_Ostium_lock_entry_resolves_0_2_0_with_the_signed_content_hash` (constants), `test_build_policy.py::test_pinned_signer_line_is_byte_exact` | Static half covered. **Manual (Marco):** `RESULT: PASS` block naming the head commit, in the PR; G6 and G7 check it |
| M-5 job token reaches only what restores | `deploy/tests/test_ci_workflow.py::test_the_job_asks_for_read_only_contents_and_packages_and_nothing_else`, `::test_the_credential_is_only_a_step_env_of_a_run_step_that_restores`, `::test_the_credential_is_never_written_for_later_steps`, `::test_packages_read_is_granted_only_to_the_jobs_that_restore`; `.claude/tests/test_ci_fullstack.py::test_each_job_always_reports_with_read_only_permissions` | Covered statically (devops tests, not re-written by G5). The runtime behaviour is proven only by the first CI run (manual) |
| S-7 no max-lifetime test | Nothing written, see Story 2 | Honoured |
| S-8 tampered hash, unlocked restore | Manual steps below | **Pending (main session)** |

### Manual checks for the main session (S-8 and M-1)

G5 cannot run these (the tool rules allow only `dotnet build` and `dotnet test`). Use a scratch copy made outside the
repository, never `git stash`, never the real lock files. Run each in the scratch copy only.

1. Copy the working tree to the scratchpad (the lock files are not committed yet, so `git archive HEAD` would lack them):
   `robocopy <repo> <scratchpad>\copy /E /XD .git bin obj node_modules`.
2. **NU1403.** In `<scratchpad>\copy\src\Decisya.Api\packages.lock.json`, change one character of the `Ostium.Authentication.Jwt` `contentHash`.
   Run `DECISYA_LOCKED_RESTORE=true dotnet restore src/Decisya.Api --locked-mode -v:n` in the copy.
   Expect: nonzero exit, error NU1403, and no package fetched from any source other than the two in `NuGet.config`. Record the exit code and the NU1403 line.
3. **S-8 (unlocked).** Same tampered file, `DECISYA_LOCKED_RESTORE` unset: `dotnet restore src/Decisya.Api`. Record whether it fails
   or rewrites the lock file silently (diff the file before and after). If it rewrites silently, the pre-push locked step and CI are the only controls on that path; record it.
4. **DECISYA0009, csproj body form.** In the copy, add `<RestoreLockedMode>false</RestoreLockedMode>` to the first `PropertyGroup` of
   `src/Decisya.SharedKernel/Decisya.SharedKernel.csproj`. Run `DECISYA_LOCKED_RESTORE=true dotnet msbuild src/Decisya.SharedKernel -t:DecisyaCentralPackagePolicy`.
   Expect: error DECISYA0009. Then remove the line and run the same command: expect success. (G4 probed the global-property form; this is the form G3 M-1 names.)
5. **Mutation check of the new rules** (optional, same copy): add a `using`/reference to `Microsoft.IdentityModel.Tokens` in a `Decisya.Api` type and run
   `dotnet test tests/Decisya.Api.Tests --filter-not-trait "Category=Integration"`: `Decisya_Api_does_not_depend_on_JwtBearer_or_IdentityModel` must fail; add an `Ostium` reference to a non-`Authentication` type: `Only_Decisya_Api_Authentication_and_Program_depend_on_Ostium` must fail.

### G6 fix-now findings (Low)

| Finding | Test |
| --- | --- |
| S-164-01 (ASVS V12.3.2): a certificate from the pinned root for another host name is refused | `Api/Authentication/ApiTrustedRootWiringTests.cs::ApiTrustedRootWiringTests::A_server_certificate_from_the_pinned_root_for_another_host_name_is_rejected` **(G5)**: expects `HttpRequestException`; passes, so host name validation holds in `Ostium.Backchannel` 0.1.1 |
| S-164-02 (ASVS V14.2.2): the 401 carries `Cache-Control: no-store` | `Api/Authentication/ChallengeResponseTests.cs::ChallengeResponseTests::All_rejections_are_identical`, helper `AssertBareChallengeAsync` now asserts `CacheControl.NoStore` for every rejection row; passes |

After both: Api.Tests unit lane 374 passed, 0 failed (Integration lane unchanged at 68, not re-run).

### Observations and gaps

- **Fallback policy names the scheme.** Ostium 0.1.1's fallback policy carries `AuthenticationSchemes = ["Bearer"]`, whereas the old Decisya policy had none (G5 first run failed on that assertion). With one scheme the outcome is identical, so `AuthenticationSchemeSetTests` accepts exactly `Bearer` and nothing else. Not a defect; recorded so G6 knows the policy shape.
- **No `OstiumMarkerMaskingTests`:** 0.1.1 has no marker (above). Story 5's marker scenario is not applicable at this version.
- **Mutation checks not run by G5:** the G2 requirement that each new or retargeted architecture rule fails on a deliberate violation needs edits to `src/`, outside the test-engineer lane. Step 5 above is the recipe; the main session or G6 must run it, or Marco accepts the gap.
- **First CI run** is the only proof of the locked restore with `GITHUB_TOKEN`, of `packages: read`, and of Dependabot's lock-file handling.
- **Unit-trait note:** older Api.Tests classes carry no `Category` trait. The new classes carry `Unit`, `Architecture` or `Integration` as the working agreement says; the older classes were left alone.
- No flaky test was seen in the three runs.

<!-- gate: G5 | verdict: PASS | issue: #164 -->
