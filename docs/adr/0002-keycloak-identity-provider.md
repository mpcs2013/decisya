# 0002. Keycloak as sole identity provider

- Status: Proposed
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

Chosen option: **Keycloak, realm `decisya`, confidential client `decisya-bff`**

### Consequences

- Good: No credential storage in Decisya; MFA and policies configured, not coded
- Bad: One more container to run and back up
- Enforced by: Realm export committed under deploy/keycloak; integration tests use Testcontainers.Keycloak
