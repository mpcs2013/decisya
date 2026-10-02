# Phase 0 – Modules.Audit: append-only audit log for cross-tenant admin commands

## Issue 0.12 (#24) — Modules.Audit append-only log

Done when (issue): Audit rows cannot be updated or deleted at DB role level.

Inputs: `docs/ai/pipeline/24.md` (Marco's decisions of 2026-10-01: full tier; #24 also wires the audit record into the three Entitlements admin handlers, in the same unit of work), ADR-0012, `docs/requirements/phase-0/entitlements-module.md` (R-1, Q6), `docs/security/threat-models/entitlements-module.md` (C-1 to C-3, G4-23-01, G4-23-02). `gh issue view 24` was not read in this session, so the scope below comes from the manifest, Marco's decisions and the carry-forward. Anything the issue body might add beyond that is an open question, not an assumption.

### Scope note (role framing)

There is still no admin endpoint (#25). The stories are written "As a platform admin" (the operator whose cross-tenant command is recorded), "As the platform operator" (properties no single caller experiences), and "As a tenant user" (the tenant acted upon). Admin stories are tested by calling the application commands directly, as in #23.

**Entitlement plan:** none of Audit's operations is gated by a feature key. The audit write is platform infrastructure that cannot be switched off by a plan. The `entitlements.*` commands stay ungated for the reason given in #23.

### Scope, tight (#84)

In scope:
- A new `Modules.Audit` module (+ `Modules.Audit.Contracts`) with one append-only table in its own `audit` schema, behind its own least-privilege database role.
- A write contract in `Modules.Audit.Contracts` that Entitlements calls. Nothing else is public.
- The three Entitlements admin handlers (`StartTrial`, `GrantOverride`, `RevokeOverride`) write one audit record each, atomic with the command.
- Isolation proof for the audit aggregate (two-tenant test) and the role privilege proof.

Deferred, owned elsewhere (not in this PR):
- Any read, query, search or export operation or endpoint over audit records, including the platform-admin reader (an `[AllowCrossTenant]` handler) and any tenant-visible "activity" view: #25 or later.
- Retention, archival, purge, legal hold.
- Tamper-evident hashing or chaining, signed records, external WORM export. A DB trigger that rejects UPDATE/DELETE for the table owner is likewise deferred: the Done-when is role level, and the owner is the migrator, not the API.
- Auditing non-admin (tenant-user) actions and any module other than Entitlements. Tenancy JIT provisioning (#21) is not audited here.
- Auditing refused attempts (see Q2).
- Platform-admin role check on `ICurrentCaller` (C-2, #25). The `None`-resolution precondition from G4-23-01 stays as it is.
- Relaxing G4-23-02's reachability rules: unchanged by #24. #25 may relax them only because #24 now exists (C-1).
- Target-tenant existence check (S-5 in the #23 threat model).

### Design notes (adopted as recommendations; see Open questions)

- **One record per committed command.** A record is written for each `StartTrial`, `GrantOverride` and `RevokeOverride` that reaches its outcome `succeeded`, and commits or rolls back with the command's own row change. "If one fails both fail" is a behaviour requirement. G2 decides the mechanism (shared connection and transaction, outbox in the same transaction, or other). It must hold for the module-owned contexts that ADR-0012 creates per command, and it must not add an ambient transaction that breaks the #23 23505 race handling.
- **Record content (the minimum).** Each record holds:
  - `Id` (unique);
  - `OccurredAt` (NodaTime `Instant`, from `IClock`; never `DateTime`);
  - `ActorUserId`: the authenticated caller's stable identity-provider user id (opaque, not email or name). Stored unhashed, because attribution needs it. Logs still hash it;
  - `Action`: one value of a closed, code-defined set: `entitlements.trial.start`, `entitlements.override.grant`, `entitlements.override.revoke`;
  - `TenantId`: the target tenant of the command (see Q1);
  - `Outcome`: closed set, `succeeded` only in #24 (see Q2);
  - `FeatureKey`: the catalog feature key for override actions, empty for trial start. It is a catalog constant, not user text;
  - `TraceId`: the W3C trace id of the current activity (32 hex characters) when an activity exists, else null. This is the correlation id.
- **Never stored:** the override `Reason`, the command object, free text of any kind, email, display name, IP address, token, claim set. The reason stays in `FeatureOverride.Reason` (already `[Sensitive]`, #23 G4-23-05) and is not copied or masked into the audit record. A record is therefore PII-free by construction, and no masking logic is needed.
- **Append-only at the role.** The application database role(s) that write the audit table hold `INSERT` (and `SELECT`, if EF needs it to read generated values) and no `UPDATE`, `DELETE` or `TRUNCATE`, on the table and its sequences, and no DDL. The migrator owner is the only principal that can alter the table. This is the same least-privilege style as `decisya_tenancy` and `decisya_entitlements`, and the Done-when is proven by connecting as that role.
- **Tenant scoping (Q1 recommendation).** The audit record is an `ITenantScoped` aggregate whose `TenantId` is the target tenant. Invariant 1 then needs no exception, the ADR-0012 target-tenant scope can write it, and the #22 filter keeps tenant A's records out of tenant B's reads. The actor is a separate column, because a platform admin has no tenant.
- **Who may read (the minimum).** In #24 no application operation reads audit records: Contracts exposes a write operation only, and the module has no endpoint. The only readers are the database owner (operations, break-glass) and tests. The future platform-admin reader must be an `[AllowCrossTenant]` handler that is itself audited. A tenant-visible view of its own audit rows is a product decision for a later issue. Until then, a tenant context reads only rows whose `TenantId` is its own (the filter), which is the isolation test below.
- **No AI dependency** (invariant 2). The write is deterministic and has no `IChatClient` path.

---

## Open questions for Marco (all three answered by Marco, 2026-10-01: recommendations accepted as written)

Status: Q1 answered (Marco, 2026-10-01). Q2 answered (Marco, 2026-10-01). Q3 answered (Marco, 2026-10-01). No open question remains, and the stories below are written to these answers.

1. **Is the audit table tenant-scoped (`ITenantScoped`), and is reading it cross-tenant?** Answered (Marco, 2026-10-01): recommendation accepted.
   - Recommended: yes, `ITenantScoped`, with `TenantId` = the target tenant. The actor is a separate column. No cross-tenant read exists in #24. The future platform-admin reader is an audited `[AllowCrossTenant]` handler.
   - Rejected: a platform-level table with a `TargetTenantId` column and no filter. It needs an invariant-1 exception (an ADR), and any code with the audit context could read every tenant's records by default.
   - Affects: Story 5, and Story 6's "every entity is `ITenantScoped`".
2. **Are refused attempts audited?** Answered (Marco, 2026-10-01): recommendation accepted.
   - Recommended: no, not in #24. Audited outcome is `succeeded` only.
     - `Forbidden` (a tenant or invalid ambient) runs before any database access (G4-23-01). Auditing it would need a write for a caller who has no right to the target scope, and would turn an unauthorised caller into a database-write amplifier. It stays an operational log line and a metric.
     - Validation failures and `trial_already_used` change nothing and carry no new fact worth an immutable record.
     - The `Outcome` column is a closed set so a later issue can add `refused` or `denied` without reshaping the table.
   - Rejected: auditing everything, including `Forbidden`. Larger, and it breaks the G4-23-01 "zero database access" proof.
   - Affects: Story 2 (the refused-attempt scenarios assert that no record is written).
3. **What is the actor when there is no HTTP path yet?** Answered (Marco, 2026-10-01): recommendation accepted.
   - Recommended: the actor is the ambient `ICurrentCaller` user id, exactly as the request pipeline sets it (#21). If the ambient caller has no user id (a direct call with no caller set), the command is refused with `entitlements.actor_unknown` before validation and before any write. It never writes a placeholder such as "system" or "unknown". The order is: `Forbidden` precondition (G4-23-01), then actor check, then validation. Tests set a caller user id in their fixture.
   - Rejected: a "system" actor for non-HTTP calls. No non-HTTP caller exists (no jobs, no seeding of these commands), and a placeholder would let #25 ship an unattributed command by mistake.
   - Affects: Story 4. Existing #23 test fixtures for the three commands need a caller user id (a test-engineer change).

---

### Story 1 — Audit rows cannot be updated or deleted at database role level

As the platform operator, I want audit records to be append-only for every database role the application uses, so that no bug, injection or compromised API process can alter or erase the history of admin actions.

**Gating feature key:** none (platform infrastructure).

```gherkin
Feature: The audit table is append-only at the database role (the Done-when)

  Background:
    Given PostgresFixture provides a real Postgres 18 instance with all module migrations applied
    And an audit record exists with Id "0b6f2b1e-5c3a-4a8e-9d1f-3e7a2c4b8d10" for tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7"

  Scenario: The audit writer role can insert a record
    Given a connection as the role that writes the audit table
    When a new audit record is inserted
    Then the insert succeeds

  Scenario: The audit writer role cannot update a record
    Given a connection as the role that writes the audit table
    When "UPDATE audit.audit_records SET action = 'x'" is run against the existing record
    Then the statement fails with SQLSTATE 42501
    And the record is unchanged afterwards

  Scenario: The audit writer role cannot delete or truncate
    Given a connection as the role that writes the audit table
    When "DELETE FROM audit.audit_records" is run
    Then the statement fails with SQLSTATE 42501
    When "TRUNCATE audit.audit_records" is run
    Then the statement fails with SQLSTATE 42501
    And the record still exists

  Scenario: The audit writer role has no DDL right and cannot lift its own limits
    Given a connection as the role that writes the audit table
    When each of "ALTER TABLE audit.audit_records ...", "DROP TABLE audit.audit_records", "CREATE TABLE audit.t (id int)", "GRANT UPDATE ON audit.audit_records TO PUBLIC" and "SET ROLE postgres" is run
    Then each statement fails with SQLSTATE 42501
    And the role has no elevated attribute (superuser, createdb, createrole, replication, bypassrls) and no role membership

  Scenario: No other module role has any privilege on the audit schema
    Given connections as "decisya_tenancy" and as "decisya_entitlements"
    When "SELECT" and "INSERT" on the audit table are run as "decisya_tenancy"
    Then each fails with SQLSTATE 42501
    And "decisya_entitlements" holds, on the audit table, no privilege beyond those the audit writer path needs (no UPDATE, DELETE or TRUNCATE) 

  Scenario: The audit role has no privilege on other modules' schemas
    Given a connection as the role that writes the audit table
    When "SELECT" and "INSERT" are run on "tenancy.tenants" and on "entitlements.trial_grants"
    Then each fails with SQLSTATE 42501

  Scenario: Re-running the migrator does not widen the role
    When the migrator runs twice against one database
    Then the privilege scenarios above still hold
```

### Story 2 — Each cross-tenant Entitlements admin command writes one audit record, atomically

As a platform admin, I want my start-trial, grant-override and revoke-override actions recorded in the same unit of work as the change, so that every cross-tenant change is attributable and no change can exist without its record.

**Gating feature key:** none (admin command; HTTP exposure in #25).

```gherkin
Feature: StartTrial, GrantOverride and RevokeOverride are audited in the same unit of work (C-1)

  Background:
    Given a fake IClock fixed at "2026-10-01T09:00:00Z"
    And the ambient caller resolution is "no tenant" (platform admin) with user id "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59"
    And tenant A is "7c9e6679-7425-40de-944b-e07fc1f90ae7" on Free with no trial and no override
    And an activity with trace id "4bf92f3577b34da6a3ce929d0e0e4736" is current

  Scenario: Starting a trial writes one audit record
    When the StartTrial command is executed for tenant A
    Then a TrialGrant exists for tenant A
    And exactly one audit record exists for tenant A
    And that record has Action "entitlements.trial.start", Outcome "succeeded", ActorUserId "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59", OccurredAt "2026-10-01T09:00:00Z", TraceId "4bf92f3577b34da6a3ce929d0e0e4736" and an empty FeatureKey

  Scenario: Granting an override writes one audit record naming the feature
    When GrantOverride is executed for tenant A, feature "forecasting.scenarios", reason "Design-partner pilot", no expiry
    Then a FeatureOverride exists for tenant A and "forecasting.scenarios"
    And exactly one audit record exists for tenant A with Action "entitlements.override.grant", FeatureKey "forecasting.scenarios" and Outcome "succeeded"

  Scenario: Replacing an existing override writes a further record, one per command
    Given an override exists for tenant A and "forecasting.scenarios"
    When GrantOverride is executed again for the same tenant and feature
    Then exactly one FeatureOverride row exists for tenant A and "forecasting.scenarios"
    And exactly two audit records with Action "entitlements.override.grant" exist for tenant A

  Scenario: Revoking an override writes one audit record
    Given an override exists for tenant A and "forecasting.scenarios"
    When RevokeOverride is executed for tenant A and "forecasting.scenarios"
    Then exactly one audit record with Action "entitlements.override.revoke" and FeatureKey "forecasting.scenarios" exists for tenant A

  Scenario: Revoking a feature that has no override still records the admin action
    When RevokeOverride is executed for tenant A and "forecasting.scenarios", which has no override
    Then the command succeeds and changes no entitlement row
    And exactly one audit record with Action "entitlements.override.revoke" exists for tenant A

  Scenario: If the audit write fails, the command's change is rolled back
    Given the audit insert is made to fail (fault injection at the persistence layer)
    When GrantOverride is executed for tenant A and "forecasting.scenarios"
    Then the command fails
    And no FeatureOverride row exists for tenant A
    And no audit record exists for tenant A
    And the same holds for StartTrial (no TrialGrant) and for RevokeOverride (an existing override is still present)

  Scenario: If the command's change fails, no audit record remains
    Given the entitlement write is made to fail (fault injection at the persistence layer)
    When any of the three commands is executed for tenant A
    Then the command fails
    And no audit record exists for tenant A

  Scenario: A trial race leaves exactly one record, for the winner only
    When 16 StartTrial commands run in parallel for tenant A
    Then exactly one succeeds and exactly one TrialGrant exists
    And exactly one audit record with Action "entitlements.trial.start" exists for tenant A

  Scenario: The GrantOverride race recovery path still writes exactly one record per successful command
    Given a concurrent grant wins the unique index on "ux_feature_overrides_tenant_feature"
    When GrantOverride takes its 23505 re-read path and succeeds
    Then exactly one FeatureOverride row exists
    And exactly one audit record is written for that command, with no orphan from the failed first attempt

  Scenario: Refused attempts that change nothing write no audit record
    When StartTrial is executed twice for tenant A, so the second is refused with "trial already used"
    Then exactly one audit record with Action "entitlements.trial.start" exists for tenant A
    When GrantOverride is executed with an unknown feature "nosuch.feature", an empty reason, or an expiry earlier than the clock
    Then each fails validation and no audit record is written for it

  Scenario: A non-admin ambient is refused with no database access and no audit record
    Given the ambient caller resolution is "tenant" or "invalid"
    When any of the three commands is executed with an invalid feature and an empty reason
    Then the result is "entitlements.forbidden"
    And no database command is issued, so no audit record and no entitlement row is written
```

### Story 3 — An audit record holds only what attribution needs, and nothing sensitive

As the platform operator, I want audit records to carry the minimum identifying facts and no personal or free-text data, so that the immutable log can never become a store of data that must later be erased.

```gherkin
Feature: Audit record content is minimal and PII-free

  Background:
    Given a fake IClock fixed at "2026-10-01T09:00:00Z"
    And the ambient caller is a platform admin with user id "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59"

  Scenario: The override reason never reaches the audit record
    When GrantOverride is executed for tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7", feature "forecasting.scenarios", with the reason "MARKER-9f3a Contact: anna.meier@example.com, IBAN DE89370400440532013000"
    Then no column of the audit record contains "MARKER-9f3a", the e-mail address or the IBAN
    And the audit record has no column for the reason or for the command payload

  Scenario: The reason does not appear in logs, error text or telemetry of the audit write
    When the same command succeeds, and again when its audit write is made to fail
    Then captured logs, exception messages, metric tags and activity tags contain no "MARKER-9f3a"
    And the failure the caller sees is a fixed generic message with no row, column or reason values

  Scenario: The record's action and outcome come only from closed sets
    When the audit record model is inspected
    Then Action allows only "entitlements.trial.start", "entitlements.override.grant" and "entitlements.override.revoke"
    And Outcome allows only "succeeded"
    And an attempt to write any other value fails before reaching the database

  Scenario: Time is a NodaTime Instant
    When the audit aggregate and its write contract are inspected
    Then every time-valued member is an Instant, with none of DateTime or DateTimeOffset
    And the module never reads the system clock directly

  Scenario: The trace id is the correlation id, and a missing activity does not fail the command
    Given no activity is current
    When StartTrial is executed for tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    Then the command succeeds and its audit record has a null TraceId
```

### Story 4 — The audit record always names an identified actor

As a platform admin (and as the operator who reviews the log), I want a command that has no identified caller to be refused rather than recorded as anonymous, so that every record names a real actor.

```gherkin
Feature: The actor comes from the ambient caller and is never a placeholder

  Background:
    Given the ambient caller resolution is "no tenant" (platform admin)

  Scenario: The actor is the ambient caller's user id
    Given the ambient caller has user id "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59"
    When any of the three commands succeeds for tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    Then the audit record's ActorUserId is "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59"

  Scenario: A command with no identified caller is refused
    Given the ambient caller has no user id
    When any of the three commands is executed for tenant "7c9e6679-7425-40de-944b-e07fc1f90ae7" with valid input
    Then the result is "entitlements.actor_unknown"
    And no entitlement row and no audit record is written

  Scenario: The actor check runs after the Forbidden precondition and before validation
    Given the ambient caller has no user id
    When a command with an invalid feature and an empty reason is executed under a "tenant" ambient
    Then the result is "entitlements.forbidden"
    When the same command is executed under a "no tenant" ambient
    Then the result is "entitlements.actor_unknown", not a validation error

  Scenario: The actor id is the identity-provider subject, not a name or an e-mail
    When an audit record is written
    Then ActorUserId parses as the opaque user id the caller context provides
    And no display name, e-mail or claim set is stored with the record
```

### Story 5 — Audit records never cross tenants

As a tenant user, I want the audit records about my tenant unreachable from another tenant's context, so that one tenant never learns what an admin did to another.

**Gating feature key:** none.

```gherkin
Feature: A two-tenant isolation test proves read and write isolation for the audit record

  Background:
    Given PostgresFixture provides a real Postgres 18 instance
    And an audit record exists for tenant A ("7c9e6679-7425-40de-944b-e07fc1f90ae7")
    And a different audit record exists for tenant B ("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b")

  Scenario: Tenant A cannot read tenant B's audit records
    Given the audit context's ambient tenant is tenant A
    When tenant B's records are queried, by a collection query and by their own id
    Then none of tenant B's records are returned

  Scenario: A "no tenant" or "invalid" context reads no audit record
    Given the audit context's ambient resolution is "no tenant"
    When the audit records are queried
    Then zero rows are returned
    And under "invalid" no query reaches the database

  Scenario: Tenant A cannot write a record that belongs to tenant B
    Given the audit context's ambient tenant is tenant A
    When a record with TenantId of tenant B is saved
    Then the save is refused by the tenant guard and no row is written

  Scenario: A command for tenant A adds records only for tenant A
    Given tenant B has two audit records
    When any of the three commands succeeds for tenant A
    Then tenant B still has exactly two audit records, unchanged

  Scenario: No application operation reads audit records in this issue
    When the public types of Decisya.Modules.Audit.Contracts and Decisya.Modules.Audit are inspected
    Then the only public operation is the write contract
    And there is no public read, update or delete method and no endpoint in the module
```

### Story 6 — Audit follows the platform module rules, and cross-tenant handlers must call it

As the platform operator, I want the Audit module to follow the standard module shape and every Entitlements cross-tenant handler to depend on it, so that a future handler cannot silently skip the audit.

```gherkin
Feature: Module shape, least privilege and the C-1 guard

  Scenario: The write contract is in the Contracts assembly and Entitlements depends only on it
    When the public types of Decisya.Modules.Audit.Contracts are inspected
    Then the audit write contract is declared there
    And Decisya.Modules.Entitlements references Decisya.Modules.Audit.Contracts and not Decisya.Modules.Audit
    And Decisya.Modules.Audit.Contracts references no other module's internals

  Scenario: The module uses the schema-per-module and least-privilege conventions
    When the migration for the Audit module is applied
    Then its table is in the "audit" schema only
    And every entity type of the Audit DbContext implements ITenantScoped
    And the module's database role is provisioned by the migrator with a SCRAM verifier from a generated, secret parameter, never from source

  Scenario: Every Entitlements cross-tenant handler writes the audit record
    When the types carrying [AllowCrossTenant] in Decisya.Modules.Entitlements are inspected
    Then there are exactly three, StartTrial, GrantOverride and RevokeOverride handlers
    And each references the audit write contract

  Scenario: The reachability rules of #23 still hold
    When the G4-23-02 architecture tests run
    Then they pass unchanged: the commands and handlers are internal, InternalsVisibleTo is exactly the module test assembly, no ASP.NET Core and no Wolverine reference, and no Endpoints namespace
    And the AllowCrossTenant justification of each handler states that the audit record is written, replacing the "#24 audit prerequisite" wording
```

---

## Non-functional requirements

Two rows are added to `docs/requirements/nfr.md` (ISO/IEC 25010 categories): NFR-36, NFR-37.

| Id | Category | Target (summary) |
| --- | --- | --- |
| NFR-36 | Security (integrity, non-repudiation) | 100% of `UPDATE`, `DELETE` and `TRUNCATE` statements on the audit table, run as every database role the application uses, fail with SQLSTATE 42501, and no such role holds DDL rights |
| NFR-37 | Reliability | Across the fault-injection matrix (audit write fails, command write fails, 16-way trial race, 23505 re-read path), zero commands are committed without an audit record and zero audit records exist without a committed command |

## Risks carried to G2 and G3

- **R-1 Atomicity across two module contexts.** ADR-0012 gives each command its own short-lived Entitlements context, and Audit has its own schema, role and context. G2 must pick a mechanism that makes the change and the record one commit without an ambient transaction that breaks the #23 23505 handling, and without giving the Entitlements role more than it needs on the audit table (or sharing one role across schemas, which G3 should weigh against the cross-schema proof).
- **R-2 Stored actor id.** `ActorUserId` is an opaque id but is personal data under most privacy law. Retention and erasure are deferred, and the immutable table makes that deferral a real decision later. The record holds nothing else about the person.
- **R-3 Table owner can alter.** The migrator owner can alter or truncate the table. This is accepted for #24 (Done-when is role level). A trigger and tamper-evident hashing are deferrals.
- **R-4 Refusals unaudited.** If Q2's recommendation stands, `Forbidden` and validation refusals leave only operational logs.
- **R-5 No read path.** Records cannot be read through the application until a later issue. This is intended, and means the log is not yet useful to an investigator without database access.

<!-- gate: G1 | verdict: PASS | issue: #24 -->

## Traceability

Gate G5, issue #24. Lanes: Unit, Architecture, Integration (Testcontainers Postgres 18; the Entitlements end-to-end tests connect as `decisya_entitlements` after `MigrationRunner.RunAsync`, and every audit assertion reads as the owner).

Abbreviations (all paths under `tests/`):
- **Mig** = `Decisya.Infrastructure.Migrator.Tests` (`AuditGrantsIntegrationTests` = *Grants*, `EntitlementsRoleIntegrationTests` = *Role*).
- **Ent** = `Modules/Decisya.Modules.Entitlements.Tests`: *EAudit* = `EntitlementsAuditTests`, *EAtom* = `EntitlementsAuditAtomicityTests`, *EActor* = `EntitlementsAuditActorTests`, *ERace* = `GrantOverrideRaceRecoveryTests`, *EBound* = `EntitlementsModuleBoundaryTests`, *EForb* = `EntitlementsForbiddenTests`.
- **Aud** = `Modules/Decisya.Modules.Audit.Tests`: *AWriter* = `AuditWriterTests`, *ATel* = `AuditWriterTelemetryTests`, *AIso* = `AuditIsolationTests`, *ABound* = `AuditModuleBoundaryTests`, *AModel* = `AuditModelAndRegistrationTests`.
- **Arch** = `Decisya.ArchitectureTests`: *ARules* = `AuditRuleTests`, *MBound* = `ModuleBoundaryTests`, *ABypass* = `AuditBypassUsageTests`.

### Reconciliation of G1 by G2 and ADR-0013 (applied to the rows below)

| G1 wording | Adjusted criterion, as tested |
| --- | --- |
| "the role that writes the audit table", "audit role" (Story 1) | `decisya_entitlements`, the only writer. No `decisya_audit` role exists: `Grants.The_audit_migration_creates_exactly_audit_records_with_its_constraints_and_index_and_its_history_table_in_the_audit_schema`, `Grants.The_audit_table_ACL_is_exactly_the_owner_plus_INSERT_for_decisya_entitlements_and_the_same_after_a_second_RunAsync`. The migrator's keys and the API's two connection strings are unchanged (existing `AppHostConfigurationTests`, `MigrationRunnerValidationTests`). |
| `GRANT UPDATE … TO PUBLIC` "fails with 42501" | 42501 **or** WARNING 01007 with no exception, then the ACL is asserted unchanged: `Grants.GRANT_UPDATE_TO_PUBLIC_as_decisya_entitlements_is_42501_or_a_notice_without_exception_and_the_ACL_is_unchanged`. `decisya_tenancy` gets 42501 in the same test. |
| "an empty FeatureKey" for a trial | stored as `NULL`: `EAudit.Starting_a_trial_writes_one_audit_record` asserts `FeatureKey` is null; check constraint `ck_audit_records_feature_key` in `AModel.The_five_check_constraints_and_the_tenant_leading_index_the_design_names_exist_in_the_model`. |
| "the module's database role is provisioned with a SCRAM verifier" (Story 6) | Not applicable: there is no Audit login role (ADR-0013 point 3). Replaced by the "no `decisya_audit` role" assertions above. |
| "The audit role has no privilege on other modules' schemas" | The writer role's other privileges are #23's assertions (`Role.Decisya_entitlements_role_cannot_read_or_write_the_tenancy_schema`, extended for #24). |

### Story 1 - append-only at the database role

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| The audit writer role can insert a record | `Mig Grants.Decisya_entitlements_can_INSERT_but_UPDATE_DELETE_TRUNCATE_and_SELECT_fail_with_42501_and_the_seeded_row_is_unchanged` (INSERT with no RETURNING, no SELECT); `Ent EAudit.Starting_a_trial_writes_one_audit_record` (EF insert as the real role) | Integration |
| The audit writer role cannot update a record | same `Grants` test (UPDATE fails 42501, rows byte-identical by md5, after both runs); `Grants.Pre_seeded_UPDATE_DELETE_TRUNCATE_column_PUBLIC_and_tenancy_grants_are_narrowed_by_RunAsync` | Integration |
| The audit writer role cannot delete or truncate | same two tests (DELETE, TRUNCATE, TRUNCATE with another schema's table, all 42501; the rows still exist) | Integration |
| The audit writer role has no DDL right and cannot lift its own limits | same `Grants` test (ALTER, DROP, CREATE TABLE, CREATE TRIGGER, `SET ROLE postgres`, REINDEX, CLUSTER, LOCK ACCESS EXCLUSIVE); `Grants.GRANT_UPDATE_TO_PUBLIC_as_decisya_entitlements_…` (reconciled); `Grants.Neither_module_role_holds_MAINTAIN_or_membership_of_pg_maintain_or_any_other_role`; `Mig Role.Decisya_entitlements_role_carries_no_elevated_attribute_and_no_role_membership` | Integration |
| No other module role has any privilege on the audit schema | `Grants.Decisya_tenancy_has_no_privilege_on_the_audit_schema_and_every_statement_fails_with_42501`; `Grants.PUBLIC_holds_nothing_on_the_audit_schema_its_table_its_columns_or_its_functions`; `Role.Decisya_tenancy_role_cannot_read_or_write_the_entitlements_schema` (extended with audit) | Integration |
| The audit role has no privilege on other modules' schemas (reconciled: the writer role is `decisya_entitlements`) | `Role.Decisya_entitlements_role_cannot_read_or_write_the_tenancy_schema` (extended: USAGE on audit true, CREATE false, SELECT/UPDATE/DELETE on audit 42501) | Integration |
| Re-running the migrator does not widen the role | `Grants.The_audit_table_ACL_is_exactly_the_owner_plus_INSERT_for_decisya_entitlements_and_the_same_after_a_second_RunAsync`; `Grants.Pre_seeded_…_are_narrowed_by_RunAsync`; `Role.RunAsync_is_idempotent_with_both_roles_and_the_privilege_tests_still_hold_afterwards` (extended) | Integration |

### Story 2 - one record per succeeded command, atomically

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| Starting a trial writes one audit record | `Ent EAudit.Starting_a_trial_writes_one_audit_record` (action, outcome, actor, `OccurredAt` = fake clock, trace id, `FeatureKey` NULL, target tenant) | Integration |
| Granting an override writes one audit record naming the feature | `Ent EAudit.Granting_an_override_writes_one_audit_record_naming_the_feature` | Integration |
| Replacing an existing override writes a further record, one per command | `Ent EAudit.Replacing_an_existing_override_writes_a_further_record_one_per_command` | Integration |
| Revoking an override writes one audit record | `Ent EAudit.Revoking_an_override_writes_one_audit_record` | Integration |
| Revoking a feature that has no override still records the admin action | `Ent EAudit.Revoking_a_feature_that_has_no_override_still_records_the_admin_action` | Integration |
| If the audit write fails, the command's change is rolled back | `Ent EAtom.If_the_audit_write_fails_or_anything_fails_after_it_or_the_commit_fails_the_commands_change_is_rolled_back` (3 commands x `append_throws_after_real_append`, `insert_privilege_revoked` = real 42501, plus the save and commit faults) | Integration |
| If the command's change fails, no audit record remains | same test, `save_fails` and `commit_fails` cells (a real append rolled back with the failed commit); control `Ent EAtom.A_healthy_command_commits_its_change_and_its_record_together` | Integration |
| A trial race leaves exactly one record, for the winner only | `Ent EAtom.A_trial_race_leaves_exactly_one_record_for_the_winner_only` (16-way, as the real role) | Integration |
| The GrantOverride race recovery path still writes exactly one record per successful command | `Ent ERace.When_the_23505_re_read_finds_the_winner_the_handler_replaces_it_and_leaves_exactly_one_row_and_exactly_one_audit_record`; `…_finds_no_row_because_the_winner_was_revoked_…`; `…A_second_failure_in_the_retry_propagates_instead_of_looping_and_leaves_no_audit_record` (each as owner and as role); `Ent EAtom.Concurrent_grants_for_one_feature_leave_one_override_and_exactly_one_record_per_successful_command`; `Ent EAtom.Concurrent_revokes_leave_one_record_per_command`; `Ent EAtom.A_revoke_that_loses_the_delete_to_a_concurrent_revoke_still_writes_its_own_one_record` | Integration |
| Refused attempts that change nothing write no audit record | `Ent EAudit.Refused_attempts_that_change_nothing_write_no_audit_record` (`trial_already_used`, unknown feature, empty reason, past expiry, default keys) | Integration |
| A non-admin ambient is refused with no database access and no audit record | `Ent EAudit.A_non_admin_ambient_is_refused_with_no_database_access_and_no_audit_record` (command interceptor: 0 commands, 0 appends); `Ent EForb.A_non_admin_ambient_gets_forbidden_from_every_command_before_validation_and_with_no_database_access` (unreachable host) | Integration, Unit |

### Story 3 - minimal, PII-free content

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| The override reason never reaches the audit record | `Ent EAudit.The_override_reason_never_reaches_the_audit_record` (whole-row JSON, no marker, e-mail or IBAN; columns are exactly the eight expected) | Integration |
| The reason does not appear in logs, error text or telemetry of the audit write | `Ent EAudit.The_reason_does_not_appear_in_logs_error_text_or_telemetry_of_the_audit_write` (success, append throws, 42501, commit fails, save fails); `Aud AWriter.A_malformed_entry_throws_ArgumentException_before_any_SQL_and_the_message_names_no_entry_value`; existing `Ent EntitlementsReasonConfidentialityTests` | Integration, Unit |
| The record's action and outcome come only from closed sets | `Aud AModel.The_action_and_outcome_check_constraints_allow_only_the_closed_sets`; `Aud AModel.An_undefined_action_fails_in_the_converter_before_it_can_reach_the_database`; `Aud AWriter.A_malformed_entry_…` (undefined action); `Aud ABound.AuditAction_has_exactly_the_three_values_1_to_3` | Unit |
| Time is a NodaTime Instant | `Aud ABound.No_Domain_or_Application_type_has_a_DateTime_DateTimeOffset_or_TimeZoneInfo_member_or_parameter`; `Aud ABound.AuditRecord_OccurredAt_is_an_Instant`; `Aud ABound.The_Audit_module_never_reads_the_system_clock_directly`; `Aud AWriter.Append_writes_one_succeeded_record_with_the_actor_the_clock_time_…` | Unit, Architecture |
| The trace id is the correlation id, and a missing activity does not fail the command | `Ent EAudit.The_trace_id_is_the_correlation_id`; `Ent EntitlementsAuditNoActivityTests.The_trace_id_is_null_and_a_missing_activity_does_not_fail_the_command`; `Aud ATel.A_missing_activity_does_not_fail_the_write_and_the_trace_id_is_null` (both in a non-parallel collection) | Integration |

### Story 4 - the actor

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| The actor is the ambient caller's user id | `Ent EAudit.The_actor_is_the_ambient_callers_user_id` (3 commands); `Aud AWriter.Append_writes_one_succeeded_record_…` | Integration |
| A command with no identified caller is refused | `Ent EActor.A_command_with_no_identified_caller_is_refused_with_actor_unknown_and_no_database_access` (null, empty, whitespace; unreachable host); `Ent EActor.A_command_with_no_identified_caller_writes_no_entitlement_row_and_no_audit_record`; `Aud AWriter.The_writer_called_directly_with_no_valid_caller_throws_before_any_SQL_and_never_writes_a_placeholder_actor` | Unit, Integration |
| The actor check runs after the Forbidden precondition and before validation | `Ent EActor.The_actor_check_runs_before_validation_and_after_the_Forbidden_precondition`; `Ent EActor.No_refusal_issues_a_database_command_and_no_audit_append_but_a_valid_command_does` (command interceptor) | Unit, Integration |
| The actor id is the identity-provider subject, not a name or an e-mail | `Ent EAudit.The_override_reason_never_reaches_the_audit_record` (column list: no name, e-mail or claim column); `Aud ABound.AuditEntry_is_exactly_TenantId_Action_and_FeatureKey_with_no_actor_time_trace_or_outcome_member`; `Aud ABound.The_module_source_holds_no_placeholder_actor_literal` | Integration, Architecture |

### Story 5 - isolation (isolation-test skill)

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| Tenant A cannot read tenant B's audit records | `Aud AIso.Tenant_A_cannot_read_tenant_Bs_audit_records` (collection and by id); `Aud AIso.Tenant_A_cannot_update_or_delete_tenant_Bs_audit_records_by_id` | Integration |
| A "no tenant" or "invalid" context reads no audit record | `Aud AIso.A_no_tenant_context_reads_zero_audit_records`; `Aud AIso.An_invalid_context_throws_before_a_query_reaches_the_database` | Integration |
| Tenant A cannot write a record that belongs to tenant B | `Aud AIso.Tenant_A_cannot_write_a_record_that_belongs_to_tenant_B`; `Aud AWriter.The_writer_refuses_an_Invalid_ambient_and_a_Tenant_ambient_for_another_tenant_before_any_SQL` | Integration, Unit |
| A command for tenant A adds records only for tenant A | `Ent EAudit.A_command_for_tenant_A_adds_records_only_for_tenant_A` | Integration |
| No application operation reads audit records in this issue | `Aud ABound.Audit_Contracts_exports_exactly_IAuditWriter_AuditEntry_and_AuditAction`; `…IAuditWriter_has_exactly_one_method_AppendAsync_…`; `…Decisya_Modules_Audit_exports_exactly_AuditModule_AuditDbContext_and_AuditDbContextOptions`; `…No_exported_member_signature_mentions_AuditRecord_DbSet_or_IQueryable`; `…AuditDbContext_declares_no_public_DbSet_and_its_only_set_is_internal`; `…AuditRecord_is_internal_with_get_only_properties_and_no_instance_method_besides_the_getters`; `…The_Audit_module_declares_no_Endpoints_namespace_and_no_Endpoints_folder` | Architecture |

### Story 6 - module rules and the C-1 guard

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| The write contract is in the Contracts assembly and Entitlements depends only on it | `Ent EBound.Decisya_Modules_Entitlements_references_Audit_Contracts_and_not_the_Audit_implementation`; `Ent EBound.The_Entitlements_module_references_no_other_modules_implementation`; `Aud ABound.Audit_Contracts_exports_exactly_…`; `Arch MBound.The_architecture_scope_includes_the_Audit_module_and_its_Contracts` (the Contracts boundary rules in `ContractsBoundaryTests` then cover Audit.Contracts) | Architecture |
| The module uses the schema-per-module and least-privilege conventions (reconciled: no audit role) | `Mig Grants.The_audit_migration_creates_exactly_audit_records_with_its_constraints_and_index_and_its_history_table_in_the_audit_schema`; `Aud AModel.Every_AuditDbContext_entity_type_maps_to_schema_audit_and_is_ITenantScoped`; `Aud AIso.The_module_persists_only_through_ITenantScoped_aggregates`; `Aud AModel.The_migration_snapshot_matches_the_model_with_no_pending_changes`; `Aud AModel.AddAuditModule_registers_IAuditWriter_to_AuditWriter_scoped_and_registers_no_DbContext_or_options`; `Aud AModel.AddAuditModule_takes_no_connection_string`; existing `Arch MBound` TenantModelRule and TenantIdImmutabilityRule passes over the scope | Unit, Integration, Architecture |
| Every Entitlements cross-tenant handler writes the audit record | `Arch MBound.CrossTenantAuditRule_passes_over_the_current_architecture_scope`; red/green fixtures `Arch ARules.CrossTenantAuditRule_fails_on_an_AllowCrossTenant_type_that_never_appends`, `…_passes_on_an_AllowCrossTenant_type_that_appends_inside_an_async_method`; `Ent EBound.The_AllowCrossTenant_types_are_exactly_the_three_admin_handlers_and_none_is_public`; `Ent EBound.Each_AllowCrossTenant_justification_is_not_blank_and_names_ADR_0012_ADR_0013_and_IAuditWriter`; `Ent EBound.The_handlers_take_the_ambient_tenant_the_options_the_caller_and_the_audit_writer_and_never_the_DI_context`; behaviour per command in `Ent EAudit` and `Ent EAtom` | Architecture, Integration |
| The reachability rules of #23 still hold | unchanged existing `Ent EBound` tests: `InternalsVisibleTo_is_exactly_the_module_test_assembly`, `The_Entitlements_module_references_no_ASP_NET_Core_and_no_Wolverine_assembly`, `The_Entitlements_module_declares_no_Endpoints_namespace`, `The_commands_handlers_and_TargetTenant_are_internal_and_not_in_Contracts`, `No_public_type_in_the_module_or_its_Contracts_has_a_HandleAsync_method_…`; the justification wording is replaced as G1 requires (test above) | Architecture |

### Non-functional requirements

| Id | Test(s) | Lane |
| --- | --- | --- |
| NFR-36 (UPDATE, DELETE, TRUNCATE fail 42501 for every application role; no DDL) | `Mig Grants.Decisya_entitlements_can_INSERT_but_UPDATE_DELETE_TRUNCATE_…` (writer, after both runs); `Mig Grants.Decisya_tenancy_has_no_privilege_on_the_audit_schema_…`; `Mig Grants.The_audit_table_ACL_is_exactly_…`; `Mig Grants.Pre_seeded_…_narrowed_by_RunAsync` | Integration |
| NFR-37 (zero commands without a record, zero records without a command) | `Ent EAtom.If_the_audit_write_fails_or_anything_fails_after_it_or_the_commit_fails_the_commands_change_is_rolled_back` (12 cells); `Ent EAtom.A_trial_race_…`; `Ent EAtom.Concurrent_grants_…`; `Ent EAtom.Concurrent_revokes_…`; `Ent ERace.*` | Integration |

### G3 MUSTs and SHOULD

| Id | Test(s) |
| --- | --- |
| G4-24-01 append-only, re-run cannot widen | Story 1 and NFR-36 rows above. Exact ACL via `aclexplode`, null column ACLs, exact schema ACL, `has_table_privilege` matrix (8 privileges incl. MAINTAIN), `pg_maintain`, PUBLIC, no sequence or function, no `decisya_audit`; pre-seeded UPDATE/DELETE narrowed; all repeated after a second `RunAsync` (`Mig Grants`, 8 tests) |
| G4-24-02 atomicity and the #23 race paths | Story 2 rows and NFR-37: fault matrix (decorator throws after a real append, real 42501, save interceptor, `DbTransactionInterceptor` on commit), 16-way trial race, GrantOverride 23505 re-reads, concurrent revokes, `trial_already_used` and validation write no record; also `Ent EAtom.The_audit_append_runs_on_the_commands_own_connection_and_transaction`; `Arch ARules.ExplicitTransactionRule_*` and `Arch MBound.ExplicitTransactionRule_passes_over_the_current_architecture_scope` |
| G4-24-03 the transaction is not a raw-SQL channel | `Arch ARules.RawAdoNetRule_fails_on_raw_ADO_NET` (theory over the violating fixtures), `…RawAdoNetRule_passes_a_type_that_only_reads_the_connection_and_enlists_through_EF`, `Arch MBound.RawAdoNetRule_passes_over_the_current_architecture_scope`; `Arch ABypass.The_three_Entitlements_handlers_and_AuditWriter_reference_only_TenantResolution_For_from_the_bypass_list` and `…The_scan_sees_TenantResolution_For_in_each_of_the_four_types_so_the_rule_is_not_vacuous` (added in G5); `Arch ARules.ExplicitTransactionRule_never_lets_a_listed_type_turn_off_savepoints_or_automatic_transactions`; `Aud AWriter.Append_runs_in_the_callers_transaction_it_never_commits_or_rolls_back_and_leaves_the_connection_open` |
| G4-24-04 validated actor, no database before refusal | Story 4 rows; `Aud ABound.AuditEntry_is_exactly_TenantId_Action_and_FeatureKey_…` (shape pinned); `Aud AWriter.The_writer_refuses_an_Invalid_ambient_…`, `The_writer_called_directly_with_no_valid_caller_…` (stub transaction over an unopened connection: refusal precedes any SQL); `Ent EActor.No_refusal_issues_a_database_command_…` |
| G4-24-05 no sensitive data | `Aud ABound.AuditRecord_ActorUserId_carries_Sensitive`; `Ent EAudit.The_reason_does_not_appear_in_logs_error_text_or_telemetry_of_the_audit_write` (marker `MARKER-9f3a` with e-mail and IBAN, 5 modes incl. every failed-append case); `Ent EAudit.The_override_reason_never_reaches_the_audit_record`; `Aud ATel.The_writers_one_activity_is_Audit_Append_tagged_only_with_the_action_code`; `Aud ABound.The_AuditWriter_takes_only_the_ambient_tenant_the_caller_and_the_clock_so_it_has_no_logger`; `Aud AModel.The_five_check_constraints_…`; existing `Arch NoSensitiveEfSwitchesTests` |
| S-1 exempt the writer type, not the Audit assembly | `Arch ARules.CrossTenantAuditRule_fails_on_an_attributed_type_in_an_Audit_namespace_that_does_not_append`; `Arch ARules.CrossTenantAuditRule_does_not_exempt_a_type_that_only_borrows_the_AuditWriter_full_name` |

### Results and notes

- This G5 session (no container runtime available, Integration lanes not re-run): `tests/Decisya.ArchitectureTests` 67 passed, 0 failed, including the 2 new `AuditBypassUsageTests`. Earlier G5 session with containers up: unit lane 970/970; Integration Entitlements 87/87 (3 consecutive runs), Audit 15/15, Migrator 25/25; race and atomicity classes 26/26 on each of 3 runs. The orchestrator's G4 run (manifest) reports the same Integration counts.
- Two deviations from G2, neither a product defect: (1) the scaffolded EF migration class `AddAuditRecords` is public, so the "exports exactly three types" test excludes `Migration` subclasses; (2) Npgsql's `NpgsqlTransaction.Connection` throws `InvalidOperationException("Transaction is already completed")` for a completed transaction instead of returning null, so the writer's own "transaction has already completed" message is proved with a stub transaction and the real-Npgsql case is proved as "refused, nothing written".
- No criterion is `manual`. Retention, erasure, a hash chain, an owner trigger, auditing refusals and a read path are deferred by G1 and G2 to #83 and are not criteria of #24.

<!-- gate: G5 | verdict: PASS | issue: #24 -->

