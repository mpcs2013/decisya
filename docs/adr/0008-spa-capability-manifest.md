# 0008. React SPA behind the BFF with server-computed capability manifest

- Status: Accepted
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
