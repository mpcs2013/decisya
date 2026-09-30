# 0012. Cross-tenant admin commands run in a target-tenant scope

- Status: Accepted
- Date: 2026-09-30
- Deciders: Marco
- Tags: security, data
- Refines: [ADR-0001](0001-multi-tenant-single-instance.md) (how an `[AllowCrossTenant]` admin handler writes)

## Context and problem statement

Issue #23 adds the first commands that act on a tenant other than the caller's: a platform admin, who has no `tenant_id` claim (resolution `None`), starts a trial or grants and revokes a feature override for an explicit target tenant. Platform invariant 1 allows cross-tenant work only in an `[AllowCrossTenant]`-attributed, audited admin handler.

The #22 machinery does not give such a handler a write path:

- `TenantDbContext`'s `SaveChanges` guard throws `NoTenant` for any tenant-scoped write under `None`, and `TenantMismatch` for a row whose `TenantId` is not the ambient tenant. It has no bypass, by design.
- `[AllowCrossTenant]` only permits the read bypasses on `CrossTenantQueryRule`'s list (`IgnoreQueryFilters`, `GetDatabaseValues`, `Reload`, raw SQL). None of them helps a write.

Every later admin feature (#25's admin API, support tooling, tenant export) hits the same problem, so the answer is a platform pattern, not an Entitlements detail.

## Decision drivers

- Financial data: tenant isolation is non-negotiable, and the guard is the last line before SQL.
- Keep the #22 guard and filter unchanged: no new escape hatch in `TenantDbContext`.
- #24 must be able to find every cross-tenant handler by reflection over `AllowCrossTenantAttribute`.
- Solo developer: one small, checkable pattern that needs no new package.

## Considered options

1. **`[AllowCrossTenant]` handler with `IgnoreQueryFilters` for reads and a guard bypass for writes.** Rejected: it adds a write escape hatch to `TenantDbContext`, and a filter-free read sees every tenant's rows when only one tenant is meant.
2. **Target-tenant scope.** The `[AllowCrossTenant]` handler builds its own short-lived module context whose `ICurrentTenant` is fixed to `TenantResolution.For(target)`. The filter restricts reads to the target, and the guard accepts exactly the target's writes. `TenantResolution.For` and `FromClaim` join `CrossTenantQueryRule`'s list, so only an attributed type can mint a tenant scope. Chosen.
3. **Mutable ambient tenant: switch the request's scoped `ICurrentTenant` for the command's duration.** Rejected: the #21 `RequestCaller` is set once by design (B-1), and a switch would leak into every other scoped service that resolves during the command.
4. **Admin impersonation: the admin gets a token carrying the target `tenant_id`.** Rejected: the claim is Keycloak-managed per user (#20, #21). Per-command tokens need a token-exchange flow, which is far out of phase 0 scope.

## Decision outcome

Chosen option: **2, target-tenant scope**, because it keeps the filter and the guard as the only enforcement and adds no bypass. A cross-tenant command can touch only the one tenant it names.

1. A cross-tenant admin command carries an explicit target `TenantId`. Its handler type carries `[AllowCrossTenant("<justification>")]`.
2. The handler checks that the ambient resolution is `None` (a platform admin). `Tenant` and `Invalid` give a `Forbidden` result before any database access. Authorization proper is the admin endpoint's role policy (#25); the `None` check is fail-closed defence in depth, not authorization.
3. The handler creates the module context itself, from the module's `DbContextOptions<T>` and a module-internal `ICurrentTenant` fixed to `TenantResolution.For(target)`, and disposes it before returning. The request's scoped `ICurrentTenant` is never changed.
4. `TenantResolution.For`, `TenantResolution.FromClaim` and `TenantResolution.NoTenant` are added to `CrossTenantQueryRule`'s list. Inside `ArchitectureScope` (Persistence and the modules), only an `[AllowCrossTenant]` type may create a tenant resolution. `Decisya.Api` is outside the scope; `ApiBoundaryTests` allows only `CallerContextMiddleware` to call `For`, `FromClaim` or `NoTenant` there, and #25's endpoints must not add a second type.
5. Until #24 adds the audit record, a cross-tenant command must not be reachable over HTTP. #23 enforces this for Entitlements: its handlers are `internal` and the module does not reference ASP.NET Core.

### Consequences

- Good: No change to `TenantDbContext`. The filter and the guard cover cross-tenant writes exactly as they cover tenant writes.
- Good: A handler bug cannot touch a third tenant. Any row outside the target fails the guard, and a filtered read never returns one.
- Good: #24 finds every cross-tenant handler through the attribute it already plans to reflect over.
- Bad: A handler cannot join the request's ambient unit of work. Its context is separate, so a future Wolverine outbox needs the envelope-tenant middleware (ADR-0005) instead of this pattern for message handlers.
- Bad: Until #24, the attribute marks the handlers but nothing records their calls. This is accepted for #23 because the commands have no HTTP path (#23 G1 R-1, Marco 2026-09-30).
- Invariants: no `CLAUDE.md` invariant changes. This ADR states how an `[AllowCrossTenant]` admin handler meets invariant 1.
- Enforced by: `CrossTenantQueryRule` with the three added entries (with a violating and a compliant fixture) and `AllowCrossTenantJustificationRule`, both in `Decisya.ArchitectureTests`. In `Decisya.Api.Tests`: `ApiBoundaryTests.Only_CallerContextMiddleware_mints_a_tenant_resolution_in_Decisya_Api`. In the Entitlements module tests: its `[AllowCrossTenant]` types are not public, the module does not reference `Microsoft.AspNetCore`, a `Tenant` or `Invalid` ambient gets `Forbidden` with no row written, and a command for tenant A changes no row of tenant B.
