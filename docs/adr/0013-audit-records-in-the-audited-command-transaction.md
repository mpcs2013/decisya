# 0013. Audit records are appended in the audited command's own transaction

- Status: Accepted (2026-10-01); amendment 1 Accepted (#341, Marco 2026-11-22)
- Date: 2026-10-01
- Deciders: Marco
- Tags: security, data, module
- Refines: [ADR-0005](0005-modular-monolith-wolverine.md) (one DB role per module schema), [ADR-0012](0012-cross-tenant-admin-commands-target-tenant-scope.md) (the admin handler's unit of work)

## Context and problem statement

Platform invariant 1 allows cross-tenant work only in an `[AllowCrossTenant]`, **audited** admin handler. #23 shipped three such handlers in Entitlements (`StartTrial`, `GrantOverride`, `RevokeOverride`) and left them unaudited behind a no-HTTP fence (#23 G3 C-1). #24 adds `Modules.Audit` and must make the audit record commit **if and only if** the command commits (G1 R-1, NFR-37). The Done-when is that no application database role can update or delete an audit row.

The forces:
- ADR-0012 gives each admin command its own short-lived module context under that module's role (`decisya_entitlements`). It cannot join a request-wide unit of work.
- ADR-0005 gives each module its own schema and its own DB role. A role writing into another module's schema has no precedent, except the shared Wolverine storage schema, which ADR-0005 already foresees ("each module role gets only the grants it needs there").
- One Postgres transaction is one connection, and one connection has one session role. Any design that is atomic in a single transaction therefore runs the audit `INSERT` under the audited module's role.
- The #23 race paths (StartTrial's 23505, GrantOverride's 23505 re-read and save, RevokeOverride's concurrency exception) must keep working.
- Wolverine is not referenced yet, and adding it for one table is out of scope (#84). No new package.
- Every application role lives in the one API process, which holds every module's connection string. Role separation protects against a bug or an injection in one module's SQL path, not against a compromised process.

## Decision drivers

- Atomicity with no distributed transaction and no eventual window.
- Least privilege: the audit table is append-only for every application role, and no role can read it in #24 (G1: no read path).
- Keep the #22 filter and guard, and the ADR-0012 target-tenant scope, unchanged.
- Solo developer: no new process, relay, package, connection string or AppHost resource.

## Considered options

1. **Append in the caller's transaction, with an INSERT-only grant to the caller's role.** `Modules.Audit.Contracts.IAuditWriter.AppendAsync(entry, DbTransaction, ct)` builds an `AuditDbContext` on the caller's connection and enlists it in the caller's transaction. The audited module's role gets `USAGE` on schema `audit` and `INSERT` on `audit.audit_records`, nothing more. Chosen.
2. **Own `decisya_audit` role and connection.** This is the ADR-0005 shape, but it means two connections and two commits, so it is not atomic. A crash between the commits leaves a change with no record, or a record of a change that never happened. Rejected.
3. **Outbox in the `entitlements` schema with a relay to `audit`.** Atomic at write time, but the pending record lives where `decisya_entitlements` has `UPDATE` and `DELETE`, so it is not append-only until relayed. It also needs a background relay host, idempotent delivery and eventual-consistency tests. Rejected for #24. Wolverine's EF Core outbox (ADR-0005) is the later replacement for this ADR.
4. **`SET ROLE decisya_audit` inside the transaction.** The caller must be a member of the audit role, which is the same privilege as a direct grant with more machinery. It needs raw SQL, which `CrossTenantQueryRule` bans. Rejected.
5. **Two-phase commit (`PREPARE TRANSACTION`).** Needs `max_prepared_transactions > 0`, and orphaned prepared transactions hold locks until an operator resolves them. Rejected.
6. **Map the audit entity into `EntitlementsDbContext`.** One `SaveChanges`, but Entitlements would then own Audit's table mapping, and its migrations would compete for `audit.audit_records`. Rejected (ADR-0005 boundary).

## Decision outcome

Chosen option: **1, append in the caller's transaction**, because it is the only option that is atomic, append-only from the first byte, and needs no new runtime component.

1. **Contract.** `IAuditWriter.AppendAsync(AuditEntry, DbTransaction, CancellationToken)` is the Audit module's only public operation. The writer never opens a connection, never commits and never rolls back. It reads the actor (`ICurrentCaller`), the time (`IClock`) and the trace id (`Activity.Current`) itself, so a caller can neither forge nor omit them.
2. **Grant.** Only the roles of modules that call `IAuditWriter` hold, on schema `audit`, `USAGE` and, on `audit.audit_records`, `INSERT`. They get no `SELECT`, `UPDATE`, `DELETE`, `TRUNCATE`, `REFERENCES`, `TRIGGER` or `CREATE`, nothing on `audit."__EFMigrationsHistory"`, and nothing else in `audit`. The migrator applies this through an append-only grant step that takes an explicit list of writer roles. The list is `decisya_entitlements` in #24, and a new caller is a reviewed change to that list. This is the one sanctioned exception to "a module role touches only its own schema". The rule "a module role cannot **read** another module's schema" still holds for every role.
3. **No Audit login role until there is a reader.** #24 has no read path, so it creates no `decisya_audit` role, password parameter or connection string. The issue that adds the platform-admin reader (an audited `[AllowCrossTenant]` handler, #25 or later) adds a `decisya_audit` role with `SELECT` only.
4. **The audited handler's unit of work.** Each audited `[AllowCrossTenant]` handler opens one explicit transaction on its own ADR-0012 context and does its existing work in it. It then calls `AppendAsync` with that transaction and commits. A refusal, a validation failure or an exception disposes the transaction uncommitted, so nothing persists.
   - The #23 23505 and concurrency paths keep working because EF Core creates a savepoint before each `SaveChanges` inside a user transaction, and rolls back to it on failure (auto-savepoints, on by default). `AutoSavepointsEnabled` is never turned off.
   - An execution strategy that retries (`EnableRetryOnFailure`) must not be added without wrapping the whole unit in `strategy.ExecuteAsync`.

### Consequences

- Good: The record and the change share one commit, so NFR-37 holds by construction, with no window and no relay.
- Good: The audit table is append-only for every application role from the first insert. No application role can read it.
- Good: There is no new process, package, connection string, AppHost parameter or migrator configuration key.
- Good: A module whose role holds no grant cannot write audit records even if it calls the contract. The database enforces who can audit.
- Bad: `decisya_entitlements` can insert audit rows for any tenant and any actor value through raw SQL. A bug or injection in Entitlements could add forged records. It still cannot hide or change an existing one, and every genuine record carries the trace id. Accepted. The tamper-evident chain is deferred (#24 G1).
- Bad: The `IAuditWriter` contract exposes `System.Data.Common.DbTransaction`, so the Audit module cannot move to its own host without changing its callers. This is a deliberate exception to ADR-0005's "hybrid" property. The Wolverine EF Core outbox removes it when Wolverine arrives, and the contract changes then.
- Bad: Audited handlers hold their transaction a little longer (one more `INSERT`). Racing `StartTrial` losers wait on the unique index until the winner commits, exactly as before.
- Bad: The ADR-0005 cross-schema role test changes. Each role still cannot read the other modules' schemas. In addition, `decisya_entitlements` must be able to `INSERT` into `audit.audit_records` and nothing else there, and `decisya_tenancy` must have nothing at all in `audit`.
- Invariants: no `CLAUDE.md` invariant changes. This ADR states how an `[AllowCrossTenant]` handler is "audited" (invariant 1).
- Enforced by:
  - `Decisya.ArchitectureTests`: a new `CrossTenantAuditRule` (every `[AllowCrossTenant]` type in `ArchitectureScope` outside `Decisya.Modules.Audit` references `IAuditWriter.AppendAsync`, with a violating and a compliant fixture), and a new `ExplicitTransactionRule` (`BeginTransaction*` and `UseTransaction*` only in the audited handlers and the audit writer; `AutoSavepointsEnabled` never set).
  - `Decisya.Infrastructure.Migrator.Tests` (Testcontainers), as each application role: `UPDATE`, `DELETE` and `TRUNCATE` on `audit.audit_records` fail with `42501`, and the role holds exactly the privileges listed above, before and after an idempotent re-run.
  - The Entitlements module tests: the fault-injection and race matrix of NFR-37, run end to end as `decisya_entitlements` against the migrated schema.

## Amendments

<!-- Dated, attributed additions only; earlier text is never rewritten. -->

### Amendment 1 (Proposed, 2026-11-21, #341 G2): tenant users' writes in a module are audited through the same contract, with a subject id

- Status: **Accepted** (Marco, 2026-11-22, #341 G6).
- Accepted residual risks: "R-L-FORGE (ADR-0013 amendment 1): forged audit rows written as `decisya_ledger`" and "R-L-LOCK (the limit's book-row lock, G2 D2; T341-20)" are accepted at full scope, as the #341 G6 review states them ([`docs/security/reviews/341.md`](../security/reviews/341.md), "Residual risks for Marco"), as known, unfixed vulnerabilities, tracked in #243 before the beta: an audit-forgery detection job (R-L-FORGE) and the create `lock_timeout` (R-L-LOCK).
- Architecture note: [`docs/architecture/341-ledger-accounts.md`](../architecture/341-ledger-accounts.md).

**Context.** The decision above was written for the three Entitlements admin commands (`[AllowCrossTenant]`, ambient `None`). #341 adds the Ledger module, whose account writes (create, rename, currency change) are made by a tenant's owner under the ambient tenant and must be audited in the same transaction (EP-02, G1 Story 7, NFR-83). The record must name the account it changed, and must carry no account name, amount or other personal data. `AuditEntry` today is `(TenantId, Action, FeatureKey?)` with a closed set of three Entitlements actions, so it can name neither a ledger action nor an account. Forces: the outcome above (one transaction, one connection, INSERT-only grant) must keep holding; no new package; one contract for every module; no free text.

**Considered options.**
1. **Extend the one contract:** a new optional `SubjectId` (an opaque `Guid` of the changed object) on `AuditEntry`, three new `AuditAction` values, and per-action shape and ambient rules in the writer. Chosen.
2. **A second, Ledger-specific writer interface or table.** Rejected: two append-only paths to review, two grant steps, and the audit reader (later) would join two tables.
3. **Put the account id in `FeatureKey`.** Rejected: it overloads a typed field, breaks its `module.feature` shape check, and invites free text later.
4. **A free-text `Details` field.** Rejected: free text is where names and amounts leak (G1 Story 7, NFR-62).

**Decision (adds to points 1, 2 and 4; nothing above is rewritten).**
1. `AuditEntry` becomes `AuditEntry(TenantId TenantId, AuditAction Action, string? FeatureKey, Guid? SubjectId = null)`. `SubjectId` is the id of the object the command changed, never a value of it. Existing callers compile unchanged.
2. `AuditAction` gains `LedgerAccountCreate = 4`, `LedgerAccountRename = 5`, `LedgerAccountCurrencyChange = 6`, stored as `ledger.account.create`, `ledger.account.rename`, `ledger.account.currency_change`. A new action is still a reviewed change to the closed set. A module appends only actions of its own family: `ledger.*` from `Modules.Ledger`, `entitlements.*` from `Modules.Entitlements` (#341 G3 F341-01; enforced by a source scan, rule R-L21 of the #341 note, implemented as `Decisya.ArchitectureTests.AuditActionFamilyScanTests`).
3. The writer checks the entry's shape per action, before any SQL: the two override actions need a feature key and no subject; trial start needs neither; the three ledger actions need a non-empty subject and no feature key.
4. The writer's ambient check becomes per action family: the Entitlements actions keep the rule above (`None`, or `Tenant` equal to the entry's tenant). The ledger actions require ambient `Tenant` equal to the entry's tenant; `None` (a platform admin) and `Invalid` are refused, so an admin path can never write a tenant user's record.
5. Point 4's unit of work applies to these handlers too, although they are not `[AllowCrossTenant]`: one explicit transaction on the module's own (ambient-tenant) context, the write, `AppendAsync`, commit; a refusal disposes it uncommitted.
6. Point 2's writer-role list becomes `decisya_entitlements` and `decisya_ledger`; `decisya_ledger` gets exactly the same two grants and nothing else in `audit`.
7. Storage: a nullable `subject_id uuid` column; the action check lists six codes; the feature-key check becomes "feature key present exactly for the two override actions"; a new check makes `subject_id` present exactly for the three ledger actions.

**Consequences.**
- Good: one append-only table and one contract for every module; the database refuses a malformed row even if the writer's checks were bypassed.
- Good: the record can name the changed object without carrying any of its values.
- Bad: `decisya_ledger` can insert forged audit rows through raw SQL, as the Bad consequence above already accepts for `decisya_entitlements`; raw SQL stays banned in both modules by `CrossTenantQueryRule` and `RawAdoNetRule`.
- Bad: the Audit migration changes two check constraints on a table that already holds records. Existing rows satisfy the new checks (their `subject_id` is null and their actions are unchanged).
- Invariants: no `CLAUDE.md` invariant changes.
- Enforced by: the rules and tests in the #341 architecture note. Each test named here exists under `tests/` (checked on 2026-11-22 for #341 G6-341-07; the map is the G5 Traceability in [`docs/requirements/341-ledger-accounts.md`](../requirements/341-ledger-accounts.md)).
  - Audit writer shape and ambient matrix (points 3 and 4, rule R-L18), `Decisya.Modules.Audit.Tests`: `AuditWriterTests.A_ledger_record_is_refused_when_the_ambient_tenant_is_None_before_any_SQL`, `A_ledger_record_is_refused_when_the_ambient_tenant_is_Invalid_before_any_SQL`, `A_ledger_record_is_refused_when_the_ambient_tenant_is_another_tenant_before_any_SQL`, `A_ledger_record_without_a_subject_id_or_with_an_empty_one_is_refused_before_any_SQL`, `A_ledger_record_that_carries_a_feature_key_is_refused_before_any_SQL`, `An_Entitlements_record_that_carries_a_subject_id_is_refused_before_any_SQL`, `A_valid_ledger_record_under_its_own_tenant_passes_the_guard_and_reaches_the_database`, `The_Entitlements_family_keeps_its_ambient_rule_None_or_the_matching_tenant` (Unit) and `A_valid_ledger_record_under_its_own_tenant_is_stored_with_the_subject_id_and_the_actor` (Integration).
  - The new checks (point 7): no class named `AuditModelTests` exists; the earlier text of this line named it in error. The model side is `AuditModelAndRegistrationTests.The_six_check_constraints_and_the_tenant_leading_index_the_design_names_exist_in_the_model` (`Decisya.Modules.Audit.Tests`, Unit; it pins `ck_audit_records_subject` among the check names). The database side is `LedgerAuditTests.The_audit_migration_adds_the_subject_column_and_the_three_checks` (`Decisya.Modules.Ledger.Tests`, Integration: `subject_id` is a nullable `uuid`, the three checks exist, the action check holds the six codes).
  - `decisya_ledger` as a second writer role (point 6): `AuditGrantsIntegrationTests` (`Decisya.Infrastructure.Migrator.Tests`, Integration; the table and schema ACLs are exactly the owner plus the two writers, before and after a re-run) and `LedgerRoleIntegrationTests.Decisya_ledger_is_an_audit_writer_with_exactly_USAGE_on_the_schema_and_INSERT_on_the_table`.
  - The Ledger fault-injection matrix of NFR-83 (point 5), `Decisya.Modules.Ledger.Tests`, Integration, as `decisya_ledger`: `LedgerAuditTests.A_failed_audit_write_rolls_the_account_write_back_for_a_create`, `An_audit_append_the_database_refuses_rolls_the_account_write_back_for_a_create`, `A_commit_that_fails_after_the_audit_append_leaves_no_account_and_no_record_for_a_create`, `A_failed_audit_write_rolls_the_rename_back_and_a_failed_currency_change_too` and `A_failed_account_write_leaves_no_audit_record_for_a_rename_and_a_currency_change`.
  - Point 2's family rule (R-L21), `Decisya.ArchitectureTests`: `AuditActionFamilyScanTests.Over_the_real_source_tree_each_module_uses_only_its_own_audit_action_family`, with the red cases `A_red_fixture_that_appends_another_modules_action_fails_the_scan` and `A_using_static_of_AuditAction_outside_the_Audit_module_fails_the_scan` and the green case `Each_module_may_use_its_own_family_and_the_Audit_module_may_use_all_of_them`.
