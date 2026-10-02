# 0012. Cross-tenant admin commands run in a target-tenant scope

- Status: Accepted (2026-09-30); amendment 1 Accepted (#25, Marco 2026-10-02)
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

## Amendment 1 (issue #25): platform-admin precondition, cross-module facts under the target scope, the HTTP path

- Status: **Accepted** (Marco, 2026-10-02). The decision above stays Accepted; this amendment changes points 2 and 5 and adds point 6.
- Architecture note: [`docs/architecture/admin-api.md`](../architecture/admin-api.md).

### Context

#25 exposes the three Entitlements commands over HTTP for the first time. Three things in the decision above no longer hold or no longer suffice:

- **Point 2 is too weak once there is a transport.** `None` means "a validated caller with no `tenant_id` claim", not "a platform admin" (#23 G3 C-2, T-03). Any realm user whose `tenant_id` attribute is missing would pass it.
- **The handler needs a fact another module owns.** S-5 (#23 G3) requires the target tenant to exist before a write. The tenant row lives in `tenancy.tenants`. `decisya_entitlements` cannot read that schema (ADR-0005; the #23 cross-schema test), and Entitlements cannot build a `TenancyDbContext`.
- **Point 5's fence is lifted.** The audit record exists since #24 (ADR-0013), and #25 adds the HTTP path.

Forces: the #22 filter and guard stay the only enforcement; `CrossTenantAuditRule` (#24) requires every `[AllowCrossTenant]` type outside the writer to append an audit record; no new package, no Wolverine yet (#84); one solo developer.

### Considered options for the cross-module fact (point 6)

1. **The handler passes its minted target scope to the other module's Contracts query.** Tenancy builds its own context under that resolution, on its own role, and returns a `bool`. The receiving type mints nothing and is not attributed. **Chosen.**
2. **Mark the Tenancy reader `[AllowCrossTenant]`.** `CrossTenantAuditRule` then requires an audit record for a read probe: either a fourth `AuditAction` with a migration and a refused-call semantics nobody asked for, or an exemption list in the audit rule. Rejected: it weakens the rule #24 just shipped.
3. **Grant `SELECT` on `tenancy.tenants` to `decisya_entitlements`.** Rejected: it breaks ADR-0005's "a module role cannot read another module's schema", which ADR-0013 explicitly kept.
4. **Check in the Admin endpoint before the command.** The endpoint would have to mint `TenantResolution.For` (so it would need the attribute, as option 2), and a direct command call would skip the check (G1 #25 Story 5). Rejected.
5. **Replicate tenant existence into `entitlements` through a `TenantProvisioned` message.** The right long-term shape under ADR-0005, but it needs Wolverine and an outbox. Rejected for phase 0; recorded as the later replacement.

### Decision (amends points 2 and 5, adds point 6)

2. *(replaces point 2)* The handler requires the ambient resolution to be `None` **and** `ICurrentCaller.IsPlatformAdmin` to be true. Anything else gives `Forbidden` before any validation or database access. `IsPlatformAdmin` is set once per request by `RequestCaller`, only from the validated access token (#20): the exact realm role `platform-admin` in the `roles` claim, and no `tenant_id` claim. It defaults to `false` on every implementation that does not set it. Authorization proper is the `Admin.PlatformAdmin` policy on the `/api/admin` endpoint group; the handler check stays fail-closed defence in depth, now strong enough to stand on its own.
5. *(replaces point 5)* A cross-tenant command is reachable over HTTP only through `Decisya.Modules.Admin`, which calls the module's public admin contract (`Decisya.Modules.Entitlements.Contracts.Admin.IEntitlementAdminCommands`). The handlers, the command records and the attributed types stay `internal`, and the Entitlements module still references no ASP.NET Core assembly. Only `Decisya.Modules.Admin` and the owning module may depend on a module's `Contracts.Admin` namespace (rule).
6. *(new)* When an `[AllowCrossTenant]` handler needs a fact that another module owns about its target tenant, it passes the `TenantResolution` it minted to that module's Contracts query, and never a raw `TenantId`. The receiving module builds its own short-lived context from its own `DbContextOptions<T>` and a module-internal `ICurrentTenant` fixed to that resolution, reads through the ordinary filter, and returns only the fact (no entity, no DTO with tenant data). The receiving type carries no `[AllowCrossTenant]`, calls no bypass-list member, and refuses a resolution that is not of kind `Tenant`. The first use is `Decisya.Modules.Tenancy.Contracts.ITenantExistence.ExistsAsync(TenantResolution, CancellationToken)`.

Why option 1 is safe: a `TenantResolution` for an arbitrary tenant can only be minted inside `ArchitectureScope` by an `[AllowCrossTenant]` (and therefore audited) type, or in `Decisya.Api` by `CallerContextMiddleware` from the validated claim. Any other caller can only pass its own ambient resolution, so it can only ask about its own tenant.

### Consequences

- Good: S-5 holds for HTTP and for a direct command call, with no new grant, schema, migration or package. The ADR-0005 cross-schema tests stay true.
- Good: `CrossTenantAuditRule` and `CrossTenantQueryRule` are unchanged; no exemption is added.
- Good: a no-`tenant_id` realm user who is not an admin can no longer run an admin command even if the endpoint policy is removed by mistake.
- Bad: the existence check and the write are two connections and two moments (#25 G1 R-4). Harmless while tenants cannot be deleted; a tenant-deletion feature must revisit it, for example with option 5.
- Bad: a resolution value now crosses a module boundary. A future Contracts query that takes a `TenantResolution` must follow point 6 exactly (refuse non-`Tenant`, return only a fact); the rules below pin the first one, and G2 reviews each new one.
- Bad: one more `TargetTenant`-style holder type (the third copy, in Tenancy, after Entitlements and Audit). Moving it into `Decisya.Infrastructure.Persistence` stays on #83 with the `InstantConverter` entry.
- Invariants: no `CLAUDE.md` invariant changes. Invariant 1 is met as before: the only cross-tenant writes and reads run under a scope minted by an attributed, audited handler, through the filter.
- Enforced by (in addition to the list above):
  - Entitlements module tests: each handler refuses `None` with `IsPlatformAdmin == false`, and `Tenant` or `Invalid` with `IsPlatformAdmin == true`, with no database command; the attributed set is still exactly the three handlers; the module still references no `Microsoft.AspNetCore*` assembly; it references `Decisya.Modules.Tenancy.Contracts` and not `Decisya.Modules.Tenancy`.
  - `Decisya.ArchitectureTests`: only `Decisya.Modules.Admin` and `Decisya.Modules.Entitlements` depend on `Decisya.Modules.Entitlements.Contracts.Admin`; `Decisya.Modules.Admin` has no `[AllowCrossTenant]` type and references no `TenantResolution` minting member.
  - Tenancy module tests: `TenantExistence` is not attributed, references no bypass-list member, throws `ArgumentException` for `None` and `Invalid` before any SQL, returns `false` for an unknown tenant and `true` for an existing one, and reads only the named tenant's row (isolation test).
  - `Decisya.SharedKernel.Tests`: `ICurrentCaller.IsPlatformAdmin` exists and its default implementation returns `false`.
