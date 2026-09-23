# ASVS 5.0 L2 – Decisya curated controls

Chapter numbers follow **ASVS 5.0** (May 2025). They differ from ASVS 4.0: for example session management moved from V3 to V7, and V3 is now web frontend security. Cite controls as `V<chapter>.<section>.<requirement>` from the official 5.0 text; expand a chapter from the official document the first time a change touches it.

| Chapter | Decisya focus |
| --- | --- |
| V1 Encoding and sanitization | Output encoding in the SPA; no string-built SQL, shell or LDAP; parameterised queries only |
| V2 Validation and business logic | FluentValidation schemas at every endpoint, size limits, business limits (e.g. `Money.MaxAllocationParts`), anti-automation on costly operations |
| V3 Web frontend security | CSP, `X-Content-Type-Options`, `Referrer-Policy`, cookie flags seen from the browser, no token reachable from JavaScript |
| V4 API and web service | JSON only, CORS locked to the BFF origin, rate limiting on auth and AI endpoints, internal API unreachable from the internet |
| V5 File handling | Upload size/type allow-list, stored in object storage outside the web root, scanned, served with `Content-Disposition` |
| V6 Authentication | Keycloak-managed; MFA enforcement, password policy delegated, no local password storage — **apply L3 requirements** |
| V7 Session management | BFF cookie `HttpOnly; Secure; SameSite=Strict`, Redis ticket store, sliding/absolute timeouts, logout ends the Keycloak session — **apply L3 requirements** |
| V8 Authorization | Policy per endpoint, object-level checks on every id (BOLA), tenant filter, admin role separation, deny by default |
| V9 Self-contained tokens | Internal JWTs: RS256/ES256 allow-list, `iss`/`aud`/`exp` validated, ≤ 5 min lifetime |
| V10 OAuth and OIDC | Confidential `decisya-bff` client, PKCE, correlation/nonce cookies `SameSite=Lax`, refresh rotation |
| V11 Cryptography | No custom crypto, platform primitives only, secrets from the environment |
| V12 Secure communication | TLS 1.2+, HSTS, internal hops over TLS where they cross hosts |
| V13 Configuration | Dependency scanning, no debug in production, secrets never in source or images |
| V14 Data protection | Encryption at rest, retention schedule, erasure job, export, minimal data in caches |
| V15 Secure coding and architecture | Integer overflow, resource limits, dependency hygiene, safe defaults for value types |
| V16 Security logging and error handling | Generic ProblemDetails, JSON logs with trace id, `[Sensitive]` masking, no stack traces to clients, no PII in exception messages |
| V17 WebRTC | Not used — N/A |
