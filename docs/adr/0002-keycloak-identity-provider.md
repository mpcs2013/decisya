# 0002. Keycloak as sole identity provider

- Status: Accepted
- Date: 2026-09-20
- Deciders: Marco
- Tags: security

## Context and problem statement

The platform needs OIDC, MFA, password policy and admin tooling without paid software.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **ASP.NET Core Identity** — in-process; we would own password storage and MFA UI.
2. **Hosted IdP (Auth0, Entra External ID)** — paid or usage-capped; vendor lock-in.
3. **Keycloak** — free, self-hosted, OIDC/OAuth2 compliant, tenant attribute as a claim.

## Decision outcome

Chosen option: **Keycloak (Apache-2.0), single realm `decisya`, confidential client `decisya-bff` using authorization code with PKCE (S256) and a client secret from environment variables or user-secrets; the tenant reaches tokens as a `tenant_id` claim (mechanism, Keycloak Organizations or a user attribute, decided in #22; realm-per-tenant ruled out); Keycloak stores its data in its own Postgres database and role**

### Consequences

- Good: No credential storage in Decisya; MFA and policies configured, not coded
- Bad: One more container to run and back up
- Bad: Aspire.Hosting.Keycloak is preview-only (13.5.4-preview); accepted for the AppHost, re-checked on each Aspire bump
- Bad: Keycloak's database is part of backup and the restore drill (ADR-0007); Keycloak upgrades re-run the realm tests
- Bad: Keycloak integration tests in the agent sandbox need the Keycloak image added to .devcontainer/engine/images.Dockerfile (ADR-0010) by the first issue that uses them
- Bad: The client secret is a shared secret; switching decisya-bff to private_key_jwt is revisited before the first external tenant
- Enforced by: Realm export under deploy/keycloak with every secret replaced by a placeholder injected from the environment, scanned by gitleaks in CI; a realm-configuration test (Testcontainers.Keycloak, Category=Integration) asserting PKCE S256 on decisya-bff, access-token lifespan ≤ 300 s, RS256/ES256 signing, tenant_id and audience mappers, brute-force detection, OTP and password policy
