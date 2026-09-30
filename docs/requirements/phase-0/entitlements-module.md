# Phase 0 – Modules.Entitlements: plans, features, overrides and trials

## Issue 0.11 (#23) — Modules.Entitlements (plans, features, overrides, trials)

Done when (issue): a Free plan denies a Phase 3 feature; an admin override grants it.

`gh issue view 23` and `docs/PHASE-0.md` were not readable in this session (no shell; `docs/PHASE-0.md` has no entitlement text). The scope below comes from the title, the Done-when in the manifest, ADR-0008 and Marco's decisions. Anything the issue body might say beyond that is an open question, not an assumption.

### Scope note (role framing)

The stories are written "As a tenant user" (the tenant whose access is being decided), "As a platform admin" (the operator who grants overrides), and "As the platform operator" (fail-closed properties no single caller experiences differently). There is no admin endpoint in #23. The admin stories are tested by calling the application command directly.

**Entitlement plan:** the Entitlements module is the platform's gating primitive, so none of its own commands or queries is gated by a feature key. The keys it defines (Story 1) are the first `module.feature` keys in the platform. `entitlements.*` operations are admin-only and unreachable over HTTP until #25.

### Scope, tight (#84)

In scope:
- A static catalog of plans and feature keys, in code.
- `IEntitlementService` in `Modules.Entitlements.Contracts` (ADR-0008, ADR-0005), answering "is feature X enabled for the current tenant".
- Two tenant-scoped aggregates in a new `entitlements` schema: `TrialGrant` and `FeatureOverride`.
- Three application commands (start trial, grant override, revoke override) plus the evaluation query.
- Two-tenant isolation test on the new aggregates.

