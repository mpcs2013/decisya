---
name: keycloak
description: "Rules and known behaviour of Decisya's Keycloak 26 setup: the decisya realm file, secret placeholders, the decisya-bff client, tenant_id, the dev issuer, the admin console, and how to test logins. Use whenever a change touches deploy/keycloak, the realm, tokens, the issuer, OIDC login or the Keycloak container."
---
# keycloak

Decisya uses one realm, `decisya` (ADR-0002), imported from the realm file in `deploy/keycloak/` (only the AppHost, `tests/` and `docs/` may name that file: `RealmExportFileTests` guards against new launch paths). Design: `docs/architecture/keycloak-realm.md`; threats: `docs/security/threat-models/keycloak-realm.md`. Everything below was learned the hard way in #17.

## Realm file
- Hand-written, never a raw export: no key material, no hashed credentials, no generated ids. `RealmExportFileTests` enforces it.
- Secrets appear only as `${DECISYA_BFF_CLIENT_SECRET}` and `${DECISYA_DEV_USER_PASSWORD}`. Keycloak 26 substitutes the plain `${X}` form at import; it does not substitute `${env.X}`.
- An unset variable is not an error: Keycloak silently keeps the literal placeholder text as the secret or password. That is why the AppHost refuses to start when a variable is missing (`RealmSecretRules`), the test fixture resolves both values before starting a container, and `PlaceholderSubstitutionRegressionTests` pins the behaviour. Any new way to start Keycloak needs the same guard.
- Import happens only into an empty database. An existing realm is skipped at every restart, so an edited realm file reaches a dev instance only after the volume reset in GETTING-STARTED §3, which Marco runs; agents never remove a volume. CI always tests the committed file on an empty container.

## Client and tokens
- `decisya-bff` is confidential, requires PKCE S256, allows only the standard code flow, and has one exact redirect URI; never a wildcard. Implicit flow and direct access grants stay off, and `offline_access` is not granted by default.
- Access tokens last at most 300 s, signed RS256; request objects must be signed.
- `tenant_id` is an admin-only user-profile attribute, mapped into the access and ID tokens. A user can never edit it; `TenantSelfEditTests` proves that, and must keep proving it if the mechanism moves to Organizations.
- `sslRequired` is `external` for dev, and never `none`. It is only acceptable because every port is bound to 127.0.0.1.

## Issuer and URLs
- In a real AppHost run on a machine that trusts the .NET dev certificate, Aspire terminates HTTPS for the container: the issuer is `https://localhost:8080/realms/decisya`. Without a trusted certificate the same URLs are `http://`. Testcontainers runs are always `http`.
- Code never hard-codes the scheme or host: take the authority from the Keycloak resource or configuration, and assert only the `/realms/decisya` suffix in tests.

## Admin console and secrets
- The admin console (`/admin/`) and the bootstrap admin password are Marco's. Never read, print or copy the admin password, the client secret or the dev password, and never read the local secret store.

## Testing logins
- Keycloak's login cookies are `Secure`. .NET's `CookieContainer` does not send `Secure` cookies over `http://`, so a Testcontainers login returns 400 "cookie not found" instead of a real result. Use the test-only `SecureCookieRelayHandler` in `Decisya.Identity.Tests`; never copy it into product code.
- A wrong password returns 200 with "Invalid username or password."; assert that text for negative logins, not merely "not a redirect".

## Runnable examples
These tests are the canonical examples; CI runs them, so they cannot drift from the rules above.

| Rule | Example | Run |
| --- | --- | --- |
| Placeholders stay literal when unset | `tests/Decisya.Identity.Tests/PlaceholderSubstitutionRegressionTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-trait "Category=Integration"` |
| Missing variable refuses the start | `tests/Decisya.Identity.Tests/RealmSecretRulesTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"` (no Docker) |
| Realm file rules (no key material, no generated ids) | `tests/Decisya.Identity.Tests/RealmExportFileTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"` |
| Exact redirect URI, no wildcards or http | `tests/Decisya.Identity.Tests/RealmConfigurationTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-trait "Category=Integration"` (Docker) |
| Login flow with the cookie relay, negative logins | `tests/Decisya.Identity.Tests/BffLoginFlowTests.cs`, `SecureCookieRelayHandler.cs` | Integration command above |
| `tenant_id` cannot be self-edited | `tests/Decisya.Identity.Tests/TenantSelfEditTests.cs` | Integration command above |
