# 0008. React SPA behind the BFF with server-computed capability manifest

- Status: Proposed
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
2. **Blazor** — fewer moving parts, but React is the chosen frontend for real products.
3. **React SPA; /bff/me returns a capability manifest computed by IEntitlementService** — UI hides, API denies.

## Decision outcome

Chosen option: **React SPA with server-computed capability manifest**

### Consequences

- Good: Single source of truth for entitlements
- Bad: Two languages in the repo
- Enforced by: Entitlement tests on the API; Playwright checks gated routes
