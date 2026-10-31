# security-reviewer addendum (Decisya)

Read with Norma's `security-reviewer` agent and its `threat-model` and `asvs-checklist` skills. This file adds Decisya's assurance level, domain checks and inputs; it never drops a core check (#246, ADR-0023).

## Assurance level
Financial data: ASVS 5.0 **Level 2** everywhere; the **V6 (authentication) and V7 (session management) chapters apply Level 3**.

## Triggers
Run a threat delta before any new module, external integration, file upload, AI feature or auth change, and a diff review on every PR that touches auth, sessions, input handling, data access, file upload, AI prompts or infrastructure.

## Always check (in addition to the core list)
- BFF cookie flags; no token ever reaches the browser; antiforgery on every non-GET request.
- Object-level authorization on every handler (BOLA); the tenant filter is never bypassed (CLAUDE.md invariant 1).
- `[Sensitive]` present on every PII field (CLAUDE.md, observability).
- Dependencies: no High or Critical from `dotnet list package --vulnerable --include-transitive` or `npm audit`.
- AI lanes: prompt injection, output validation, spend caps, no advice-like output (CLAUDE.md invariants 2, 3 and 6).

## Threat model focus
- Inputs: the issue's requirements and architecture note, and the module's Contracts project and endpoints when they exist.
- Trust boundaries: browser | BFF | API | data stores | external providers.
- Financial context: Tampering (amounts, tenant id), Information disclosure (PII in logs, cross-tenant reads), Elevation (BOLA on account ids), Denial of service (unbounded inputs).

## Decisya control focus (ASVS 5.0 chapters)
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

## Phase exit baseline
At every phase exit, record the full baseline in `docs/security/asvs-l2.md` (the "Phase <p> exit verdict" section), as at the Phase 0 exit (#228).

## Backlog
Backlog findings go to the current phase backlog issue (Phase 1: #243). A High or Medium flaw, even in unchanged code, goes to the orchestrator at once, for Marco.
