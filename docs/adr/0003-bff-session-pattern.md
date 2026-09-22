# 0003. BFF session pattern with Redis ticket store

- Status: Proposed
- Date: 2026-09-20
- Deciders: Marco
- Tags: security

## Context and problem statement

Client-side token storage exposes tokens to XSS. Sessions must be server-side.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **SPA with tokens in memory** — still exposed to XSS via the JS context.
2. **Duende BFF** — excellent, but licensing conditions apply above a revenue threshold.
3. **Hand-rolled BFF: ASP.NET Core cookie auth + OIDC + Redis ticket store + YARP** — no licence, full control.

## Decision outcome

Chosen option: **Hand-rolled BFF**

### Consequences

- Good: Browser never sees a token; session cookie HttpOnly/Secure/SameSite=Strict; OIDC correlation/nonce cookies SameSite=Lax
- Bad: We own refresh-token rotation and logout correctness
- Enforced by: Integration tests: cookie flags, no token in any browser-visible response, refresh path, alg allow-list on the API
