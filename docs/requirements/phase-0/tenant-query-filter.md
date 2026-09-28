# Phase 0 – Global tenant query filter + NetArchTest ITenantScoped rule

## Issue 0.10 (#22) — ITenantScoped, TenantDbContext, the AllowCrossTenant escape hatch, PostgresFixture, the isolation test

### Scope note (role framing)

`ITenantScoped` and `TenantDbContext` are platform primitives, in the same sense #32's
`TenantId` and `Result` were: no module, no `Contracts` project and no endpoint exists
here (that is #21). Stories that describe the **contract itself** (the interface shape,
the architecture test, the test fixture) use **"As a Decisya module developer"**, the
framing #32 adopted for the same reason. Stories that describe **what the filter
guarantees** — a tenant's data never reaching another tenant, a write never landing in
the wrong tenant, an unparsable claim never being treated as "no tenant" — use **"As a
tenant user"**, the framing #20 used for its own adversarial-but-user-protecting
scenarios (Story 2, Story 3): the mechanism under test is a query filter or a `SaveChanges`
override, not something a genuine tenant user's browser triggers directly, but the person
the control protects is still her.

**Entitlement plan:** N/A for every story in this issue, for the same reason as #32.
`ITenantScoped` and `TenantDbContext` are core platform infrastructure that every
subscription plan's data rests on; nothing here is gated by an entitlement feature key.

