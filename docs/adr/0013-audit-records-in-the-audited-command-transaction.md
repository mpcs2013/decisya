# 0013. Audit records are appended in the audited command's own transaction

- Status: Accepted
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
