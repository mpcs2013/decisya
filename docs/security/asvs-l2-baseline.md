# OWASP ASVS 5.0 baseline: Decisya at the Phase 0 exit (issue #27)

- Scope: all of `src/` on `main` at `a3e56dc`, plus `deploy/` and the AppHost. Tooling (agents, hooks, CI) is covered by the threat models, not here.
- Level: L2 everywhere; V6 (Authentication) and V7 (Session management) at L3, as `.claude/skills/asvs-checklist/references/l2-controls.md` requires. L3 rows outside V6 and V7 are left out.
- Numbering: ASVS 5.0.0 (May 2025). Rows cite requirement ids. A row that cites a section covers every requirement of that section at the level in scope. G6 should check any id it relies on against the official 5.0.0 text.
- Results: **Pass** (evidence), **Fail** (finding id in `docs/security/threat-models/system-baseline.md`), **N/A** (reason). "Delegated" means that Keycloak provides the control and the realm configuration or tests pin it. "Dev only" means the control holds for every launch path that exists today, and #29 must keep it for production.
- Dependency scan, 2026-10-03: `dotnet list decisya.slnx package --vulnerable --include-transitive` found no vulnerable package in 32 projects. `npm audit --audit-level=high` in `src/Decisya.Web` found 0 vulnerabilities.
- Reviewer: security-reviewer agent, 2026-10-03. Chapter summary: `docs/security/asvs-l2.md`.

## Summary