Deferred, owned elsewhere:
- Billing, payment, plan changes (upgrade/downgrade), plan assignment by purchase: later phase. In #23 a tenant is on Free, or on a trial of Pro, nothing else.
- Admin endpoint for overrides and trials: #25.
- Audit log of override grant/revoke: #24 follow-up (Marco). See Risk R-1.
- `GET /bff/me` capability manifest fields and the SPA: #26.
- The ADR-0008 endpoint-metadata test ("every `/api` endpoint carries an entitlement policy or an explicit no-entitlement marker") and the authorization policy that calls `IEntitlementService`: first gated endpoint / #26. #23 ships no gated endpoint.
- Auto-starting a trial at sign-in (would couple Entitlements to Tenancy JIT provisioning, #21).
- Per-seat limits, usage quotas, metered features, deny-overrides, plan-price data.

### Design notes (adopted as recommendations; see Open questions)

- **Plans and features are code, not rows.** A static `PlanCatalog` maps `PlanId` (`Free`, `Pro`) to a set of feature keys. A plan or feature change is a reviewed PR, needs no seed/migration sync, and cannot be edited by a tenant. Rows exist only for per-tenant facts (trial, override).
- **Phase 3 feature** means a catalog feature key for a capability scheduled for Phase 3, registered now so the gate can be tested before the capability exists. Recommended key: `forecasting.scenarios` (Pro only). One Free key is also registered so "Free allows something" is testable: `ledger.transactions`. Final key names need Marco's confirmation (Q1).
- **Default plan is `Free`, implicit.** A tenant with no trial row is on Free. No row is written when a tenant is provisioned, so Entitlements has no dependency on Tenancy.
- **Trial** is a `TrialGrant`: tenant, plan `Pro`, `StartsAt` and `EndsAt` as NodaTime `Instant`, `EndsAt = StartsAt + Duration.FromDays(14)`. Active while `StartsAt <= now < EndsAt` (end exclusive), read from `IClock`. There is no expiry job: expiry is computed at evaluation, so the tenant reverts to Free at `EndsAt` exactly. One trial per tenant, ever (a second start is refused, including after expiry).
- **Override** is a `FeatureOverride`: tenant, feature key, `GrantedAt`, optional `ExpiresAt` (`Instant`), required reason text. Grant-only. Precedence for one feature: (1) unknown key, invalid or no tenant → denied; (2) an unexpired override → granted; (3) else an active trial's plan includes it → granted; (4) else the Free plan includes it → granted; (5) else denied. An override expires exactly like a trial (`now < ExpiresAt`; no `ExpiresAt` means until revoked). One override per tenant and feature: granting again replaces the expiry and reason.
- **Admin commands cross tenants by design.** A platform admin has no `tenant_id` (#20, #21). The commands take an explicit target `TenantId` and are the `[AllowCrossTenant]` handlers that platform invariant 1 describes. Who may call them (authorization) is enforced at the admin endpoint in #25. The command itself trusts its caller, and is unreachable over HTTP in #23.
- **Evaluation is for the current tenant only.** `IEntitlementService.IsEnabledAsync(featureKey)` reads `ICurrentTenant`. It never takes a tenant id argument, so a caller cannot ask about another tenant.
- **No AI dependency** (invariant 2): evaluation is deterministic.

---

## Open questions for Marco (all six answered by Marco, 2026-09-30: recommendations accepted as written)

Status: Q1 answered (Marco, 2026-09-30). Q2 answered (Marco, 2026-09-30). Q3 answered (Marco, 2026-09-30). Q4 answered (Marco, 2026-09-30). Q5 answered (Marco, 2026-09-30). Q6 answered (Marco, 2026-09-30): the audit deferral is accepted, and #24 must add auditing before #25 exposes the commands.

1. **Where do plans and features live, and what is the Phase 3 feature key?** Recommended: plans and features in code (`PlanCatalog`); overrides and trials in Postgres. Phase 3 key `forecasting.scenarios`, Pro-only, plus Free key `ledger.transactions`. Rejected: plan/feature tables, which add seeding and an admin editor for no phase-0 scenario. Please confirm the key name, or name the actual Phase 3 capability (I could not read the issue or a phase plan that names it).
2. **Default plan for a newly provisioned tenant?** Recommended: `Free`, implicit, no row. Rejected: a row written at provisioning (couples to #21, needs a backfill for existing tenants).
3. **Trial semantics.** Recommended: Pro, 14 days, end-exclusive, computed at read from `IClock`, reverts to Free afterwards, one per tenant ever, started by an admin command (not automatically at sign-in). Is 14 days and "one per tenant ever" right? Is auto-start at sign-in wanted (it would be a follow-up)?
4. **Override precedence and expiry.** Recommended: grant-only, beats plan and trial, optional expiry, revocable, one per tenant and feature. Rejected: deny-overrides (no scenario needs one; precedence would need a second rule).
5. **Is `GET /api/entitlements/me` in scope?** Recommended: no. #26 owns the `/bff/me` manifest and ADR-0008 wants one shared `IEntitlementService`, not a second, parallel surface. #23 is verified through the service and commands. The cost: no HTTP-level proof of the Done-when until #25/#26 (accepted).
6. **Override audit gap (Risk R-1).** Invariant 1 says cross-tenant handlers are audited, and Marco deferred auditing to a #24 follow-up. Recommended: accept for #23 because the commands have no HTTP path; G3 should record the gap, and the #24 follow-up must exist before #25 exposes the commands. Please confirm that ordering.

All six are answered, so no open question remains. The verdict is PASS-WITH-NOTES: the note is Risk R-1 (unaudited cross-tenant admin commands), accepted by Marco, with #24 required to add auditing before #25 exposes the commands.

---

### Story 1 — A Free tenant is denied a Phase 3 feature and allowed a Free feature

As a tenant user on the Free plan, I want the platform to deny me features outside my plan and allow those inside it, so that what I can use matches what I subscribe to.

**Gating feature keys:** none (Entitlements is the gating primitive). Catalog keys: `ledger.transactions` (Free, Pro), `forecasting.scenarios` (Pro).

```gherkin
Feature: The Free plan denies a Phase 3 feature (the Done-when)

  Background:
    Given the plan catalog lists "ledger.transactions" for plans Free and Pro
    And the plan catalog lists "forecasting.scenarios" for plan Pro only

  Scenario: A tenant with no trial and no override is on Free and is denied the Phase 3 feature
    Given the current tenant is "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    And that tenant has no TrialGrant and no FeatureOverride
    When IsEnabledAsync is called for "forecasting.scenarios"
    Then the result is denied

  Scenario: A Free tenant is allowed a feature the Free plan includes
    Given the current tenant is "7c9e6679-7425-40de-944b-e07fc1f90ae7" with no TrialGrant and no FeatureOverride
    When IsEnabledAsync is called for "ledger.transactions"
    Then the result is allowed

  Scenario: An unknown feature key is denied
    Given the current tenant is "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    When IsEnabledAsync is called for "nosuch.feature"
    Then the result is denied
    And no exception is thrown

  Scenario: A resolution of no tenant or an invalid tenant is denied every feature
    Given the current tenant resolution is "no tenant" (platform admin)
    When IsEnabledAsync is called for "ledger.transactions"
    Then the result is denied
    And the same holds when the resolution is "invalid"
```

### Story 2 — A platform admin overrides a tenant's plan to grant a feature

As a platform admin, I want to grant a specific tenant a specific feature outside its plan, optionally until a date, and to revoke it, so that I can support a customer without changing their plan.

**Gating feature key:** none (admin command; HTTP exposure in #25).

```gherkin
Feature: An admin override grants a feature the plan denies (the Done-when)

  Background:
    Given a fake IClock fixed at "2026-10-01T09:00:00Z"
    And tenant A is "7c9e6679-7425-40de-944b-e07fc1f90ae7" on Free with no trial

  Scenario: An admin override grants the Phase 3 feature
    Given IsEnabledAsync for "forecasting.scenarios" is denied for tenant A
    When the GrantOverride command is executed for tenant A, feature "forecasting.scenarios", reason "Design-partner pilot", no expiry
    Then a FeatureOverride row exists for tenant A and "forecasting.scenarios"
    And IsEnabledAsync for "forecasting.scenarios" is allowed for tenant A

  Scenario: An override only affects the tenant and feature it names
    Given an override grants "forecasting.scenarios" to tenant A
    When IsEnabledAsync is evaluated for tenant B "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b" on Free, and for tenant A for a different Pro-only feature
    Then tenant B is denied "forecasting.scenarios"
    And tenant A is denied the other Pro-only feature

  Scenario: An override with an expiry is honoured until the expiry instant and not after
    Given an override grants "forecasting.scenarios" to tenant A with ExpiresAt "2026-10-15T09:00:00Z"
    When the clock reads "2026-10-15T08:59:59Z"
    Then IsEnabledAsync is allowed
    When the clock reads "2026-10-15T09:00:00Z"
    Then IsEnabledAsync is denied

  Scenario: Granting again replaces the existing override, never duplicating it
    Given an override exists for tenant A and "forecasting.scenarios" with reason "Design-partner pilot"
    When GrantOverride is executed again for the same tenant and feature with reason "Pilot extended"
    Then exactly one FeatureOverride row exists for tenant A and "forecasting.scenarios"
    And its reason is "Pilot extended"

  Scenario: Revoking an override restores the plan's answer
    Given an override grants "forecasting.scenarios" to tenant A
    When the RevokeOverride command is executed for tenant A and "forecasting.scenarios"
    Then IsEnabledAsync for "forecasting.scenarios" is denied for tenant A
    And revoking a feature that has no override succeeds without error and changes nothing

  Scenario: An override for an unknown feature key is rejected
    When GrantOverride is executed for tenant A and feature "nosuch.feature"
    Then the command fails with a validation error
    And no FeatureOverride row is created

  Scenario: An override needs a reason and an expiry in the future
    When GrantOverride is executed for tenant A with an empty reason
    Then the command fails with a validation error
    When GrantOverride is executed with ExpiresAt "2026-09-30T09:00:00Z", earlier than the clock
    Then the command fails with a validation error
    And no FeatureOverride row is created in either case
```

### Story 3 — A tenant can start one time-limited Pro trial

As a tenant user, I want a 14-day Pro trial, so that I can try Pro features before deciding whether to subscribe.

**Gating feature key:** none (admin command in #23; self-service start is a follow-up).

```gherkin
Feature: A trial grants Pro for 14 days, then reverts to Free

  Background:
    Given a fake IClock
    And tenant A is "7c9e6679-7425-40de-944b-e07fc1f90ae7" on Free

  Scenario: Starting a trial grants Pro features for exactly 14 days
    Given the clock reads "2026-10-01T09:00:00Z"
    When the StartTrial command is executed for tenant A
    Then a TrialGrant exists for tenant A with plan Pro, StartsAt "2026-10-01T09:00:00Z" and EndsAt "2026-10-15T09:00:00Z"
    And IsEnabledAsync for "forecasting.scenarios" is allowed at "2026-10-15T08:59:59Z"

  Scenario: The trial expires at EndsAt and the tenant reverts to Free
    Given tenant A's trial ended at "2026-10-15T09:00:00Z"
    When the clock reads "2026-10-15T09:00:00Z"
    Then IsEnabledAsync for "forecasting.scenarios" is denied
    And IsEnabledAsync for "ledger.transactions" is allowed

  Scenario: A tenant cannot start a second trial, even after the first has expired
    Given tenant A started a trial that has since expired
    When the StartTrial command is executed for tenant A again
    Then the command is refused with a "trial already used" error
    And exactly one TrialGrant row exists for tenant A

  Scenario: A trial does not affect another tenant
    Given tenant A has an active trial
    When IsEnabledAsync for "forecasting.scenarios" is evaluated for tenant B "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b" on Free
    Then tenant B is denied

  Scenario: Concurrent trial starts for one tenant create exactly one TrialGrant
    When 16 StartTrial commands run in parallel for tenant A
    Then exactly one succeeds
    And exactly one TrialGrant row exists for tenant A
```

### Story 4 — Entitlement rows never cross tenants

As a tenant user, I want my trial and override rows unreachable from another tenant, so that nobody can read or change my entitlements through theirs.

```gherkin
Feature: A two-tenant isolation test proves read and write isolation for TrialGrant and FeatureOverride

  Background:
    Given PostgresFixture provides a real Postgres 18 instance
    And a TrialGrant and a FeatureOverride exist for tenant A ("7c9e6679-7425-40de-944b-e07fc1f90ae7")
    And a different TrialGrant and FeatureOverride exist for tenant B ("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b")

  Scenario: Tenant A cannot read tenant B's rows
    Given the context's ambient tenant is tenant A
    When tenant B's TrialGrant and FeatureOverride rows are queried, by a collection query and by their own id
    Then none of tenant B's rows are returned

  Scenario: Tenant A cannot update or delete tenant B's rows by id
    Given the context's ambient tenant is tenant A
    When an update or a delete is attempted against tenant B's row ids
    Then zero rows are affected
    And tenant B's rows are unchanged in the database afterward

  Scenario: The module persists only through ITenantScoped aggregates
    Given the Entitlements module's DbContext model
    When the mapped entity types are inspected
    Then every entity type implements ITenantScoped
```

### Story 5 — The module meets the platform structure rules

As the platform operator, I want the Entitlements module to follow the standard module shape, so that architecture tests and later consumers (#25, #26) can rely on it.

```gherkin
Feature: Module shape and money/time conventions

  Scenario: IEntitlementService is declared in the Contracts assembly
    When the public types of Decisya.Modules.Entitlements.Contracts are inspected
    Then IEntitlementService is declared there
    And Decisya.Modules.Entitlements.Contracts references no other module's internals

  Scenario: The module uses the schema-per-module and least-privilege conventions
    When the migration for the Entitlements module is applied
    Then its tables are in the "entitlements" schema
    And the module's database role has DML rights only, with no DROP or ALTER

  Scenario: All instants are NodaTime
    When the aggregates and commands are inspected
    Then every time-valued member is an Instant, with none of DateTime or DateTimeOffset
```

---

## Non-functional requirements

Two rows are added to `docs/requirements/nfr.md` (ISO/IEC 25010 categories): NFR-34, NFR-35.

| Id | Category | Target (summary) |
| --- | --- | --- |
| NFR-34 | Security | Zero cross-tenant `FeatureOverride` or `TrialGrant` rows read, updated or deleted by id across the two-tenant matrix; an evaluation never reflects another tenant's override or trial |
| NFR-35 | Reliability | Evaluation fails closed in 100% of unknown-key, no-tenant, invalid-tenant, expired-trial and expired-override cases, with zero exceptions escaping |

## Risks carried to G2 and G3

- **R-1 Unaudited cross-tenant admin commands.** `GrantOverride`, `RevokeOverride` and `StartTrial` act on a tenant other than the caller's. Platform invariant 1 requires an audited `[AllowCrossTenant]` handler. Audit is deferred to a #24 follow-up (Marco). Until then the commands must not be reachable over HTTP (#23 adds no endpoint). G2 and G3 decide how the attribute and the command's caller check are expressed.
- **R-2 Stale answers.** ADR-0008 accepts staleness in the manifest. #23 adds no cache, so every evaluation reads the database. A cache is a #26 decision.
- **R-3 Free-text reason.** The override `reason` may carry customer details. It is `[Sensitive]` for logging (CLAUDE.md observability).

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #23 -->

## Traceability

Abbreviations. **E** = `tests/Modules/Decisya.Modules.Entitlements.Tests`. **M** = `tests/Decisya.Infrastructure.Migrator.Tests`. **A** = `tests/Decisya.ArchitectureTests`. **AP** = `tests/Decisya.Api.Tests`. **SD** = `tests/Decisya.ServiceDefaults.Tests`. A test is `Class.Method` in the named project. Lanes: Unit, Architecture, Integration (Testcontainers Postgres 18, `FakeClock`).

**Where the Done-when is proven.** "Free plan denies Phase 3 feature; admin override grants it" is proven at service and command level: `EntitlementsOverrideTests.A_tenant_with_no_trial_and_no_override_is_on_Free_and_is_denied_the_Phase_3_feature` (Free denies `forecasting.scenarios`) and `EntitlementsOverrideTests.An_admin_override_grants_the_Phase_3_feature` (the GrantOverride command makes `IEntitlementService.IsEnabledAsync` true) in E, both Integration, against a real Postgres 18 database. There is no HTTP-level proof: #23 has no endpoint (G1 Q5). The HTTP proof belongs to #25 (admin endpoint) and #26 (`/bff/me` manifest).

### Acceptance criteria

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A tenant with no trial and no override is on Free and is denied the Phase 3 feature | E `EntitlementsOverrideTests.A_tenant_with_no_trial_and_no_override_is_on_Free_and_is_denied_the_Phase_3_feature` | Integration |
| 1 | A Free tenant is allowed a feature the Free plan includes | E `EntitlementsOverrideTests.A_Free_tenant_is_allowed_a_feature_the_Free_plan_includes`; E `EntitlementsFailClosedTests.A_Free_key_is_granted_to_a_tenant_without_a_database_query` | Integration; Unit |
| 1 | An unknown feature key is denied | E `EntitlementsOverrideTests.An_unknown_feature_key_is_denied`; E `EntitlementsFailClosedTests.An_unknown_key_and_default_FeatureKey_are_denied_for_a_tenant_without_touching_the_database` | Integration; Unit |
| 1 | A resolution of no tenant or an invalid tenant is denied every feature | E `EntitlementsFailClosedTests.Under_a_tenant_less_ambient_every_key_is_denied_with_no_exception_and_no_database_access`; E `EntitlementsFailClosedTests.Under_an_invalid_ambient_every_key_is_denied_with_no_exception_and_no_database_access` | Unit |
| 2 | An admin override grants the Phase 3 feature | E `EntitlementsOverrideTests.An_admin_override_grants_the_Phase_3_feature` | Integration |
| 2 | An override only affects the tenant and feature it names | E `EntitlementsOverrideTests.An_override_only_affects_the_tenant_and_feature_it_names` (uses the internal `PlanCatalog` seam for the second Pro-only key); E `EntitlementsIsolationTests.An_evaluation_never_reflects_another_tenants_override_or_trial` | Integration |
| 2 | An override with an expiry is honoured until the expiry instant and not after | E `EntitlementsOverrideTests.An_override_with_an_expiry_is_honoured_until_the_expiry_instant_and_not_after`; E `EntitlementsTrialTests.An_override_is_active_one_tick_before_ExpiresAt_and_denied_at_ExpiresAt`; E `EntitlementsDomainTests.An_override_without_an_expiry_never_expires_and_one_with_an_expiry_is_end_exclusive` | Integration; Unit |
| 2 | Granting again replaces the existing override, never duplicating it | E `EntitlementsOverrideTests.Granting_again_replaces_the_existing_override_never_duplicating_it`; E `GrantOverrideRaceRecoveryTests.When_the_23505_re_read_finds_the_winner_the_handler_replaces_it_and_leaves_exactly_one_row` | Integration |
| 2 | Revoking an override restores the plan's answer | E `EntitlementsOverrideTests.Revoking_an_override_restores_the_plans_answer`; E `EntitlementsOverrideTests.Revoking_a_feature_that_has_no_override_succeeds_without_error_and_changes_nothing` (the "no override" clause); E `EntitlementsOverrideTests.Revoking_an_override_for_a_key_later_removed_from_the_catalog_still_works` | Integration |
| 2 | An override for an unknown feature key is rejected | E `EntitlementsOverrideTests.An_override_for_an_unknown_feature_key_is_rejected`; E `EntitlementsForbiddenTests.Validation_fails_before_any_database_access` | Integration; Unit |
| 2 | An override needs a reason and an expiry in the future | E `EntitlementsOverrideTests.An_override_needs_a_reason_and_an_expiry_in_the_future`; E `EntitlementsOverrideTests.A_reason_of_exactly_500_characters_is_accepted_and_is_stored_trimmed` | Integration |
| 3 | Starting a trial grants Pro features for exactly 14 days | E `EntitlementsTrialTests.Starting_a_trial_grants_Pro_features_for_exactly_14_days`; E `EntitlementsDomainTests.A_trial_lasts_exactly_14_days_of_elapsed_time_and_is_Pro` | Integration; Unit |
| 3 | The trial expires at EndsAt and the tenant reverts to Free | E `EntitlementsTrialTests.The_trial_expires_at_EndsAt_and_the_tenant_reverts_to_Free`; E `EntitlementsTrialTests.A_trial_is_active_one_tick_before_EndsAt_and_denied_at_EndsAt` | Integration |
| 3 | A tenant cannot start a second trial, even after the first has expired | E `EntitlementsTrialTests.A_tenant_cannot_start_a_second_trial_even_after_the_first_has_expired` | Integration |
| 3 | A trial does not affect another tenant | E `EntitlementsTrialTests.A_trial_does_not_affect_another_tenant` | Integration |
| 3 | Concurrent trial starts for one tenant create exactly one TrialGrant | E `EntitlementsTrialTests.Concurrent_trial_starts_for_one_tenant_create_exactly_one_TrialGrant` (16 parallel calls, one success, 15 `trial_already_used`, one row; run 3 of 3 by the orchestrator) | Integration |
| 4 | Tenant A cannot read tenant B's rows | E `EntitlementsIsolationTests.Tenant_A_cannot_read_tenant_Bs_rows` (collection and by id, both aggregates) | Integration |
| 4 | Tenant A cannot update or delete tenant B's rows by id | E `EntitlementsIsolationTests.Tenant_A_cannot_update_or_delete_tenant_Bs_rows_by_id`; E `EntitlementsIsolationTests.Tenant_A_cannot_save_a_row_that_belongs_to_tenant_B` | Integration |
| 4 | The module persists only through ITenantScoped aggregates | E `EntitlementsIsolationTests.The_module_persists_only_through_ITenantScoped_aggregates`; E `EntitlementsModuleBoundaryTests.Every_EntitlementsDbContext_entity_type_maps_to_schema_entitlements_and_is_ITenantScoped`; A `ModuleBoundaryTests.TenantModelRule_passes_over_the_current_architecture_scope` | Integration; Architecture |
| 5 | IEntitlementService is declared in the Contracts assembly | E `EntitlementsModuleBoundaryTests.IEntitlementService_is_declared_in_the_Contracts_assembly_and_its_only_operation_takes_a_feature_key`; A `ContractsBoundaryTests.Every_Contracts_assembly_depends_on_only_SharedKernel_and_other_Contracts_assemblies_among_Decisya_assemblies` (Contracts scope includes the new assembly) | Architecture |
| 5 | The module uses the schema-per-module and least-privilege conventions | M `EntitlementsRoleIntegrationTests.The_entitlements_migration_creates_both_tables_with_their_constraints_in_the_entitlements_schema_only`; M `EntitlementsRoleIntegrationTests.Decisya_entitlements_role_has_no_DDL_privilege_and_cannot_read_the_migrations_history_table`; M `EntitlementsRoleIntegrationTests.Decisya_entitlements_role_can_read_write_update_and_delete_both_of_its_tables`; E `EntitlementsModuleBoundaryTests.The_unique_indexes_and_check_constraints_the_design_names_exist_in_the_model` and `.The_migration_snapshot_matches_the_model_with_no_pending_changes` | Integration; Architecture |
| 5 | All instants are NodaTime | E `EntitlementsModuleBoundaryTests.No_Domain_or_Application_type_has_a_DateTime_or_DateTimeOffset_property_field_or_parameter`; E `EntitlementsModuleBoundaryTests.Every_time_valued_member_of_the_aggregates_and_commands_is_an_Instant`; E `EntitlementsModuleBoundaryTests.The_Entitlements_module_never_reads_the_system_clock_directly` | Architecture |
| NFR-34 | Zero cross-tenant rows read, updated or deleted by id; an evaluation never reflects another tenant's override or trial | E `EntitlementsIsolationTests.Tenant_A_cannot_read_tenant_Bs_rows`, `.Tenant_A_cannot_update_or_delete_tenant_Bs_rows_by_id`, `.An_evaluation_never_reflects_another_tenants_override_or_trial`, `.A_command_for_tenant_A_changes_no_row_of_tenant_B`, `.A_revoke_for_tenant_A_does_not_delete_tenant_Bs_override_for_the_same_feature` | Integration |
| NFR-35 | Evaluation fails closed in every unknown-key, no-tenant, invalid-tenant, expired-trial and expired-override case, with no exception escaping | E `EntitlementsFailClosedTests.*` (none, invalid, unknown, default); E `EntitlementsTrialTests.An_expired_trial_with_no_override_denies_the_Pro_key_and_allows_the_Free_key`, `.A_trial_is_active_one_tick_before_EndsAt_and_denied_at_EndsAt`, `.An_override_is_active_one_tick_before_ExpiresAt_and_denied_at_ExpiresAt` | Unit; Integration |

No criterion is `manual`. The deferred items (auditing, the admin endpoint, the `/bff/me` manifest, the ADR-0008 endpoint-metadata test) are outside #23 (G1 scope) and have no scenario here.

### G3 MUSTs and SHOULDs

| G3 item | Test(s) | Lane |
| --- | --- | --- |
| G4-23-01 (`Tenant` and `Invalid` ambient get `Forbidden`, precondition before validation, no row written) | E `EntitlementsForbiddenTests.A_non_admin_ambient_gets_forbidden_from_every_command_before_validation_and_with_no_database_access` (Theory over `Tenant` and `Invalid`; placeholder host), `.A_non_admin_ambient_writes_no_row_for_any_command_against_any_tenant`, `.A_non_admin_ambient_cannot_revoke_an_existing_override_or_replace_it` | Unit; Integration |
| G4-23-01 (`None` with `default(TenantId)` gives `tenant_invalid`, no exception) | E `EntitlementsForbiddenTests.A_tenant_less_admin_ambient_with_a_default_target_tenant_gets_tenant_invalid_and_no_exception`; E `EntitlementsOverrideTests.A_default_target_tenant_is_rejected_as_tenant_invalid_without_an_exception` | Unit; Integration |
| G4-23-01 (a command for A changes no row of B, and two-tenant isolation) | E `EntitlementsIsolationTests.A_command_for_tenant_A_changes_no_row_of_tenant_B`, `.A_revoke_for_tenant_A_does_not_delete_tenant_Bs_override_for_the_same_feature`, plus the Story 4 tests above | Integration |
| G4-23-01 (only attributed code can mint the scope: `For`, `FromClaim`, unattributed and attributed fixtures) | A `TenancyRuleTests.CrossTenantQueryRule_fails_on_an_unattributed_tenant_scope_mint` (Theory: `UnattributedTenantScope`, `UnattributedClaimScope`, `UnattributedNoTenantMint`); A `TenancyRuleTests.CrossTenantQueryRule_passes_on_an_attributed_tenant_scope_mint`; A `ModuleBoundaryTests.CrossTenantQueryRule_passes_over_the_current_architecture_scope` (proves the three handlers are attributed); E `EntitlementsModuleBoundaryTests.The_handlers_take_the_ambient_tenant_and_the_options_and_never_the_DI_context` | Architecture |
| G4-23-02 (nothing outside the module and its tests can reach the commands) | E `EntitlementsModuleBoundaryTests.InternalsVisibleTo_is_exactly_the_module_test_assembly`, `.The_Entitlements_module_references_no_ASP_NET_Core_and_no_Wolverine_assembly`, `.The_Entitlements_project_file_declares_no_FrameworkReference_no_AspNetCore_and_no_Wolverine_package`, `.The_Entitlements_module_declares_no_Endpoints_namespace`, `.The_AllowCrossTenant_types_are_exactly_the_three_admin_handlers_and_none_is_public`, `.Each_AllowCrossTenant_justification_is_not_blank_and_names_the_24_audit_prerequisite`, `.The_commands_handlers_and_TargetTenant_are_internal_and_not_in_Contracts`, `.No_public_type_in_the_module_or_its_Contracts_has_a_HandleAsync_method_or_a_constructor_parameter_of_a_command_type`, `.No_public_member_of_any_Contracts_type_has_type_TenantId_or_TenantResolution` | Architecture |
| G4-23-03 (fails closed for unknown key, `default`, `None`, `Invalid`, database error) | E `EntitlementsFailClosedTests.Under_a_tenant_less_ambient_every_key_is_denied_with_no_exception_and_no_database_access`, `.Under_an_invalid_ambient_every_key_is_denied_with_no_exception_and_no_database_access`, `.An_unknown_key_and_default_FeatureKey_are_denied_for_a_tenant_without_touching_the_database`, `.A_database_error_propagates_and_is_never_turned_into_allowed_or_denied`, `.The_evaluation_counter_records_the_catalog_key_or_unknown_and_never_throws_on_a_default_key`; E `EntitlementsModuleBoundaryTests.The_entitlement_service_contains_no_catch_so_a_database_error_can_never_become_allowed` | Unit; Architecture |
| G4-23-03 (expiry at T minus one tick and at T, trials and overrides; combinations) | E `EntitlementsTrialTests.A_trial_is_active_one_tick_before_EndsAt_and_denied_at_EndsAt`, `.An_override_is_active_one_tick_before_ExpiresAt_and_denied_at_ExpiresAt`, `.An_expired_override_with_an_active_trial_is_allowed_through_the_trial`, `.An_expired_trial_with_an_active_override_is_allowed_through_the_override`, `.An_expired_trial_with_no_override_denies_the_Pro_key_and_allows_the_Free_key`, `.A_trial_is_not_active_before_its_StartsAt`; E `EntitlementsDomainTests.A_trial_is_active_from_StartsAt_up_to_but_not_including_EndsAt` | Integration; Unit |
| G4-23-03 (16-way StartTrial race) | E `EntitlementsTrialTests.Concurrent_trial_starts_for_one_tenant_create_exactly_one_TrialGrant`; a second start after expiry: `.A_tenant_cannot_start_a_second_trial_even_after_the_first_has_expired` | Integration |
| G4-23-04 (`decisya_entitlements` privileges fail with 42501; flags and membership; verifier) | M `EntitlementsRoleIntegrationTests.Decisya_entitlements_role_has_no_DDL_privilege_and_cannot_read_the_migrations_history_table`, `.Decisya_entitlements_role_can_read_write_update_and_delete_both_of_its_tables`, `.Decisya_entitlements_role_carries_no_elevated_attribute_and_no_role_membership`, `.The_entitlements_SCRAM_verifier_authenticates_and_is_stored_in_verifier_form`, `.Each_module_role_authenticates_only_with_its_own_password` | Integration |
| G4-23-04 (cross-schema, both directions) | M `EntitlementsRoleIntegrationTests.Decisya_entitlements_role_cannot_read_or_write_the_tenancy_schema`, `.Decisya_tenancy_role_cannot_read_or_write_the_entitlements_schema` | Integration |
| G4-23-04 (second password validation, including a trailing newline, before any connection) | M `MigrationRunnerValidationTests.RunAsync_throws_naming_only_the_entitlements_password_key_never_its_value_when_that_password_is_invalid` (Theory: null, empty, short, 31 characters, hyphen, quote, space, trailing `\n`), `.RunAsync_validates_the_tenancy_password_first_when_both_passwords_are_invalid` | Unit |
| G4-23-04 (idempotence with both roles; log capture; AppHost wiring) | M `EntitlementsRoleIntegrationTests.RunAsync_is_idempotent_with_both_roles_and_the_privilege_tests_still_hold_afterwards`; M `MigrationRunnerIntegrationTests.A_successful_run_logs_only_a_fixed_completion_message_never_the_password_or_connection_string` (both passwords); M `MigratorLogTests.A_failed_run_logs_only_the_configuration_key_name_never_the_password_or_connection_string`; SD `AppHostConfigurationTests.AppHost_cs_gives_decisya_api_only_the_tenancy_and_entitlements_connection_strings_and_waits_for_the_migrator`, `.AppHost_cs_gives_decisya_migrator_the_entitlements_role_password_and_no_entitlements_connection_string`, `.AppHost_cs_passes_secrets_only_through_parameters`; AppHost (Category=AppHost) `TenancyMigratorResourceTests` on a running AppHost (platform-dev, run by the orchestrator) | Integration; Unit |
| G4-23-05 (the override reason never appears in logs, errors, metric or activity tags) | E `EntitlementsReasonConfidentialityTests.A_marker_reason_never_appears_in_captured_logs_error_messages_metric_tags_or_activity_tags`, `.A_trial_or_tenant_log_line_names_only_the_fixed_fields_and_never_the_raw_command` | Integration |
| G4-23-05 (`[Sensitive]` placement and masking) | E `EntitlementsReasonConfidentialityTests.The_reason_is_marked_Sensitive_on_the_entity_property_and_on_the_command_record_property`, `.The_masking_processor_renders_the_Reason_of_a_logged_GrantOverride_and_a_FeatureOverride_as_masked` | Unit |
| S-1 (`get_NoTenant` on the bypass list, one violating fixture) | A `TenancyRuleTests.CrossTenantQueryRule_fails_on_an_unattributed_tenant_scope_mint` (case `UnattributedNoTenantMint`) | Architecture |
| S-2 (only `CallerContextMiddleware` mints a resolution in `Decisya.Api`) | AP `ApiBoundaryTests.Only_CallerContextMiddleware_mints_a_tenant_resolution_in_Decisya_Api`; AP `ApiBoundaryTests.Decisya_Api_csproj_has_exactly_the_ServiceDefaults_Tenancy_and_Entitlements_ProjectReferences_and_only_the_JwtBearer_package` | Architecture |
| S-3 (the 23505 path where the re-read finds no row) | E `GrantOverrideRaceRecoveryTests.When_the_23505_re_read_finds_no_row_because_the_winner_was_revoked_the_override_is_added_afresh_without_a_NullReferenceException`; `.When_the_23505_re_read_finds_the_winner_the_handler_replaces_it_and_leaves_exactly_one_row`; `.A_second_failure_in_the_retry_propagates_instead_of_looping`. The sequence is faked with a `SaveChangesInterceptor` over a real Postgres, so the lane is Integration, not the unit lane G3 suggested | Integration |

### Notes

- **`DbSet.Find(id)` is not required.** Neither G1 (Story 4 names a collection query, a query "by their own id", and update or delete by id) nor G3 (G4-23-01 names read by collection and by id, update and delete by id) mentions `Find`. The tests use `SingleOrDefaultAsync(x => x.Id == …)` and `ExecuteUpdate`/`ExecuteDelete`. No `Find` test was added.
- **Runs behind this table** (all green): E unit and architecture lane 59/59; E Integration 40/40; A 43/43 and AP unit lane 121/121 (run by the test-engineer); M Integration 17/17 three times and M unit lane 29/29; SD 176/176. The orchestrator's G4 runs (Tenancy 9, Migrator 17, Api 15, Persistence 14, Identity 32, AppHost 5 plus 1 skip, unit 893/894 with one BFF flake recorded in #83) are consistent with these.

<!-- gate: G5 | verdict: PASS | issue: #23 -->

