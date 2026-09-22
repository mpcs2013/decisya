# 0005. Hybrid modular monolith with Wolverine

- Status: Proposed
- Date: 2026-09-20
- Deciders: Marco
- Tags: module

## Context and problem statement

Modules must stay independently evolvable without the operational cost of microservices.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **Layered monolith** — boundaries erode.
2. **Microservices** — too much operations for one person.
3. **Modular monolith, Module + Module.Contracts, schema-per-module, Wolverine with Postgres transport** — proven in PortfolioTracker.

## Decision outcome

Chosen option: **Modular monolith with Wolverine**

### Consequences

- Good: Compile-time boundaries; no external broker
- Bad: Cross-module reporting needs read models or Contracts queries
- Enforced by: NetArchTest: no implementation-to-implementation references; EF schema per module
