# 0008. React SPA behind the BFF with server-computed capability manifest

- Status: Accepted (2026-09-20); amendment 1 Accepted (#26, Marco 2026-10-03)
- Date: 2026-09-20
- Deciders: Marco
- Tags: module

## Context and problem statement

Subscription plans gate modules; the UI must reflect entitlements without ever being the enforcement point.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **Feature flags in the SPA bundle** — trivially bypassed.
2. **Blazor** — one language, but React/Vite/Playwright is the frontend stack Marco targets for the product; rejected for product and skills reasons, not technical ones.
3. **React SPA; /bff/me returns a capability manifest computed by IEntitlementService** — UI hides, API denies.

## Decision outcome

Chosen option: **React SPA (Vite) served by the BFF (Decisya.Bff, ADR-0003) on the same origin; GET /bff/me returns a capability manifest computed server-side by IEntitlementService (declared in the entitlements module's Contracts); every API endpoint enforces the same entitlement through an authorization policy**

### Consequences

- Good: Single source of truth for entitlements
- Good: One origin keeps SameSite=Strict working and allows a strict CSP
- Bad: Two languages in the repo
- Bad: The manifest can be stale after a plan change: short cache, and the SPA re-fetches /bff/me on 403
- Bad: Manifest and endpoint policy must share one IEntitlementService implementation
- Enforced by: An endpoint-metadata test enumerates every endpoint under /api and fails unless it requires authorization and carries an entitlement policy or an explicit no-entitlement marker; API tests that a tenant without the capability gets 403 on direct calls; a contract test on the /bff/me manifest schema; Playwright (Firefox and Chromium) checks gated routes are hidden and a forced navigation shows the denial page

## Amendment 1 (issue #26): the manifest is `GET /api/capabilities`, not `/bff/me`

- Status: **Accepted** (Marco, 2026-10-03; direction chosen as #26 G1 Q1). The decision above stays Accepted; this amendment replaces the manifest's location and the staleness rule, and names the no-entitlement marker.
- Architecture note: [`docs/architecture/spa-shell.md`](../architecture/spa-shell.md).

### Context

The decision above puts the manifest in `GET /bff/me`. `Decisya.Bff` cannot compute it there without breaking another rule:

- The BFF references only `Decisya.ServiceDefaults` and has no database role (#18; `BffBoundaryTests`). `IEntitlementService` needs the Entitlements module, its `DbContext`, the `decisya_entitlements` role and the ambient tenant, which only `Decisya.Api` resolves (#20, #21).
- `/bff/me` is anonymous and always answers 200 (#18). A manifest inside it would make that endpoint call the Api with the user's token and carry its own fail-closed rule.

Forces: least privilege per host (CLAUDE.md), one `IEntitlementService` for the manifest and the policies (this ADR), no new package, solo-developer time.

### Considered options

1. **`/bff/me` computes the manifest in-process** (the BFF references Entitlements and gets a database connection). Rejected: it breaks the BFF's one-reference rule and gives the session host a database role.
2. **`/bff/me` calls the Api server-side with the session's access token and merges the answer.** Rejected: it adds a second forwarding path beside YARP, with its own token refresh and fail-closed handling, to an anonymous always-200 endpoint.
3. **A new `GET /api/capabilities` on `Decisya.Api`, reached through the existing BFF `/api` forwarding (#19).** The Api already holds the validated caller, the ambient tenant and the scoped `IEntitlementService` that the policies use. **Chosen.**

### Decision (replaces "GET /bff/me returns a capability manifest" and the staleness consequence)

1. The capability manifest is `GET /api/capabilities` on `Decisya.Api`. The SPA reaches it at the same path on the BFF origin; the BFF forwards it with the access token like every other `/api` call. `GET /bff/me` stays identity-only.
2. The body is exactly `{"capabilities": {"<feature key>": true|false, ...}}`. It lists every key in `Decisya.Modules.Entitlements.Contracts.FeatureKeys.All`, each evaluated once by the request-scoped `IEntitlementService`. It carries no plan, trial date, override reason, tenant id or actor id. The SPA treats an absent key as denied.
3. Any exception from `IEntitlementService` gives the generic 500 `ProblemDetails`; the endpoint never returns a partial manifest and never turns an error into `true` (#23 G3 C-3). A caller with no tenant (a platform admin) gets 200 with every key `false`.
4. The SPA fetches the manifest on every full page load, and re-fetches it once when an `/api` call it makes returns 403 (the earlier text said `/bff/me`). A 403 from the manifest itself is not re-fetched.
5. The explicit no-entitlement marker is `Decisya.SharedKernel.Authorization.NoEntitlementRequiredAttribute`, used as endpoint metadata. `/api/capabilities` carries it (it reports gates; it is not one), and so does every other `/api` endpoint that has no entitlement policy today.

### Consequences

- Good: the BFF stays module-free and database-free; the manifest and the endpoint policies resolve the same `IEntitlementService` registration in the same process.
- Good: the manifest gets the Api's existing protections for free: the fallback policy (authenticated, `sub`), the caller-context 403s, the tenant-membership gate, `Cache-Control: no-store` on every response, and the generic 500.
- Bad: a signed-in page load makes two calls (`/bff/me`, then `/api/capabilities`). A token-refresh failure at the BFF shows as a manifest failure; the SPA then hides every gated item (fail closed).
- Bad: every existing `/api` endpoint gains the marker in the same issue, because the enumeration test cannot pass otherwise.
- Invariants: no `CLAUDE.md` invariant changes.
- Enforced by (replaces the list above):
  - `Decisya.Api.Tests` endpoint-metadata test: every `/api` endpoint of the real host is non-anonymous and carries the marker or an entitlement policy. The marked set is pinned exactly.
  - `Decisya.Api.Tests` manifest tests: the response shape, keys equal to `FeatureKeys.All`, values equal to `IsEnabledAsync` (through a stub registration), 500 on an evaluation exception, all-false for a tenant-less caller, 401 when anonymous, `no-store`.
  - Entitlements module tests: `FeatureKeys.All` equals the public `FeatureKey` properties of `FeatureKeys` and the plan catalog's known set.
  - API tests that a tenant without a capability gets 403 on direct calls: the first feature-gated endpoint.
  - Playwright (Firefox and Chromium): gated routes are hidden and a forced navigation shows the denial page.
