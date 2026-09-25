# 0003. BFF session pattern with Redis ticket store

- Status: Accepted
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
2. **Duende BFF** — mature, but a commercial licence is required above the Community Edition revenue threshold.
3. **Hand-rolled BFF: ASP.NET Core cookie auth + OIDC + Redis ticket store + YARP** — no licence, full control.

## Decision outcome

Chosen option: **Hand-rolled BFF in its own host (Decisya.Bff): ASP.NET Core cookie + OpenID Connect handlers, an ITicketStore in the Redis-protocol store (ADR-0007) protected with Data Protection, and YARP forwarding /api/* to Decisya.Api with the user's Keycloak access token attached server-side. The BFF serves the SPA's static files, so SPA and BFF share one origin.**

### Consequences

- Good: Browser never sees a token; session cookie HttpOnly/Secure/SameSite=Strict; OIDC correlation/nonce cookies SameSite=Lax
- Good: The API only ever sees a Keycloak access token (lifespan ≤ 5 min, ADR-0002), validated for iss, aud, exp and the RS256/ES256 allow-list
- Bad: We own refresh-token rotation and logout correctness
- Bad: Redis is on the login path; an outage signs every user out. Redis requires auth, and TLS when off-host (ADR-0007)
- Bad: The Data Protection key ring is persisted and shared, or each deploy invalidates all sessions
- Bad: Refresh-token rotation needs a per-session lock against concurrent refreshes; logout needs RP-initiated and back-channel logout
- Enforced by: Integration tests (Testcontainers Redis + Keycloak): session cookie HttpOnly/Secure/SameSite=Strict and correlation/nonce Lax; no access, refresh or id token in any browser-visible body, header or cookie; refresh under concurrent requests; back-channel logout deletes the ticket; CSRF header required on state-changing calls; the API rejects alg none, HS256, expired and wrong-aud tokens. ZAP baseline in CI (0.16)
