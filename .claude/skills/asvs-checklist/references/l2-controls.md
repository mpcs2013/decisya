# ASVS 5.0 Level 2 – chapter checklist

Chapter numbers follow **ASVS 5.0** (May 2025). They differ from ASVS 4.0: for example session management moved from V3 to V7, and V3 is now web frontend security. Cite controls as `V<chapter>.<section>.<requirement>` from the official 5.0 text; expand a chapter from the official document the first time a change touches it. A project narrows or raises this list in its security-reviewer addendum (for example, Level 3 for some chapters); the addendum may add checks, never drop one.

| Chapter | What to check |
| --- | --- |
| V1 Encoding and sanitization | No string-built URLs, headers or commands from request data; output encoding where the project renders data |
| V2 Validation and business logic | Inputs validated at the boundary, size limits, anti-automation on sensitive operations |
| V3 Web frontend security | Security headers, no caching of sensitive responses, cookie flags seen from the browser, no secret reachable from scripts |
| V4 API and web service | Method and content-type allow-lists, upstream errors not leaked, rate limits |
| V5 File handling | Uploads and key or certificate files: permissions checked, never served; otherwise N/A |
| V6 Authentication | Strong credential handling, multi-factor where required, brute-force protection |
| V7 Session management | Cookie flags, absolute and idle timeouts, fixation guard, logout ends the session |
| V8 Authorization | Deny by default; object-level and role checks on every handler |
| V9 Self-contained tokens | Algorithm allow-list; issuer, audience, expiry and token type validated; short lifetimes |
| V10 OAuth and OIDC | Confidential clients where possible, PKCE, strict redirect and logout handling |
| V11 Cryptography | No custom cryptography; keys protected; approved algorithms only |
| V12 Secure communication | TLS everywhere outside local development; host name validation kept |
| V13 Configuration | Options fail closed; no development relaxation in production; dependency scanning; secrets never in source or packages |
| V14 Data protection | Sensitive data minimised in caches and at rest; retention and erasure handled |
| V15 Secure coding and architecture | Component boundaries enforced by tests, dependency hygiene, safe defaults, pinned supply chain |
| V16 Security logging and error handling | No token, credential or personal data in any record; generic client errors; no stack traces to clients |
| V17 WebRTC | Not used unless the project says so: N/A |