**Scope, tight (per the manifest's "Keep issues small" instruction):**

- The `ITenantScoped` contract that `CLAUDE.md` invariant 1 requires every persisted
  aggregate to implement.
- A `TenantDbContext` base class whose global query filter scopes every `ITenantScoped`
  entity to the current tenant.
- Where "the current tenant" comes from in a request, in the abstract: the validated
  identity's `tenant_id` claim (`Decisya.Api`'s `CallerIdentity.TenantId`, per #20) —
  and the three outcomes that resolving it can have: no claim at all (legitimate, e.g. a
  platform admin), a well-formed claim, or a malformed one.
- What happens under each outcome: no claim means no tenant's data, never every
  tenant's; a malformed claim is rejected outright, never silently treated as "no
  tenant" and never treated as any particular tenant.
- Writes: a row can never be persisted under a tenant other than the one that created it,
  and an entity with no tenant assigned can never be persisted at all.
- The `[AllowCrossTenant]` escape hatch: its shape, and what it requires so #24's audit
  log has something to record — #24 owns writing the audit log itself.
- The NetArchTest rule that every persisted entity implements `ITenantScoped` — the
  Done-when: red on a deliberate violation, green once it's fixed.
- `PostgresFixture`, a shared Testcontainers fixture for integration tests.
- A two-tenant isolation test against one small, test-only entity, proving the filter
  before #21 depends on it.

**Out of scope, with the owning issue:**

- **A real `Tenants` table, a real tenant-onboarding flow, or any business module.**
  Owned by **#21** (`Modules.Tenancy`). This issue's isolation test uses one small,
  test-only entity, never a real aggregate.
- **The ASP.NET Core middleware that reads `CallerIdentity.TenantId` and populates the
  ambient tenant accessor this issue defines, including turning a malformed claim into
  an HTTP 403.** See Open question 1 — recommended and adopted: deferred to **#21**,
  the first issue that mounts an authenticated endpoint against `TenantDbContext`. This
  issue proves the resolution logic and the `TenantDbContext` behavior directly, by
  supplying the ambient tenant to the context in tests, not by running a live HTTP
  request through Keycloak.
- **Persisting an audit record for an `[AllowCrossTenant]` call.** Owned by **#24**.
  This issue only defines the attribute's shape and requires it to carry a
  justification, so #24 has something non-empty to log.
- **Redis and object-storage key prefixing by tenant, and the Wolverine envelope tenant
  middleware.** Owned by ADR-0001/ADR-0005's later issues (object storage, messaging).
- **Postgres row-level security** (ADR-0001 option 4). Deferred, not rejected, per the
  ADR; nothing here blocks adding it later.
- **A real module's `DbContext`.** `module-scaffold`'s "tenancy" phase (used by #21 and
  every later module) is what actually derives from `TenantDbContext`; this issue ships
  the base class only.

---

## Open questions (recommended answers adopted below)

1. **Does #22 wire the ASP.NET Core middleware that turns `CallerIdentity.TenantId`
   into the ambient tenant, and a malformed claim into a 403, into `Decisya.Api` now —
   or is that deferred?**
   **Recommended and adopted: deferred to #21.** The manifest's own G4 path list for
   this issue names `src/Decisya.SharedKernel/**`, `src/Decisya.Infrastructure*/**`,
   `tests/Decisya.ArchitectureTests/**` and `tests/Decisya.TestInfrastructure/**` — not
   `src/Decisya.Api`. #22 ships the pure claim-resolution logic (Story 3) and
   `TenantDbContext`'s behavior under each outcome, both provable without touching
   `Decisya.Api` or running a real login. #21 is a few lines of host wiring once
   `Modules.Tenancy` has its first authenticated endpoint to prove it against.
2. **On a cross-tenant or untenanted write, does `TenantDbContext` throw, or return a
   `Result` failure?**
   **Recommended and adopted: throw a dedicated exception** (e.g.
   `CrossTenantWriteException`) from `SaveChanges`/`SaveChangesAsync`. This is a
   programming-error-class violation — a handler building an entity with the wrong (or
   no) `TenantId` — not an expected business outcome a caller should branch on with
   `Result.Match`. It is exactly the shape `CLAUDE.md`'s error principle and #20 Story 4
   already handle once a host exists: an unhandled exception becomes a generic
   `ProblemDetails`, never a leaked detail.
3. **Is `ITenantScoped.TenantId` auto-stamped by `TenantDbContext` when an entity is
   added without one, or must every entity's own constructor set it, with
   `TenantDbContext` only verifying?**
   **Recommended and adopted: no auto-stamp.** `TenantId` is a get-only property that a
   concrete entity's constructor sets, the same immutability precedent `TenantId` itself
   set in #32. `TenantDbContext.SaveChanges` only verifies — reject default/uninitialized,
   reject mismatched — so no module can come to depend on ambient auto-fill for
   something as sensitive as which tenant owns a row.

Flag any of the three above to Marco for override before G4 starts; if overridden, this
file is revised before implementation proceeds. Names below (`ICurrentTenantContext`,
`TenantClaimResolver`, `TenantClaimResolution`, `TenantScopedProbe`,
`CrossTenantWriteException`) are illustrative for the Gherkin steps below; G2 may rename
them without weakening any scenario.

---

### Story 1 — ITenantScoped is a uniform, immutable contract every persisted entity implements

As a Decisya module developer, I want a single `ITenantScoped` interface exposing a
`TenantId` that cannot be reassigned once an entity is constructed, so that tenant
scoping is uniform across every module's `Domain` layer and can never be silently
dropped or mutated by a later change (`CLAUDE.md` invariant 1: "every persisted
aggregate implements `ITenantScoped` and carries `TenantId`").

#### Acceptance criteria

```gherkin
Feature: ITenantScoped is a uniform, immutable contract

  Scenario: ITenantScoped exposes exactly one read-only TenantId member
    Given the ITenantScoped interface
    When its public members are inspected
    Then it declares exactly one member, a TenantId-typed property named TenantId
    And that property has no public setter

  Scenario: An entity implementing ITenantScoped exposes no way to change its tenant after construction
    Given a test entity implementing ITenantScoped
    When its public and internal members are inspected
    Then no member allows assigning a new value to TenantId after the entity is constructed

  Scenario: An entity cannot be constructed with an uninitialized TenantId
    Given a test entity implementing ITenantScoped, constructed with default(TenantId)
    When the entity is later added to a TenantDbContext and SaveChanges is called
    Then the save is rejected before any row is written (see Story 5)
```

---

### Story 2 — A tenant user's queries never return another tenant's rows

As a tenant user, I want every query issued through a `TenantDbContext` to be
automatically scoped to my tenant, so that no query — however it is written, including
a query by primary key — can return another tenant's data because of a coding mistake
in a module I don't control.

#### Acceptance criteria

```gherkin
Feature: The global tenant query filter scopes every ITenantScoped query

  Background:
    Given a TenantDbContext-derived context, backed by a real Postgres database
    And a row of a small ITenantScoped test entity ("TenantScopedProbe") exists for tenant A
    And a different row of the same entity exists for tenant B

  Scenario: A caller scoped to tenant A never sees tenant B's row in a collection query
    Given the context's ambient tenant is tenant A
    When all TenantScopedProbe rows are queried
    Then only tenant A's row is returned
    And tenant B's row does not appear, in any form

  Scenario: A caller scoped to tenant A never fetches tenant B's row by its own id
    Given the context's ambient tenant is tenant A
    When TenantScopedProbe is queried by tenant B's row's own primary key
    Then no row is returned

  Scenario: The filter cannot be bypassed by a plain LINQ query written without thinking about tenancy
    Given the context's ambient tenant is tenant A
    When a query is written with no explicit tenant predicate at all (e.g. "where Name == x")
    Then the result set still never contains a row belonging to any tenant other than A
```

---

### Story 3 — The current tenant is resolved from the caller's validated claim, with three distinct outcomes

As a tenant user, I want the platform to resolve exactly one of three outcomes from my
authenticated identity's `tenant_id` claim — "no tenant" (legitimate, e.g. a platform
admin), a specific tenant, or "invalid" — and to never confuse the first with the third,
so that a malformed claim can never be quietly treated as "show me nothing" instead of
being rejected outright, and so a missing claim can never be treated as "show me
everything."

#### Acceptance criteria

```gherkin
Feature: Current-tenant resolution has three distinct, non-overlapping outcomes

  Scenario: A caller with no tenant_id claim at all resolves to "no tenant"
    Given a validated identity with no tenant_id claim present
    When the platform resolves the caller's current tenant
    Then the resolution is "no tenant"
    And the resolution is not an error
    And the resolution is not the all-zero GUID or any default TenantId

  Scenario: A caller with a well-formed tenant_id claim resolves to that tenant
    Given a validated identity whose tenant_id claim is "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    When the platform resolves the caller's current tenant
    Then the resolution is the TenantId parsed from that value

  Scenario: A caller with a malformed tenant_id claim resolves to "invalid", never to "no tenant" and never to a guessed tenant
    Given a validated identity whose tenant_id claim is "not-a-guid"
    When the platform resolves the caller's current tenant
    Then the resolution is "invalid"
    And the resolution is distinct from "no tenant"
    And the resolution names no specific TenantId

  Scenario: A caller with a malformed tenant_id claim never reaches a tenant-scoped query
    Given a resolution of "invalid" for a request
    When that request's code path is inspected
    Then no TenantDbContext query executes using that resolution
    And no fallback to "no tenant" behavior occurs
```

---

### Story 4 — A caller with no tenant sees no tenant's data, never every tenant's

As a tenant user — and, in this scenario, as a platform admin whose token legitimately
carries no `tenant_id` — I want a caller resolved to "no tenant" to see zero rows from
every `ITenantScoped` query, never every tenant's rows, so that a missing tenant claim
can never turn into an accidental full-tenant-base data dump (carried in from #20/#83
B-5: "a missing `tenant_id` means no tenant data, never all tenants").

#### Acceptance criteria

```gherkin
Feature: "No tenant" yields zero rows, never every tenant's rows

  Background:
    Given a TenantDbContext-derived context, backed by a real Postgres database
    And TenantScopedProbe rows exist for at least two different tenants

  Scenario: A "no tenant" caller's collection query returns nothing
    Given the context's ambient tenant resolution is "no tenant"
    When all TenantScopedProbe rows are queried
    Then zero rows are returned

  Scenario: A "no tenant" caller's query by another tenant's row id returns nothing
    Given the context's ambient tenant resolution is "no tenant"
    When TenantScopedProbe is queried by an existing row's primary key
    Then no row is returned
```

---

### Story 5 — A row can never be saved into the wrong tenant, or with no tenant at all

As a tenant user, I want the platform to refuse to persist a row whose `TenantId`
doesn't match my own, and to refuse to persist a row with no tenant assigned, so that
even a bug in a module's handler can't create or move data into someone else's tenant
or leave a row untenanted.

#### Acceptance criteria

```gherkin
Feature: Writes can never cross a tenant boundary or skip tenancy entirely

  Background:
    Given a TenantDbContext-derived context, backed by a real Postgres database
    And the context's ambient tenant is tenant A

  Scenario: Saving a new row tagged with a different tenant is rejected
    Given a new TenantScopedProbe entity constructed with tenant B's TenantId
    When it is added to the context and SaveChanges is called
    Then the save is rejected before any row is written
    And no row for that entity exists afterward, under any tenant

  Scenario: Saving a new row with no tenant assigned is rejected
    Given a new TenantScopedProbe entity constructed with default(TenantId)
    When it is added to the context and SaveChanges is called
    Then the save is rejected before any row is written

  Scenario: Modifying an existing row so its tenant would change is rejected
    Given an existing TenantScopedProbe row belonging to tenant A, loaded through the context
    When its TenantId is changed to tenant B's and SaveChanges is called
    Then the save is rejected
    And the row's TenantId is unchanged afterward

  Scenario: A same-tenant write succeeds normally
    Given a new TenantScopedProbe entity constructed with tenant A's TenantId
    When it is added to the context and SaveChanges is called
    Then the save succeeds
    And the row is retrievable afterward under tenant A's ambient context
```

---

### Story 6 — Crossing the tenant filter is possible only from an attributed, justified admin handler

As a Decisya module developer, I want the only way to bypass the tenant filter to be a
type explicitly marked `[AllowCrossTenant]` with a non-empty justification, so that
cross-tenant access stays a rare, reviewable exception rather than something any handler
can reach for, and so #24's audit log always has a real reason to record.

#### Acceptance criteria

```gherkin
Feature: AllowCrossTenant is the only escape from the tenant filter, and it must justify itself

  Scenario: A type not marked AllowCrossTenant cannot ignore the tenant filter
    Given a handler type that calls IgnoreQueryFilters() on a TenantDbContext query, and does not carry [AllowCrossTenant]
    When the architecture test for cross-tenant access runs
    Then it fails, naming the offending type

  Scenario: A type marked AllowCrossTenant with a justification may ignore the tenant filter
    Given a handler type that calls IgnoreQueryFilters() and carries [AllowCrossTenant("reconciliation report for platform support ticket #123")]
    When the architecture test for cross-tenant access runs
    Then it passes

  Scenario: AllowCrossTenant without a justification is rejected
    Given a type carrying [AllowCrossTenant] with an empty or whitespace-only justification
    When the attribute-usage check runs
    Then it fails, naming the offending type

  Scenario: Every AllowCrossTenant usage is discoverable for #24's future audit log
    Given one or more types in the compiled assemblies carry [AllowCrossTenant]
    When they are enumerated by reflection
    Then each one's type name and justification text can be read back exactly as declared
```

---

### Story 7 — The architecture test fails on a deliberate ITenantScoped violation, then passes (the Done-when)

As a Decisya module developer, I want a NetArchTest rule that fails the build the
moment any type mapped into a `TenantDbContext` model doesn't implement `ITenantScoped`,
so a missing implementation is caught at build time, never discovered later as a data
leak in production.

#### Acceptance criteria

```gherkin
Feature: Every persisted entity implements ITenantScoped, enforced by an architecture test

  Scenario: A deliberate violation fails the test
    Given a fixture entity type mapped into a TenantDbContext-derived model, that does not implement ITenantScoped
    When the Decisya.ArchitectureTests rule for ITenantScoped runs
    Then it fails
    And the failure names the violating type

  Scenario: Fixing the violation turns the test green
    Given the same fixture entity type, now implementing ITenantScoped
    When the Decisya.ArchitectureTests rule for ITenantScoped runs again
    Then it passes

  Scenario: A compliant entity never trips the rule
    Given every entity type mapped into every TenantDbContext-derived model implements ITenantScoped
    When the Decisya.ArchitectureTests rule for ITenantScoped runs
    Then it passes
```

---

### Story 8 — PostgresFixture gives every module a real Postgres to test isolation against

As a Decisya module developer, I want a shared, reusable `PostgresFixture` built on
Testcontainers, so that isolation tests and schema tests run against a real Postgres 18
instance instead of an in-memory provider that would hide a real filter bug (EF Core's
InMemory provider doesn't enforce the same SQL null semantics the tenant filter relies
on).

#### Acceptance criteria

```gherkin
Feature: PostgresFixture provisions a real, disposable Postgres instance for tests

  Scenario: The fixture starts a working Postgres container
    Given no external Postgres server is configured
    When a test collection using PostgresFixture starts
    Then a Postgres 18 container starts
    And the fixture exposes a connection string that a TenantDbContext can connect with

  Scenario: The fixture is shared across the tests in one collection
    Given two test classes in the same PostgresFixture-backed collection
    When both run
    Then both connect to the same container instance, started once

  Scenario: The fixture tears down cleanly
    Given a PostgresFixture-backed test collection has finished running
    When the collection disposes the fixture
    Then the container is stopped and removed, leaving no orphaned container behind
```

---

### Story 9 — A two-tenant isolation test proves the filter before #21 depends on it

As a Decisya module developer, I want a concrete two-tenant isolation test against one
small, test-only entity, so that #21 and every later module can build on a filter that
is already proven correct, rather than re-deriving the proof themselves (the
`isolation-test` skill's own contract: "one read test and one update/delete-by-id test
per aggregate root").

#### Acceptance criteria

```gherkin
Feature: A two-tenant isolation test proves read and write isolation on a real Postgres instance

  Background:
    Given PostgresFixture provides a real Postgres 18 instance
    And a TenantScopedProbe row exists for tenant A
    And a different TenantScopedProbe row exists for tenant B

  Scenario: Tenant A cannot read tenant B's row
    Given the context's ambient tenant is tenant A
    When tenant B's row is queried, by a collection query and by its own primary key
    Then neither query returns tenant B's row

  Scenario: Tenant A cannot update or delete tenant B's row by id
    Given the context's ambient tenant is tenant A
    When an update or a delete is attempted against tenant B's row's primary key
    Then zero rows are affected
    And tenant B's row is unchanged in the database afterward
```

---

## Non-functional requirements

Three rows are added to `docs/requirements/nfr.md`: NFR-28, NFR-29, NFR-30 (see that
file for the full table).

| Id | Category | Target (summary) |
| --- | --- | --- |
| NFR-28 | Security | Every EF-mapped entity type in every `TenantDbContext`-derived model implements `ITenantScoped`; zero exceptions |
| NFR-29 | Security | Zero cross-tenant rows returned by a read or affected by a write, across the full two-tenant isolation matrix |
| NFR-30 | Reliability | "No tenant" always yields zero rows; "invalid" never reaches a query; both hold across 100% of resolutions |

<!-- gate: G1 | verdict: PASS | issue: #22 -->

## Traceability

28 scenarios across 9 stories, plus NFR-28 to NFR-30. One test was missing
(Story 1, Scenario 1 — nothing reflected over `ITenantScoped` itself) and is added below,
a few lines, per the issue skill's "Keep issues small" proportion rule; nothing else
needed a new test.

**EF read-back hazard (G4-22-03/T-07):** `EntityEntry.GetDatabaseValuesAsync()` and
`ReloadAsync()` both look a row up by primary key alone, ignoring the tenant filter, and
return another tenant's real row content. No Gherkin scenario in this file names those two
members directly (Story 2 and Story 6 Scenario 1 talk about collection/by-id queries and
`IgnoreQueryFilters()`), but they are the same class of hazard Story 6 Scenario 1 guards
against — "a type not marked `[AllowCrossTenant]` cannot ignore the tenant filter" — reached
through a different EF code path than an explicit `IgnoreQueryFilters()` call. The control
is `CrossTenantQueryRule`'s ban on both members outside `[AllowCrossTenant]`, already
covered by `TenancyRuleTests.CrossTenantQueryRule_fails_on_an_EntityEntry_read_back_bypass`.
`CrossTenantReadBackTests`'s two tests do not test that control: they pin the underlying EF
behaviour the ban depends on, against real Postgres, and say explicitly to revisit the ban
(not flip the assertion) if EF ever stops leaking. Row "6 (read-back hazard)" below records
both halves.

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | ITenantScoped exposes exactly one read-only TenantId member | `ITenantScopedShapeTests.ITenantScoped_declares_exactly_one_member_a_read_only_TenantId_property` (added at G5) | Unit |
| 1 | An entity implementing ITenantScoped exposes no way to change its tenant after construction | `TenancyRuleTests.TenantIdImmutabilityRule_fails_on_an_entity_with_a_TenantId_setter`, `TenancyRuleTests.TenantIdImmutabilityRule_passes_on_an_entity_with_a_get_only_TenantId` | Unit |
| 1 | An entity cannot be constructed with an uninitialized TenantId | `TenantDbContextGuardTests.Adding_an_entity_with_no_TenantId_assigned_throws_UntenantedEntity` | Unit |
| 2 | A caller scoped to tenant A never sees tenant B's row in a collection query | `TenantQueryFilterTests.Two_contexts_of_the_same_type_each_see_only_their_own_tenants_row` | Integration |
| 2 | A caller scoped to tenant A never fetches tenant B's row by its own id | `TenantScopedProbeIsolationTests.Tenant_A_cannot_update_or_delete_rows_of_tenant_B_by_id` (the `FindAsync` assertion) | Integration |
| 2 | The filter cannot be bypassed by a plain LINQ query written without thinking about tenancy | `TenantQueryFilterTests.A_compiled_async_query_delegate_run_against_two_contexts_returns_only_each_ones_own_tenant` | Integration |
| 3 | A caller with no tenant_id claim at all resolves to "no tenant" | `TenantResolutionTests.FromClaim_null_resolves_to_NoTenant` | Unit |
| 3 | A caller with a well-formed tenant_id claim resolves to that tenant | `TenantResolutionTests.FromClaim_a_well_formed_guid_resolves_to_that_tenant` | Unit |
| 3 | A caller with a malformed tenant_id claim resolves to "invalid", never to "no tenant" and never to a guessed tenant | `TenantResolutionTests.FromClaim_a_malformed_value_resolves_to_Invalid_never_NoTenant_and_names_no_tenant` | Unit |
| 3 | A caller with a malformed tenant_id claim never reaches a tenant-scoped query | `TenantDbContextGuardTests.A_query_under_an_Invalid_resolution_throws_before_any_SQL`, `TenantQueryFilterInvalidResolutionTests.With_an_Invalid_resolution_a_collection_query_sends_no_command_and_throws_InvalidTenant` (interceptor proves zero commands are ever sent) | Unit |
| 4 | A "no tenant" caller's collection query returns nothing | `TenantQueryFilterTests.With_a_None_resolution_a_collection_query_returns_zero_rows_against_two_seeded_tenants` | Integration |
| 4 | A "no tenant" caller's query by another tenant's row id returns nothing | `TenantQueryFilterTests.With_a_None_resolution_FindAsync_returns_null_for_a_row_seeded_under_a_tenant` | Integration |
| 5 | Saving a new row tagged with a different tenant is rejected | `TenantDbContextGuardTests.Adding_an_entity_tagged_with_a_different_tenant_throws_TenantMismatch` | Unit |
| 5 | Saving a new row with no tenant assigned is rejected | `TenantDbContextGuardTests.Adding_an_entity_with_no_TenantId_assigned_throws_UntenantedEntity` | Unit |
| 5 | Modifying an existing row so its tenant would change is rejected | `TenantDbContextGuardTests.Changing_an_attached_rows_TenantId_via_the_entry_API_throws_TenantChanged_even_with_AutoDetectChangesEnabled_off` | Unit |
| 5 | A same-tenant write succeeds normally | `TenantWriteGuardTests.A_same_tenant_write_persists_and_is_read_back_under_the_same_tenant` | Integration |
| 6 | A type not marked AllowCrossTenant cannot ignore the tenant filter | `TenancyRuleTests.CrossTenantQueryRule_fails_on_an_unattributed_IgnoreQueryFilters_call` (plus the raw-SQL, method-group, expression-tree and nested-helper bypass variants in the same class) | Unit |
| 6 (read-back hazard, T-07) | The same guarantee, reached through EF's own by-id read-back path instead of `IgnoreQueryFilters()` | Control: `TenancyRuleTests.CrossTenantQueryRule_fails_on_an_EntityEntry_read_back_bypass`. Hazard pin (not the control): `CrossTenantReadBackTests.EF_GetDatabaseValuesAsync_bypasses_the_tenant_filter_so_it_is_banned_outside_AllowCrossTenant`, `CrossTenantReadBackTests.EF_ReloadAsync_bypasses_the_tenant_filter_so_it_is_banned_outside_AllowCrossTenant` | Unit + Integration |
| 6 | A type marked AllowCrossTenant with a justification may ignore the tenant filter | `TenancyRuleTests.CrossTenantQueryRule_passes_on_a_call_wrapped_in_async_plus_lambda_credited_to_the_outer_type` | Unit |
| 6 | AllowCrossTenant without a justification is rejected | `TenancyRuleTests.AllowCrossTenantJustificationRule_fails_on_a_blank_justification` (green counterpart: `AllowCrossTenantJustificationRule_passes_on_a_real_justification`) | Unit |
| 6 | Every AllowCrossTenant usage is discoverable for #24's future audit log | `TenancyRuleTests.AllowCrossTenant_usages_are_discoverable_by_reflection_Story6Scenario4` | Unit |
| 7 (Done-when) | A deliberate violation fails the test | `TenancyRuleTests.TenantModelRule_fails_on_a_context_mapping_an_unscoped_entity`; recorded red run in `docs/ai/pipeline/22.md` G4 (removing `: ITenantScoped` from the compliant fixture → 1 failed) | Unit |
| 7 (Done-when) | Fixing the violation turns the test green | `TenancyRuleTests.TenantModelRule_passes_on_a_context_mapping_only_scoped_entities`; recorded green run in `docs/ai/pipeline/22.md` G4 (restoring `: ITenantScoped` → green) | Unit |
| 7 | A compliant entity never trips the rule | `TenancyRuleTests.TenantModelRule_passes_on_a_context_mapping_only_scoped_entities` | Unit |
| 8 | The fixture starts a working Postgres container and exposes a connection string a TenantDbContext can connect with | Indirect: every Postgres-backed Integration test in `Decisya.Infrastructure.Persistence.Tests` (e.g. `TenantWriteGuardTests.A_same_tenant_write_persists_and_is_read_back_under_the_same_tenant`) calls `PostgresFixture.CreateDatabaseAsync<TContext>` and then reads/writes through the resulting context; there is no standalone test, to avoid re-testing Testcontainers' own "does the container start" coverage | Integration (indirect) |
| 8 | The fixture is shared across the tests in one collection | Indirect: `[assembly: Xunit.AssemblyFixture<PostgresFixture>]` (`AssemblyInfo.cs`) gives the whole assembly one instance; `TenantQueryFilterTests`, `TenantQueryFilterInvalidResolutionTests`, `TenantScopedProbeIsolationTests`, `TenantWriteGuardTests` and `CrossTenantReadBackTests` all inject and use it in the same run | Integration (indirect) |
| 8 | The fixture tears down cleanly, leaving no orphaned container behind | `manual` — confirming no container survives means inspecting the Docker daemon (`docker ps -a`) after the run finishes, from outside the test process; `PostgresFixture.DisposeAsync` delegates to Testcontainers' own `DisposeAsync`/Ryuk reaper, which is Testcontainers' tested responsibility, not re-asserted here | Manual |
| 9 | Tenant A cannot read tenant B's row | `TenantScopedProbeIsolationTests.Tenant_A_cannot_read_rows_of_tenant_B` | Integration |
| 9 | Tenant A cannot update or delete tenant B's row by id | `TenantScopedProbeIsolationTests.Tenant_A_cannot_update_or_delete_rows_of_tenant_B_by_id` | Integration |
| NFR-28 | Every EF-mapped entity type in every TenantDbContext-derived model implements ITenantScoped; zero exceptions | `TenancyRuleTests.TenantModelRule_fails_on_a_context_mapping_an_unscoped_entity`, `TenancyRuleTests.TenantModelRule_passes_on_a_context_mapping_only_scoped_entities` | Unit |
| NFR-29 | Zero cross-tenant rows returned by a read or affected by a write, across the full two-tenant isolation matrix | `TenantScopedProbeIsolationTests.Tenant_A_cannot_read_rows_of_tenant_B`, `TenantScopedProbeIsolationTests.Tenant_A_cannot_update_or_delete_rows_of_tenant_B_by_id`, `TenantWriteGuardTests.A_detached_Update_carrying_tenant_Bs_id_run_as_tenant_A_raises_a_concurrency_exception_and_leaves_Bs_row_unchanged`, `TenantWriteGuardTests.A_detached_Remove_carrying_tenant_Bs_id_run_as_tenant_A_raises_a_concurrency_exception_and_leaves_Bs_row_unchanged`, `TenantQueryFilterTests.Two_contexts_of_the_same_type_each_see_only_their_own_tenants_row` | Integration |
| NFR-30 | "No tenant" always yields zero rows; "invalid" never reaches a query; both hold across 100% of resolutions | `TenantQueryFilterTests.With_a_None_resolution_a_collection_query_returns_zero_rows_against_two_seeded_tenants`, `TenantQueryFilterTests.With_a_None_resolution_FindAsync_returns_null_for_a_row_seeded_under_a_tenant`, `TenantQueryFilterTests.With_a_None_resolution_CountAsync_returns_zero_against_two_seeded_tenants`, `TenantQueryFilterTests.With_a_None_resolution_ExecuteUpdateAsync_affects_zero_rows_against_two_seeded_tenants`, `TenantQueryFilterTests.With_a_None_resolution_ExecuteDeleteAsync_affects_zero_rows_against_two_seeded_tenants`, `TenantDbContextGuardTests.Adding_an_entity_under_a_None_resolution_throws_NoTenant_before_any_SQL`, `TenantQueryFilterInvalidResolutionTests.With_an_Invalid_resolution_a_collection_query_sends_no_command_and_throws_InvalidTenant`, `TenantQueryFilterInvalidResolutionTests.With_an_Invalid_resolution_ExecuteUpdateAsync_sends_no_command_and_throws_InvalidTenant`, `TenantQueryFilterInvalidResolutionTests.With_an_Invalid_resolution_ExecuteDeleteAsync_sends_no_command_and_throws_InvalidTenant`, `TenantQueryFilterInvalidResolutionTests.With_an_Invalid_resolution_SaveChangesAsync_sends_no_command_and_throws_InvalidTenant` | Unit + Integration |

**Checks re-run at G5:**
- `dotnet build -warnaserror`: 0 warnings, 0 errors.
- Unit lane (`dotnet test --filter-not-trait "Category=Integration" --filter-not-trait "Category=AppHost"`): 761 tests, 0 failures (760 recorded at G4 + 1 new: `ITenantScopedShapeTests`).
- `dotnet test --project tests/Decisya.Infrastructure.Persistence.Tests --filter-trait "Category=Integration"`: 14 tests, 0 failures — unchanged from the G4 count.

No other gaps: every scenario above has a `Direct`, `Indirect` or `manual` mapping; the only
missing test found (Story 1, Scenario 1) was added, and it was small enough (one test method,
no new fixtures) to stay in scope for this issue rather than going to backlog #83.

<!-- gate: G5 | verdict: PASS | issue: #22 -->
