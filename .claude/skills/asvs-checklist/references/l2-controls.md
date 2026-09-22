# ASVS 5.0 L2 – Decisya curated controls (expand per chapter on first touch)

- V2 Authentication: Keycloak-managed; verify MFA enforcement, password policy delegated, no local password storage.
- V3 Session management: BFF cookie flags, server-side ticket store, sliding/absolute timeouts, logout ends Keycloak session, refresh rotation.
- V4 Access control: policy per endpoint, object-level checks on every id, tenant filter, admin role separation, deny by default.
- V5 Validation and encoding: FluentValidation schemas, size limits, file type allow-list, no reflection-based binding of ids.
- V6 Cryptography: TLS 1.2+, no custom crypto, secrets from environment.
- V7 Error handling and logging: generic ProblemDetails, JSON logs, trace id, `[Sensitive]` masking, no stack traces to clients.
- V8 Data protection: encryption at rest, retention schedule, erasure job, export.
- V9 Communication: HSTS, secure cookies, internal API unreachable from the internet.
- V12 Files: upload size/type limits, stored outside web root (object storage), scanned, served with `Content-Disposition`.
- V13 API: JSON only, CORS locked to the BFF origin, rate limiting on auth and AI endpoints.
- V14 Configuration: dependency scanning, headers (CSP, X-Content-Type-Options, Referrer-Policy), no debug in prod.
