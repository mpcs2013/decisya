# 0001. Multi-tenant model, single-instance deployment

- Status: Proposed
- Date: 2026-09-20
- Deciders: Marco
- Tags: hosting

## Context and problem statement

Decisya may be sold as SaaS and also self-hosted. Retrofitting tenancy is the most expensive change possible.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **Single-tenant only** — cheapest to build; blocks SaaS without a rewrite.
2. **Database-per-tenant** — strongest isolation; operationally heavy for one person.
3. **Shared schema with TenantId + query filters** — one deployment, isolation enforced in code and tests.

## Decision outcome

Chosen option: **Shared schema with TenantId, one VPS running Docker Compose from the Aspire manifest**

### Consequences

- Good: SaaS and self-hosted differ only by Keycloak configuration; scale-up is Compose → K3s → managed Kubernetes
- Bad: A missed filter is a data leak; mitigated by architecture tests and two-tenant isolation tests
- Enforced by: NetArchTest rule (every entity implements ITenantScoped); per-module isolation tests
