# Phase 0 – Modules.Tenancy: tenants, memberships and roles

## Issue 0.09 (#21) — Modules.Tenancy (tenant, membership, roles; invitations deferred)

### Scope note (role framing)

Most stories below are written **"As a tenant user"**, the framing #20 and #22 already
established for scenarios that protect a genuine user even where the concrete example is
adversarial (a forged claim, a race). Story 3, which needs a specific role *within* the
tenant rather than just membership in it, is written **"As an Owner"** — the `<role in
tenant>` framing this pipeline's own instructions ask for. Story 5 (the concurrency
conflict) is written **"As the platform operator"**, the same choice #20's Story 4 made:
a leaked stack trace or database value is an operational/security property of the API
itself, not something one caller experiences differently from another.

**Entitlement plan:** N/A for every story in this issue, for the same reason #22 and #32
recorded for their own primitives. A tenant, her membership and her role within it are
core platform infrastructure every subscription plan rests on; `IEntitlementService` and
the capability manifest (ADR-0008) don't exist until #23. Nothing here is gated by an
entitlement feature key (`module.feature`).

### Scope, tight (per the manifest's "Keep issues small" instruction)

In scope: a `Tenant` aggregate and a `Membership` aggregate (a user belongs to a tenant
with a role), both `ITenantScoped`, in a new `Modules.Tenancy` schema; two read endpoints
under `/api/tenancy` (`GET /api/tenancy/me`, `GET /api/tenancy/members`); how the module's
`Tenant`/`Membership` rows come to exist for the seeded dev tenants and users
(`docs/architecture/keycloak-realm.md`); how the `tenant_id` claim and the module's
tenants relate, including an unknown-but-well-formed claim; the request-scoped current
tenant and the pre-routing 403 for an `Invalid` claim (#22 B-1, B-2); a platform admin
with no tenant seeing no tenant data (#20 B-5); a generic 404 with no database values on
a concurrency conflict (#22 B-3); the two-tenant isolation test (the Done-when).

Out of scope, owned elsewhere: invitations — tokens, expiry, accept flow, email (#83);
the admin API, including renaming a tenant or changing anyone's role (#25); entitlements
(#23); audit logging of `[AllowCrossTenant]` usage (#24); the SPA. `Modules.Tenancy` gets
a `Contracts` assembly per ADR-0005's standard module shape, but nothing else consumes it
in phase 0.

### Design notes (adopted directly, not open questions)

- **`Tenant` carries no display name in phase 0.** The only data the platform has about a
  brand-new tenant at sign-in time is the `tenant_id` claim itself (`CallerIdentity` — #20
  — exposes only `UserId` and `TenantId`, no email or display name). Inventing a
  placeholder name now would be undone the moment #25 (admin API) lets an Owner set a
  real one. `Tenant` therefore has only `Id` (its own `TenantId`, doubling as
  `ITenantScoped.TenantId` — a tenant's row is scoped to itself) and `CreatedAt` (`Instant`).
- **`Membership` has `TenantId`, `UserId` (the token's `sub`, matching `CallerIdentity`'s
  type), `Role`, and `CreatedAt`.** `Role` is `Owner` or `Member`. Only `Owner` is ever
  assigned by this issue's own code path (Story 1); `Member` exists so the schema doesn't
  need a breaking change the day #83 ships invitations, but no scenario below exercises
  reaching it other than a seeded row (Story 3 Scenario 2).
- **A `tenant_id` claim naming a tenant this module has never seen is materialised, not
  rejected.** Keycloak's `tenant_id` user attribute is already `edit: ["admin"]`-only
  (`docs/architecture/keycloak-realm.md`) — an authenticated caller cannot set her own
  claim — so a well-formed, unknown `tenant_id` represents a fact an administrator already
  established, not a forged grant. The module's job is to record that fact the first time
  it's seen (Story 1), never to second-guess it.
- **A `tenant_id` claim naming a tenant that already exists, for a caller who has no
  `Membership` row of her own, is refused, never auto-joined.** Two different Keycloak
  users sharing one `tenant_id` attribute isn't reachable through this issue's own code
  (invitations, the only future path that adds a second member, is #83), so this can only
  happen through a Keycloak-side misconfiguration or a future bug. Failing closed (403,
  Story 1 Scenario 4) matches CLAUDE.md's BOLA principle: membership is never inferred
  from the claim alone.
- **A resolution of "no tenant" (platform admin) never creates, reads or lists anything.**
  For the module's own `TenantDbContext`-backed queries this is inherited for free from
  the global query filter's already-proven "None → zero rows" contract (#22 Story 4);
  Stories 2 and 3 make it an explicit, observable contract of the two endpoints.

---

## Open questions (recommended answers adopted above and below)

1. **How do the seeded dev tenants and users (`docs/architecture/keycloak-realm.md`:
   `dev-alice`, `dev-bob`, `dev-admin`) come to exist as `Tenant`/`Membership` rows in
   this module: a seeding step, or created on first sign-in?**
   **Recommended and adopted: created on first sign-in (JIT provisioning, Story 1).** No
   separate seed script has to stay in sync with the realm export's seeded users, dev and
   a real future SaaS signup share one mechanism instead of two, and the claim is already
   an admin-asserted fact (previous section), so materialising it on first use adds no new
   trust decision. Rejected alternative: a startup/migration seed reading a fixed list of
   dev tenant ids — it would need updating every time the realm's seed data changes, and
   would leave real tenant onboarding as a second, separately-designed path later.
2. **Does phase 0 need two administrative tiers within a tenant (`Owner` and a separate
   `Admin`), or is one enough, given the issue text says "a tenant owner or admin lists
   the tenant's members"?**
   **Recommended and adopted: one — `Owner` — for phase 0**, with `Member` defined but
   unused by any endpoint's happy path. No story in this issue's minimal API needs two
   administrative tiers to differ from each other; #25 (admin API) or a later #83
   invitations design are the first features that plausibly would. Rejected alternative:
   model both now — no scenario would exercise the difference, so it would ship as an
   untested, speculative distinction, exactly what "Keep issues small" warns against.

Flag either answer to Marco for override before G4 starts; if overridden, this file is
revised before implementation proceeds.

---

### Story 1 — A tenant user's tenant and Owner membership are created automatically on her first authenticated request, and never duplicated

As a tenant user, I want my tenant and my `Owner` membership to be created automatically
the first time I make an authenticated request carrying a `tenant_id` claim the platform
hasn't seen before, and reused unchanged on every later request, so that I never need a
separate signup step and I can never end up with two competing records of my own tenant.

#### Acceptance criteria

```gherkin
Feature: Tenant and Owner membership are created on first sign-in, never duplicated

  Scenario: A tenant user's very first authenticated request creates her tenant and her Owner membership
    Given "dev-alice" holds a genuine, valid access token whose tenant_id claim is "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    And the Tenancy module has no Tenant row for that id yet
    When a GET request is sent to /api/tenancy/me with "Authorization: Bearer <that token>"
    Then the response is 200
    And a Tenant row with id "7c9e6679-7425-40de-944b-e07fc1f90ae7" now exists
    And a Membership row exists for that tenant id and dev-alice's "sub" claim, with role "Owner"

  Scenario: A later request from the same tenant user reuses the existing tenant and membership, never duplicating them
    Given "dev-alice" has already made one authenticated request to a Tenancy endpoint, so her Tenant and Membership rows exist
    When a second GET request is sent to /api/tenancy/me with a fresh, genuine access token for the same tenant_id
    Then the response is 200
    And exactly one Tenant row exists for that tenant_id
    And exactly one Membership row exists for that tenant id and dev-alice's "sub" claim

  Scenario: A platform admin's request never creates a tenant
    Given "dev-admin" holds a genuine, valid access token whose claims carry no tenant_id
    When a GET request is sent to /api/tenancy/me with "Authorization: Bearer <that token>"
    Then the response is 200
    And no Tenant row is created
    And no Membership row is created

  Scenario: A caller whose tenant already exists but who has no membership of her own is not silently added to it
    Given a Tenant row already exists for tenant id "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b", created by "dev-bob"
    And no Membership row exists for that tenant id and a different user's "sub" claim
    When that different user sends a GET request to /api/tenancy/me with a genuine access token whose tenant_id claim is "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"
    Then the response is 403
    And no Membership row is created for that user
```

---

### Story 2 — A tenant user sees her own tenant and membership; a platform admin sees none

As a tenant user, I want to see my own tenant and my own membership's role, so that I
know which tenant the platform thinks I'm acting in — and as a platform admin whose
token legitimately carries no `tenant_id`, I want to see none, never every tenant's.

#### Acceptance criteria

```gherkin
Feature: GET /api/tenancy/me returns the caller's own tenant and membership, or nothing for "no tenant"

  Scenario: A tenant user sees her tenant and her own membership
    Given "dev-alice" holds a genuine, valid access token whose tenant_id claim is "7c9e6679-7425-40de-944b-e07fc1f90ae7", and her Tenant and Owner Membership rows already exist
    When a GET request is sent to /api/tenancy/me with "Authorization: Bearer <that token>"
    Then the response is 200
    And the response body's tenant.id equals "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    And the response body's membership.role equals "Owner"

  Scenario: A platform admin sees no tenant data
    Given "dev-admin" holds a genuine, valid access token whose claims carry no tenant_id
    When a GET request is sent to /api/tenancy/me with "Authorization: Bearer <that token>"
    Then the response is 200
    And the response body has no "tenant" field and no "membership" field

  Scenario: A tenant user never sees another tenant's data through this endpoint
    Given "dev-alice" holds a genuine, valid access token whose tenant_id claim is "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    And a different Tenant row and Membership row exist for tenant id "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"
    When a GET request is sent to /api/tenancy/me with "Authorization: Bearer <that token>"
    Then the response body's tenant.id equals "7c9e6679-7425-40de-944b-e07fc1f90ae7", and never "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"
```

---

### Story 3 — An Owner lists her tenant's members; nobody else can

As an Owner of a tenant, I want to list everyone who belongs to my tenant, so that I know
who has access to it — and as anyone else, including a member with no `Owner` role or a
platform admin with no tenant at all, I want to be refused, so that only the person
responsible for the tenant can see its membership list.

#### Acceptance criteria

```gherkin
Feature: GET /api/tenancy/members is available only to the tenant's Owner

  Scenario: The Owner lists her tenant's members
    Given "dev-alice" is the Owner of tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7", currently its only member
    When a GET request is sent to /api/tenancy/members with "Authorization: Bearer <dev-alice's access token>"
    Then the response is 200
    And the response body contains exactly one member, dev-alice's "sub" claim with role "Owner"

  Scenario: A non-owner member cannot list the tenant's members
    Given a Membership row exists for tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7" and a second user, with role "Member" (seeded directly for this test; the flow that would create such a row through an invitation is out of scope, #83)
    When that second user sends a GET request to /api/tenancy/members with her own genuine access token for the same tenant_id
    Then the response is 403

  Scenario: A platform admin cannot list any tenant's members
    Given "dev-admin" holds a genuine, valid access token whose claims carry no tenant_id
    When a GET request is sent to /api/tenancy/members with "Authorization: Bearer <that token>"
    Then the response is 403

  Scenario: An Owner never sees another tenant's members
    Given "dev-alice" is the Owner of tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    And "dev-bob" is the Owner of a different tenant, "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"
    When "dev-alice" sends a GET request to /api/tenancy/members with her own access token
    Then the response body never contains dev-bob's "sub" claim
```

---

### Story 4 — The current tenant is resolved once per request, and an invalid claim never reaches an endpoint

As a tenant user, I want the platform to resolve my current tenant exactly once for the
whole lifetime of my request, and to refuse a request whose `tenant_id` claim is
malformed before any endpoint handler runs, so that a coding mistake elsewhere in the
request pipeline can never make my calls see a different tenant partway through, or let
a corrupted claim quietly fall through to a query (carried in from #22 B-1, B-2).

#### Acceptance criteria

```gherkin
Feature: The production current-tenant service is request-scoped, set once, and Invalid is rejected before routing

  Scenario: The production ICurrentTenant is registered with a scoped (per-request) lifetime
    Given Decisya.Api's service collection, as configured for the Tenancy module
    When the registration for the production current-tenant implementation is inspected
    Then its service lifetime is Scoped, never Singleton and never Transient

  Scenario: Every read of the current tenant within one request returns the same resolution
    Given an authenticated request whose tenant_id claim resolves to a specific tenant
    When the current tenant is read from more than one place while handling that single request
    Then every read returns the identical resolution, for the same tenant

  Scenario: A malformed tenant_id claim gets 403 before any Tenancy endpoint runs
    Given a request carries a genuine, validly-signed access token whose tenant_id claim is "not-a-guid"
    When a GET request is sent to /api/tenancy/me with "Authorization: Bearer <that token>"
    Then the response is 403
    And no TenantDbContext query executes for that request
```

---

### Story 5 — A database concurrency conflict is reported as a generic 404, never with database values

As the platform operator, I want any concurrency conflict raised while writing through a
`TenantDbContext` (EF Core's `DbUpdateConcurrencyException`) to reach the caller as a
generic 404 with no entity, column or row value from either side of the conflict, and to
reach the structured log with full detail and a trace id, so that a race between two
writes can never leak another row's content or a stack trace to whoever triggered it
(carried in from #22 B-3).

#### Acceptance criteria

```gherkin
Feature: A concurrency conflict returns a generic 404 and never database values

  Scenario: A concurrency conflict during a Tenancy write is reported as a generic 404
    Given a write against the Tenancy module's database raises a concurrency conflict (DbUpdateConcurrencyException)
    When the response is returned to the caller
    Then the response is 404 with a generic ProblemDetails body
    And the body contains no entity name, column name or row value from either side of the conflict
    And the conflict's full detail, together with a trace id, is written only to the structured log
```

---

### Story 6 — A two-tenant isolation test proves Tenant and Membership rows never cross tenants (the Done-when)

As a tenant user, I want my `Tenant` row and my `Membership` rows to be provably
unreachable from another tenant — by a read, and for `Membership`, by an update or delete
by id — so that #21 ships with the same proof #22's own isolation test gave the platform
primitive it builds on, this time against real aggregates.

#### Acceptance criteria

```gherkin
Feature: A two-tenant isolation test proves read and write isolation for Tenant and Membership

  Background:
    Given PostgresFixture provides a real Postgres 18 instance
    And a Tenant row and an Owner Membership row exist for tenant A ("7c9e6679-7425-40de-944b-e07fc1f90ae7")
    And a different Tenant row and Owner Membership row exist for tenant B ("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b")

  Scenario: Tenant A cannot read tenant B's Tenant row
    Given the context's ambient tenant is tenant A
    When tenant B's Tenant row is queried, by a collection query and by its own id
    Then neither query returns tenant B's row

  Scenario: Tenant A cannot read tenant B's Membership rows
    Given the context's ambient tenant is tenant A
    When tenant B's Membership rows are queried, by a collection query and by their own id
    Then none of tenant B's Membership rows are returned

  Scenario: Tenant A cannot update or delete tenant B's Membership row by id
    Given the context's ambient tenant is tenant A
    When an update or a delete is attempted against tenant B's Membership row's id
    Then zero rows are affected
    And tenant B's Membership row is unchanged in the database afterward
```

---

## Non-functional requirements

Three rows are added to `docs/requirements/nfr.md`: NFR-31, NFR-32, NFR-33.

| Id | Category | Target (summary) |
| --- | --- | --- |
| NFR-31 | Security | Zero cross-tenant `Tenant` or `Membership` rows are ever returned by a read, or affected by an update/delete by id, across the full two-tenant isolation matrix |
| NFR-32 | Reliability | Under concurrent first-sign-in requests for the same never-before-seen tenant_id, exactly one `Tenant` row and exactly one `Owner` `Membership` row are ever created for it — zero duplicates |
| NFR-33 | Security | Every `DbUpdateConcurrencyException` raised while handling a `Decisya.Api` request produces a 404 generic `ProblemDetails` response with zero entity, column or row values; full detail reaches only the structured log |

<!-- gate: G1 | verdict: PASS | issue: #21 -->

## Traceability

Project prefixes: **Tenancy.Tests** = `tests/Modules/Decisya.Modules.Tenancy.Tests`; **Api.Tests** = `tests/Decisya.Api.Tests`; **Migrator.Tests** = `tests/Decisya.Infrastructure.Migrator.Tests`. All Api.Tests Integration tests connect as the database owner, not as `decisya_tenancy` (recorded in #83); the role's DML-only restriction is covered by Migrator.Tests (G4-21-05 below).

| Story | Scenario / NFR | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A tenant user's very first authenticated request creates her tenant and her Owner membership | Api.Tests `TenancyEndpointScenarioTests.A_tenant_users_very_first_authenticated_request_creates_her_tenant_and_her_Owner_membership` | Integration |
| 1 | A later request from the same tenant user reuses the existing tenant and membership, never duplicating them | Api.Tests `TenancyEndpointScenarioTests.A_later_request_from_the_same_tenant_user_reuses_the_existing_tenant_and_membership_never_duplicating_them` | Integration |
| 1 | A platform admin's request never creates a tenant | Api.Tests `TenancyEndpointScenarioTests.A_platform_admins_request_never_creates_a_tenant_and_sees_no_tenant_data`; Tenancy.Tests `TenantMembershipGateTests.A_caller_with_no_tenant_resolution_writes_nothing_even_if_the_gate_is_invoked_directly` | Integration |
| 1 | A caller whose tenant already exists but who has no membership of her own is not silently added to it | Tenancy.Tests `TenantMembershipGateTests.A_caller_whose_tenant_already_exists_but_who_has_no_membership_of_her_own_is_refused_and_writes_no_row`; Api.Tests `TenancyPipelineIntegrationTests.A_different_caller_for_an_existing_tenant_is_refused_on_both_routes_and_creates_no_membership` | Integration |
| 2 | A tenant user sees her tenant and her own membership | Api.Tests `TenancyEndpointScenarioTests.A_tenant_user_sees_her_tenant_and_her_own_membership` | Integration |
| 2 | A platform admin sees no tenant data | Api.Tests `TenancyEndpointScenarioTests.A_platform_admins_request_never_creates_a_tenant_and_sees_no_tenant_data` | Integration |
| 2 | A tenant user never sees another tenant's data through this endpoint | Api.Tests `TenancyEndpointScenarioTests.A_tenant_user_never_sees_another_tenants_data_through_this_endpoint`; `TenancyPipelineIntegrationTests.Interleaved_requests_from_two_tenants_never_cross_and_every_log_pairing_stays_consistent` | Integration |
| 3 | The Owner lists her tenant's members | Api.Tests `TenancyEndpointScenarioTests.The_Owner_lists_her_tenants_members_and_never_sees_another_tenants_members` | Integration |
| 3 | A non-owner member cannot list the tenant's members | Api.Tests `TenancyPipelineIntegrationTests.A_member_with_no_owner_role_cannot_list_the_tenants_members` | Integration |
| 3 | A platform admin cannot list any tenant's members | Api.Tests `TenancyPipelineIntegrationTests.A_caller_with_no_tenant_cannot_list_any_tenants_members`; `EndpointAuthorizationTests.Api_tenancy_members_carries_the_Tenancy_Owner_policy` | Integration, Unit |
| 3 | An Owner never sees another tenant's members | Api.Tests `TenancyEndpointScenarioTests.The_Owner_lists_her_tenants_members_and_never_sees_another_tenants_members` | Integration |
| 4 | The production ICurrentTenant is registered with a scoped (per-request) lifetime | Api.Tests `CallerContextTests.ICurrentTenant_and_ICurrentCaller_are_registered_exactly_once_as_Scoped`; `CallerContextTests.A_singleton_capturing_ICurrentTenant_fails_to_build_in_every_environment` | Unit |
| 4 | Every read of the current tenant within one request returns the same resolution | Api.Tests `CallerContextTests.Every_read_of_the_current_tenant_within_one_request_scope_returns_the_identical_resolution`; `CallerContextTests.Set_called_twice_throws_even_with_identical_values`; `CallerContextTests.Before_Set_the_resolution_is_Invalid_and_UserId_throws` | Unit |
| 4 | A malformed tenant_id claim gets 403 before any Tenancy endpoint runs | Api.Tests `TenantClaimForbiddenTests.A_malformed_tenant_id_claim_gives_403_before_routing_with_no_database_access` (case "not-a-guid"; the host holds an unreachable placeholder database, so a 403 proves no query ran) | Unit |
| 5 | A concurrency conflict during a Tenancy write is reported as a generic 404 | Api.Tests `ConcurrencyConflictHttpRoundTripTests.A_concurrency_conflict_during_a_Tenancy_write_is_reported_as_a_generic_404` (real EF conflict over HTTP; body and log checked); `ConcurrencyConflictExceptionHandlerTests.A_direct_DbUpdateConcurrencyException_is_reported_as_a_generic_404`, `.A_wrapped_DbUpdateConcurrencyException_is_reported_as_a_generic_404`, `.An_unrelated_exception_is_not_handled` | Integration, Unit |
| 6 | Tenant A cannot read tenant B's Tenant row | Tenancy.Tests `TenancyIsolationTests.Tenant_A_cannot_read_tenant_Bs_Tenant_row` | Integration |
| 6 | Tenant A cannot read tenant B's Membership rows | Tenancy.Tests `TenancyIsolationTests.Tenant_A_cannot_read_tenant_Bs_Membership_rows` | Integration |
| 6 | Tenant A cannot update or delete tenant B's Membership row by id | Tenancy.Tests `TenancyIsolationTests.Tenant_A_cannot_update_or_delete_tenant_Bs_Membership_row_by_id` | Integration |
| NFR-31 | Zero cross-tenant Tenant or Membership rows read, updated or deleted | The three Story 6 tests above | Integration |
| NFR-32 | Concurrent first sign-in creates exactly one Tenant and one Owner Membership | Tenancy.Tests `TenantMembershipGateRaceTests.N16_parallel_first_sign_in_requests_for_one_new_tenant_and_one_user_create_exactly_one_tenant_and_one_Owner_membership`, `.N16_parallel_first_sign_in_requests_for_one_new_tenant_and_two_users_produce_exactly_one_Owner_and_refuse_every_call_of_the_other_user`. The NFR row names concurrent `/api/tenancy/me` requests; the tests drive the gate (`EnsureAsync`) directly with separate contexts, the layer that decides the race | Integration |
| NFR-33 | Every DbUpdateConcurrencyException gives a generic 404 with no values | The Story 5 tests above | Integration, Unit |

### G3 MUST coverage (G4-21-01 to G4-21-05)

| MUST | Test(s) | Gap |
| --- | --- | --- |
| G4-21-01 scoped caller, set once, scope validation | Api.Tests `CallerContextTests` (all), `TenancyPipelineIntegrationTests.Interleaved_requests_from_two_tenants_never_cross_and_every_log_pairing_stays_consistent` | none |
| G4-21-02 fail-closed pipeline, generic 403s | Api.Tests `TenantClaimForbiddenTests`, `EndpointAuthorizationTests.The_tenant_membership_opt_out_set_is_exactly_api_whoami`, `.The_anonymous_endpoint_set_is_exactly_health_and_alive`, `.Api_tenancy_members_carries_the_Tenancy_Owner_policy`, `TenancyPipelineIntegrationTests` (refusal, Member, None cases) | none |
| G4-21-03 JIT never claims an existing tenant; race decided by the database | Tenancy.Tests `TenantMembershipGateTests`, `TenantMembershipGateRaceTests`, `TenantMembershipGateConstraintMatchingTests`, `TenantMembershipGateTransactionGuardTests` | none |
| G4-21-04 conflict returns nothing; no sensitive EF switch | ArchitectureTests `NoSensitiveEfSwitchesTests.No_file_under_src_enables_a_sensitive_EF_or_Npgsql_diagnostic_switch`; Api.Tests `ConcurrencyConflictHttpRoundTripTests`; Tenancy.Tests `TenantMembershipGateRaceLogCaptureTests.The_two_user_provisioning_race_captures_no_raw_sub_or_tenant_id_at_Debug_level` | none |
| G4-21-05 API is DML-only; password out of every log | Migrator.Tests `MigrationRunnerIntegrationTests` (role has no DDL, idempotent, SCRAM round trip, no password in logs), `MigrationRunnerValidationTests`, `MigratorLogTests`, `ScramSha256VerifierTests`; ServiceDefaults.Tests `AppHostConfigurationTests` and AppHost.Tests `TenancyMigratorResourceTests` (the API receives only the tenancy string) | The API's own `current_user = decisya_tenancy` proof (G3 T-12 point 2) is not in Api.Tests; the role restriction is covered by Migrator.Tests, and the Api.Tests owner connection is recorded in #83 |

### Run status (G5)

Unit lane: 819 passed, 0 failed. Integration lane, run with the container engine up:

| Project | Run | Total | Passed | Failed |
| --- | --- | --- | --- | --- |
| `Decisya.Modules.Tenancy.Tests` | 1 | 9 | 9 | 0 |
| `Decisya.Modules.Tenancy.Tests` | 2 | 9 | 9 | 0 |
| `Decisya.Modules.Tenancy.Tests` | 3 | 9 | 9 | 0 |
| `Decisya.Api.Tests` | 1 | 15 | 15 | 0 |
| `Decisya.Infrastructure.Migrator.Tests` | 1 | 8 | 8 | 0 |
| `Decisya.Infrastructure.Persistence.Tests` | 1 | 14 | 14 | 0 |

<!-- gate: G5 | verdict: PASS | issue: #21 -->