| Chapter | Result | Fails (register id) |
| --- | --- | --- |
| V1 Encoding and sanitization | Pass | — |
| V2 Validation and business logic | Fail (1) | V2.4.1 (C-09) |
| V3 Web frontend security | Pass with one Low | V3.4.1 (Low, #83) |
| V4 API and web service | Pass | — |
| V5 File handling | N/A | No upload or download feature |
| V6 Authentication (L3) | Fail | V6.2.4, V6.2.11, V6.2.12, V6.3.3 (C-02); V6.8.4 (C-01); V6.3.5, V6.3.7 (Low) |
| V7 Session management (L3) | Pass with Fails | V7.5.3 (C-01); V7.1.2, V7.4.2 (Low) |
| V8 Authorization | Pass | — |
| V9 Self-contained tokens | Pass | — |
| V10 OAuth and OIDC | Pass with one Fail | V10.3.4 (C-01) |
| V11 Cryptography | Pass; key rotation linked | V11.1.1 rotation (C-04, C-05, C-20) |
| V12 Secure communication | Fail (deployment) | V12.3.1, V12.3.3 (C-11); V12.1, V12.2 (#29) |
| V13 Configuration | Fail (deployment) | V13.3.1 (C-20); V13.4.2 (C-04); V13.2.1 (C-11) |
| V14 Data protection | Pass; retention linked | V14.2.4 retention (C-04, F-6 section) |
| V15 Secure coding and architecture | Fail (1) + Low | V15.2.2 (C-09); V15.1.1 (Low); V15.1.2 (#29 SBOM) |
| V16 Security logging and error handling | Fail | V16.3.1 (C-10); V16.4.2, V16.4.3 (C-04) |
| V17 WebRTC | N/A | Not used |

## V1 Encoding and sanitization

| Id | Result | Evidence |
| --- | --- | --- |
| V1.1.1 | Pass | `TenantId` accepts one canonical `D` form, with a 64-character cap before trimming (`TenantId.cs`; `TenantIdParsingTests`). `ReturnUrlValidator` decodes once and checks the result |
| V1.1.2 | Pass | React text nodes only. `static-rules.test.ts` bans `dangerouslySetInnerHTML`, `innerHTML`, `eval(` and `new Function` |
| V1.2.1 to V1.2.3 | Pass | SPA renders through React; JSON through System.Text.Json; `encodeURIComponent` for `returnUrl` (`src/Decisya.Web/src/api/bff.ts`) |
| V1.2.4 | Pass | EF Core only. Raw SQL APIs are on the bypass list (`CrossTenantQueryRule`, `RawAdoNetRule` allow-list, `BannedSymbols`). The migrator quotes identifiers and passes passwords as SCRAM verifiers (`MigrationRunner.cs`, `ScramSha256Verifier.cs`) |
| V1.2.5 | Pass | No process execution in `src/`. The Postgres init script uses psql variables (`KeycloakDbInitScriptTests`) |
| V1.2.6 to V1.2.9 | N/A | No LDAP, XPath, LaTeX or user-built regex |
| V1.3.1 to V1.3.5 | N/A | No rich text, SVG or template input |
| V1.3.6 | Pass | Outbound calls go only to configured hosts: the Api address (https check), the OIDC authority (https outside Development) and Keycloak endpoints from discovery |
| V1.3.7 to V1.3.11 | N/A | No templates, JNDI, memcache, format strings from input or mail |
| V1.4 | Pass | Managed code only; no `unsafe` in `src/` |
| V1.5.1 | N/A | No XML input |
| V1.5.2 | Pass | System.Text.Json with typed models; Admin body: `UnmappedMemberHandling.Disallow`, no duplicate properties, `MaxDepth 4`, 8 KiB (`AdminJson.cs`, `AdminEndpoints.cs`); `TenantIdJsonConverter` caps at 128 bytes |

## V2 Validation and business logic

| Id | Result | Evidence |
| --- | --- | --- |
| V2.1.1 | Pass | Validation rules are in the requirements per issue (`docs/requirements/phase-0/*.md`) and in the Admin body contract (`docs/architecture/admin-api.md`) |
| V2.1.2, V2.1.3 | Pass | Business limits are documented: `Money.MaxAllocationParts`, one trial per tenant, one active override per feature (`docs/requirements/phase-0/entitlements-module.md`) |
| V2.2.1 | Pass | Allow-list parsing: `TenantId` (`D` form, not empty), `FeatureKey` (catalogue), `DomainError.Code` (pattern), Admin route ids, strict JSON |
| V2.2.2 | Pass | All validation runs server-side in the Api; the SPA holds no rules that matter |
| V2.2.3 | Pass | Override expiry after start; trial window from `IClock` (`EntitlementsDomainTests`) |
| V2.3.1 | Pass | No multi-step user flow yet beyond OIDC (state + PKCE) |
| V2.3.2 | Pass | `MaxAllocationParts`, the 8 KiB body limit, one trial, unique override (`MoneyAllocationBoundaryTests`, `EntitlementsTrialTests`) |
| V2.3.3 | Pass | One explicit transaction per admin command, audit in the same transaction (ADR-0013; `EntitlementsAuditAtomicityTests`) |
| V2.3.4 | Pass | Unique indexes plus a single 23505 re-read for membership, trial and override (`TenantMembershipGateRaceTests`, `GrantOverrideRaceRecoveryTests`) |
| V2.4.1 | **Fail** | No rate limiter or anti-automation in the BFF or the Api. Keycloak brute-force protection covers passwords only. **C-09** |

## V3 Web frontend security

| Id | Result | Evidence |
| --- | --- | --- |
| V3.2.1 | Pass | Reserved prefixes return 404 with an empty body; the SPA fallback serves only `index.html` for GET and HEAD; `ServeUnknownFileTypes=false` (`SpaHosting.cs`, `SpaHostingTests`) |
| V3.2.2 | Pass | React text nodes; static rule bans HTML sinks |
| V3.3.1 | Pass | `__Host-decisya-session`, `__Host-` antiforgery and XSRF cookies, `Secure` always (`CookieOptionsSetup.cs`, `Program.cs` antiforgery options) |
| V3.3.2 | Pass | Session and antiforgery cookies `SameSite=Strict`; OIDC correlation and nonce `Lax` (required for the callback) |
| V3.3.3 | Pass | `__Host-` prefix, no `Domain`, `Path=/` |
| V3.3.4 | Pass | Session cookie `HttpOnly`. The XSRF request-token cookie is readable by design (double submit) and carries no session |
| V3.4.1 | **Fail (Low)** | `UseHsts()` outside Development with framework defaults: 30 days, no `includeSubDomains`. 5.0 asks for at least one year. Low, #83 (or Caddy in #29) |
| V3.4.2 | Pass | No CORS policy registered anywhere, so no cross-origin read is allowed |
| V3.4.3 | Pass | Strict CSP, no `unsafe-*`, `object-src 'none'`, `base-uri 'none'` (`SecurityHeaders.cs`) |
| V3.4.4, V3.4.5, V3.4.6 | Pass | `nosniff`, `Referrer-Policy: no-referrer`, `frame-ancestors 'none'` plus `X-Frame-Options: DENY`; always overwritten, also on proxied responses (`SpaProxiedResponseTests`) |
| V3.5.1 | Pass | Antiforgery on every non-safe verb for `/bff` and `/api` (`AntiforgeryCheck.cs`; `AntiforgeryTests`, `ApiAntiforgeryTests`, `AdminApiAntiforgeryTests`) |
| V3.5.2 | Pass | No reliance on CORS preflight; antiforgery is the control |
| V3.5.3 | Pass | State changes only on POST, PUT, DELETE; no method override (`ApiBoundaryTests`) |
| V3.5.4 | Pass | SPA, BFF endpoints and the proxied Api share one origin by design (ADR-0003); Keycloak is a separate origin |
| V3.5.5 | N/A | No `postMessage` |
| V3.7.1 | Pass | No deprecated client technology |
| V3.7.2 | Pass | Post-login redirect only to local URLs (`ReturnUrlValidator`); logout redirect only to the Authority's end-session endpoint (`EnsureEndSessionLocation`) |

## V4 API and web service

| Id | Result | Evidence |
| --- | --- | --- |
| V4.1.1 | Pass | Minimal API JSON and ProblemDetails set content types with charset; Admin requires a JSON content type and UTF-8 |
| V4.1.2 | N/A (dev) | No plaintext user-facing listener is meant for production; the edge is #29 |
| V4.1.3 | Pass | BFF replaces `X-Forwarded-*` and drops `Forwarded`; the Api uses neither (no `UseForwardedHeaders`) |
| V4.2.1 | Pass | Kestrel and YARP framing; the BFF rejects upgrades on `/api` (`UpgradeRejectionMiddleware.cs`). Re-check with the #29 edge |
| V4.3 | N/A | No GraphQL |
| V4.4 | N/A | No WebSocket; upgrade rejected |

## V5 File handling

N/A: there is no file upload, import or download in Phase 0. The storage issue (ADR-0007) must assess V5 in its G3.

## V6 Authentication (L3)

| Id | Result | Evidence |
| --- | --- | --- |
| V6.1.1 | Pass | Brute-force settings documented and pinned (`RealmConfigurationTests`; `failureFactor` 5, wait up to 900 s) |
| V6.1.2 | Fail | No context-specific word list. Part of **C-02** |
| V6.1.3 | Pass | One pathway: Keycloak hosted login via the BFF. `admin-cli` has no direct grant; no other client has the Api audience (`RealmConfigurationTests`) |
| V6.2.1 | Pass | `length(12)` |
| V6.2.2, V6.2.3 | Pass (delegated) | Keycloak account console; password change needs re-authentication |
| V6.2.4 | **Fail** | No common-password check (`passwordPolicy` has no blacklist). **C-02** |
| V6.2.5 | Pass | No composition rules |
| V6.2.6 to V6.2.8 | Pass (delegated) | Keycloak login form |
| V6.2.9 | Pass | `maxLength(128)` |
| V6.2.10 | Pass | No forced periodic change |
| V6.2.11 | **Fail** | Only `notUsername` and `notEmail`. **C-02** |
| V6.2.12 | **Fail** | No breached-password check. **C-02** |
| V6.3.1 | Pass | `bruteForceProtected: true` |
| V6.3.2 | Pass (dev only) | Seeded `dev-*` users exist only in the dev realm; production realm without users is **C-03** |
| V6.3.3 | **Fail** | MFA is optional (`CONFIGURE_TOTP`), not enforced. **C-02** (all users), **C-01** (platform-admin) |
| V6.3.4 | Pass | No weaker pathway (no direct grant, no other client with the Api audience) |
| V6.3.5 | Fail (Low) | No notification of suspicious logins; needs Keycloak SMTP. #83 |
| V6.3.6 | Pass | Email is not an authentication mechanism |
| V6.3.7 | Fail (Low) | No notification after credential changes; needs SMTP. #83 |
| V6.3.8 | Pass (delegated) | Keycloak gives one generic error for a bad username or password |
| V6.4.1 | Pass (dev only) | Dev passwords come from a parameter; production users → #29 (C-03) |
| V6.4.2 | Pass | No hints or security questions |
| V6.4.3 | N/A | `resetPasswordAllowed: false` |
| V6.4.4 to V6.4.6 | N/A | MFA not enforced yet (C-02) |
| V6.5 | Pass (delegated) | TOTP through Keycloak defaults when enrolled; re-assess when MFA is enforced (C-02) |
| V6.6 | N/A | No out-of-band authenticators |
| V6.7 | N/A | No certificate or device authentication |
| V6.8.1 | Pass | One issuer. Memberships keyed by `sub` (S-4: add the issuer before a second IdP, Low, #83) |
| V6.8.2 | Pass | ID tokens and logout tokens are signature-checked (OIDC handler, `LogoutTokenValidator.cs`) |
| V6.8.3 | N/A | No SAML |
| V6.8.4 | **Fail** | `/api/admin` does not check `acr`/`amr`. **C-01** |

## V7 Session management (L3)

| Id | Result | Evidence |
| --- | --- | --- |
| V7.1.1 | Pass | 10 h absolute BFF session = Keycloak `ssoSessionMaxLifespan` 36000; idle 1800 s enforced through refresh failure (`docs/architecture/bff-session.md` D2, #19 B-1) |
| V7.1.2 | Fail (Low) | No documented limit on concurrent sessions per account. #83 |
| V7.1.3 | Pass | Federated lifecycle documented: RP-initiated and back-channel logout, refresh rotation (`docs/architecture/bff-session.md`, `bff-api-forwarding.md`) |
| V7.2.1 | Pass | Session validated at the BFF; tokens validated at the Api |
| V7.2.2 | Pass | Dynamic session keys, never static secrets |
| V7.2.3 | Pass | 256-bit CSPRNG session key (`RedisTicketStore.cs:34,267`) |
| V7.2.4 | Pass | A fresh key on every sign-in; the old one stops working (`SessionFixationGuard`, `SessionFixationTests`) |
| V7.3.1 | Pass | Keycloak idle 1800 s ends the session at the next refresh (`TokenRefreshTests`, `invalid_grant` → sign-out) |
| V7.3.2 | Pass | `ExpireTimeSpan` 10 h, `SlidingExpiration=false` (`CookieOptionsSetup.cs`) |
| V7.4.1 | Pass | Logout deletes the ticket and ends the Keycloak session server-side (`LogoutTests`) |
| V7.4.2 | Pass with note (Low) | A disabled user's session ends at the next refresh (≤ 300 s + 60 s skew), not at once. #83 |
| V7.4.3 | Pass (delegated) | Keycloak account console signs out other devices; back-channel logout reaches the BFF (`BackchannelLogoutTests`) |
| V7.4.4 | Pass | Sign-out on every SPA page (`shell.spec.ts`) |
| V7.4.5 | Pass (delegated) | Keycloak admin console ends sessions; back-channel logout |
| V7.5.1 | Pass (delegated) | Account attributes change only in Keycloak, which re-authenticates |
| V7.5.2 | Pass (delegated) | Keycloak account console "Device activity" |
| V7.5.3 | **Fail** | No step-up before cross-tenant admin writes. **C-01** |
| V7.6.1 | Pass | BFF lifetime aligned with Keycloak's; refresh failure ends the BFF session |
| V7.6.2 | Pass | A session starts only from an explicit `/bff/login`; no silent `prompt=none` |

## V8 Authorization

| Id | Result | Evidence |
| --- | --- | --- |
| V8.1.1 | Pass | ADR-0001, ADR-0012, `docs/architecture/admin-api.md`, tenancy roles |
| V8.1.2 | Pass | Field-level: DTOs are fixed and minimal (`WhoAmIResponse`, `MemberDto`, `CapabilitiesResponse`) |
| V8.2.1 | Pass | Fallback policy on every endpoint; anonymous set exactly `/health`, `/alive` in Development (`FallbackPolicyTests`, `EndpointAuthorizationTests`); admin group policy |
| V8.2.2 | Pass | Tenant from the validated claim only; global filter and write guard; membership gate; admin target from the route, re-checked, own context per command (`TenancyIsolationTests`, `EntitlementsIsolationTests`, `CrossTenantReadBackTests`) |
| V8.2.3 | Pass | `Disallow` on unmapped members; `tenantId` in a body is a 400 (`AdminRequestBodyTests`) |
| V8.3.1 | Pass | All decisions in the Api; the SPA manifest is display-only (ADR-0008) |
| V8.4.1 | Pass | Cross-tenant only through `[AllowCrossTenant]` handlers, audited, admin-only (`CrossTenantAuditRule`, `AuditRuleTests`, `AdminAuthorizationTests`) |

## V9 Self-contained tokens

| Id | Result | Evidence |
| --- | --- | --- |
| V9.1.1 | Pass | `RequireSignedTokens`, `ValidateIssuerSigningKey` (`JwtBearerOptionsSetup.cs`); logout token likewise |
| V9.1.2 | Pass | `ValidAlgorithms` = RS256, ES256 (Api and logout token) |
| V9.1.3 | Pass | Keys only from the authority's JWKS over https outside Development |
| V9.2.1 | Pass | `ValidateLifetime`, `RequireExpirationTime`, 60 s skew; realm lifespan 300 s |
| V9.2.2 | Pass | `typ` must equal `Bearer` (`OnTokenValidated`) |
| V9.2.3 | Pass | `ValidAudience = decisya-api` |
| V9.2.4 | Pass | Only `decisya-bff` carries the Api audience mapper (`RealmConfigurationTests`); `azp` pin is Low (#83 B-1) |

## V10 OAuth and OIDC

| Id | Result | Evidence |
| --- | --- | --- |
| V10.1.1 | Pass | Tokens stay in the BFF ticket; only the access token goes to the Api |
| V10.1.2 | Pass | OIDC handler state, nonce and PKCE bind the response to the session |
| V10.2.1 | Pass | PKCE S256 required by the realm and used by the BFF |
| V10.2.2 | N/A | One authorization server |
| V10.3.1 | Pass | `aud` and `typ` checks |
| V10.3.2 | Pass | Decisions from access-token claims only (`CallerIdentity.cs`) |
| V10.3.3 | Pass | One issuer; `sub` is stable in Keycloak (S-4 Low before a second issuer) |
| V10.3.4 | **Fail** | `acr`/`amr` not enforced for admin. **C-01** |
| V10.4.1 to V10.4.6 | Pass (delegated) | Exact redirect URIs, PKCE required, code flow only, no direct grants, refresh rotation with `refreshTokenMaxReuse` 0 (`RealmConfigurationTests`) |
| V10.4.8 | Pass | Refresh bounded by `ssoSessionMaxLifespan`; `offline_access` never issued |
| V10.4.9 | Pass (delegated) | Account console sign-out revokes refresh tokens |
| V10.4.10 | Pass | Confidential client with a generated secret (`private_key_jwt` review is #29) |
| V10.5.1 to V10.5.4 | Pass | Nonce, `sub`, issuer and `aud` checks by the OIDC handler |
| V10.5.5 | Pass | Back-channel logout validated, 16 KB cap, form field only (`BackchannelLogoutTests`) |
| V10.6, V10.7 | N/A | Keycloak is the provider; first-party client, no consent screen |

## V11 Cryptography

| Id | Result | Evidence |
| --- | --- | --- |
| V11.1.1 | Pass with link | Key inventory and owners: `system-baseline.md` "Key and secret inventory". Rotation procedures: **C-04** (hash key), **C-05** (key ring), **C-20** (realm signing key, client secret, Postgres, Keycloak-admin and Redis passwords) |
| V11.1.2 | Pass | Same inventory (closed in #27) |
| V11.2.1 | Pass | Platform primitives only: Data Protection, `HMACSHA256.HashData`, `RandomNumberGenerator`, IdentityModel |
| V11.2.2, V11.2.3 | Pass | Algorithms configurable (`ValidAlgorithms`); 128-bit+ security throughout |
| V11.3.1 to V11.3.3 | Pass | Framework Data Protection (authenticated encryption); no custom ciphers |
| V11.4.1 to V11.4.4 | Pass | SHA-256 family; passwords only in Keycloak; SCRAM-SHA-256 for DB roles |
| V11.5.1 | Pass | `RandomNumberGenerator` for session keys and the dev hash key |
| V11.6.1 | Pass | RS256/ES256 |

## V12 Secure communication

| Id | Result | Evidence |
| --- | --- | --- |
| V12.1.1, V12.1.2 | N/A (dev) | Edge TLS is #29 (Caddy). Re-assess there |
| V12.2.1, V12.2.2 | Pass with link | https enforced outside Development for the Authority and the Api address (`BffOptionsEnvironmentValidator.cs`, `ApiJwtOptionsEnvironmentValidator.cs`, final-URI check). Public certificates: #29 |
| V12.3.1, V12.3.3 | **Fail (deployment)** | Redis, Postgres and Keycloak-to-DB hops are plaintext; the production topology is unspecified. **C-11** |
| V12.3.2, V12.3.4 | Pass | Default certificate validation; no custom validators |

## V13 Configuration

| Id | Result | Evidence |
| --- | --- | --- |
| V13.1.1 | Pass | Communication needs: `system-baseline.md` data flow (closed in #27) |
| V13.2.1 | Fail (deployment) | Redis and Postgres authenticate with generated passwords in dev; production → **C-11** |
| V13.2.2 | Pass with link | Api roles DML-only per schema, audit INSERT only (`MigrationRunner.cs`); migrator superuser → **C-06** |
| V13.2.3 | Pass (dev only) | All passwords generated; production realm users → **C-03** |
| V13.2.4 | Pass | Outbound only to configured Keycloak and Api endpoints |
| V13.2.5 | Pass | BFF serves only `/assets` from the web root and the constant `index.html` |
| V13.3.1 | **Fail (deployment)** | Dev secrets live in user-secrets and Aspire parameters; production secret store and rotation: **C-20** (#29); C-04 covers the hash key |
| V13.3.2 | Pass with link | The Api gets only its two connection strings (`AppHostConfigurationTests`); production access control → #29 |
| V13.4.1 | Pass | No source-control metadata served (static files only from the web root) |
| V13.4.2 | Fail (deployment) | The Development environment turns on the exception page and health endpoints; pinning a non-Development environment is **C-04** |
| V13.4.3 | Pass | No directory browsing |
| V13.4.4 | Pass | TRACE is not routed (`/api` methods allow-list in `ProxyConfiguration.cs`) |
| V13.4.5 | Pass | Health endpoints only in Development; no OpenAPI UI |

## V14 Data protection

| Id | Result | Evidence |
| --- | --- | --- |
| V14.1.1, V14.1.2 | Pass | Classification table in `system-baseline.md` (closed in #27) |
| V14.2.1 | Pass | No sensitive data in URLs (tenant GUIDs in admin routes are ids, not secrets); query values redacted in telemetry |
| V14.2.2 | Pass | `Cache-Control: no-store` on every Api response (`NoStoreResponses.cs`) and as the BFF default |
| V14.2.3 | Pass | No third-party scripts, trackers or vendor telemetry (CSP `'self'`, NFR-12 tests) |
| V14.2.4 | Fail (linked) | No retention for logs and traces; audit actor retention deferred. **C-04**, `system-baseline.md` "tenant_id in logs" |
| V14.3.1 | Pass | Logout navigates away; the SPA stores nothing client-side |
| V14.3.2 | Pass | `no-store` |
| V14.3.3 | Pass | No `localStorage` or `sessionStorage` (`static-rules.test.ts`, `login-flow.spec.ts`) |

## V15 Secure coding and architecture

| Id | Result | Evidence |
| --- | --- | --- |
| V15.1.1 | Fail (Low) | No documented remediation timeframe; CI fails on High and Critical. #83 |
| V15.1.2 | Fail (linked) | No SBOM; #29 (in its title) |
| V15.1.3 | Pass | Costly paths documented: Admin body limits, refresh lock (NFR-25), allocation cap |
| V15.2.1 | Pass | NuGet and npm scans clean on 2026-10-03; Dependabot (npm with a 7-day cool-down) |
| V15.2.2 | **Fail** | No rate limiting on costly or unauthenticated paths. **C-09** |
| V15.2.3 | Pass | No vendor SDKs; health and dashboard Development only |
| V15.3.1 | Pass | Minimal DTOs |
| V15.3.2 | Pass | The BFF calls only configured endpoints and checks the logout redirect target |
| V15.3.3 | Pass | Strict JSON with `Disallow`; no model binding to entities |
| V15.3.4 | Pass | The Api trusts no forwarded headers |
| V15.3.5 | Pass | `TenantId`, `FeatureKey`, `Money` value types; no `IParsable` binding for `TenantId` (review 32) |
| V15.3.6 | Pass | No dynamic object merging in the SPA |
| V15.3.7 | Pass | Duplicate JSON properties rejected; route values typed |

## V16 Security logging and error handling

| Id | Result | Evidence |
| --- | --- | --- |
| V16.1.1 | Pass | Log inventory in `system-baseline.md` (closed in #27) |
| V16.2.1 | Pass | `timestamp`, `level`, `category`, `trace_id`, `span_id`, `tenant_id`, hashed `user_id` (`DecisyaJsonConsoleFormatter.cs`) |
| V16.2.2 | Pass | UTC timestamps; NodaTime `IClock` |
| V16.2.3 | Pass | Exactly two sinks (`LoggingProvidersTests`); destinations listed in the inventory |
| V16.2.4 | Pass | JSON lines; OTLP |
| V16.2.5 | Pass with residual | Masking core, `[Sensitive]`, deny-list, shape masking; residual **C-12** |
| V16.3.1 | **Fail** | No authentication events logged (Keycloak events off; JwtBearer failures below the log floor; no BFF sign-in event). **C-10** |
| V16.3.2 | Pass | Authorization refusals logged with fixed reason codes (`CallerContextMiddlewareLog.cs`, `AdminLog.cs`, membership gate) |
| V16.3.3 | Pass | Admin body rejections logged with reason codes (`AdminLog.cs:15`); back-channel logout rejections (`BffLog`) |
| V16.3.4 | Pass | Ticket store, refresh and Keycloak logout failures logged and counted (`BffLog.cs`, `BffTelemetry.cs`); masking errors counted |
| V16.4.1 | Pass | `Utf8JsonWriter` with the default encoder; log-injection tests (`DecisyaJsonConsoleFormatterTests`) |
| V16.4.2, V16.4.3 | Fail (linked) | Dev logs on the loopback dashboard only; collector access control, transport and integrity are **C-04** |
| V16.5.1 | Pass | Generic ProblemDetails with `traceId`; 5xx bodies replaced at the BFF; `IncludeErrorDetails=false` (`ExceptionHandlingTests`, `ForbiddenResponseTests`) |
| V16.5.2 | Pass | Redis failure fails closed within 1.5 s; Keycloak logout failure keeps the local logout (`RedisFailureTests`, `LogoutTests`) |
| V16.5.3 | Pass | Entitlement evaluation never turns an exception into allow (`EntitlementsFailClosedTests`, `CapabilitiesEndpointTests`) |

## V17 WebRTC

N/A: not used.
