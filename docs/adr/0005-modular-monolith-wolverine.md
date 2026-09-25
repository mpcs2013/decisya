# 0005. Hybrid modular monolith with Wolverine

- Status: Accepted
- Date: 2026-09-20
- Deciders: Marco
- Tags: module

## Context and problem statement

Modules must stay independently evolvable without the operational cost of microservices.

"Hybrid" means: all modules run in one process against one database; a module reads another module only through synchronous query interfaces in its Contracts assembly and reacts to it only through Wolverine messages; because the Contracts assembly and the message are the only coupling, a module can later move to its own host without changing its callers.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **Layered monolith** — boundaries erode.
2. **Microservices** — too much operations for one person.
3. **Modular monolith, Module + Module.Contracts, schema-per-module, Wolverine with Postgres transport** — the author has shipped this shape before (PortfolioTracker, outside this repository).
4. **MediatR + MassTransit** — both moved to commercial licences in 2025; Wolverine covers mediator and durable messaging in one MIT library (WolverineFx licence confirmed MIT on nuget.org by Marco, 2026-09-25).

## Decision outcome

Chosen option: **Modular monolith with Wolverine: Modules.<Name> + Modules.<Name>.Contracts, one Postgres schema and one DB role per module, Wolverine as in-process mediator and as durable bus over the Postgres transport with the EF Core transactional outbox**

### Consequences

- Good: Compile-time boundaries; no external broker
- Bad: Cross-module reporting needs read models or Contracts queries
- Bad: DDL is owned by a migrator process with its own role: EF migrations and Wolverine message storage are created there; the API runs with DML-only roles and Wolverine auto-provisioning off outside local dev (CLAUDE.md least privilege)
- Bad: The tenant crosses the bus on the Wolverine envelope; handler middleware sets the tenant before any DbContext resolves; a message without a tenant fails unless its handler is [AllowCrossTenant] (ADR-0001)
- Bad: Wolverine storage lives in its own schema; each module role gets only the grants it needs there
- Enforced by: NetArchTest in each module's tests and Decisya.ArchitectureTests (#22): no module references another module's implementation assembly; Contracts assemblies reference only SharedKernel and other Contracts and contain no EF Core or handler types. EF model test: every entity maps to its module's schema. Testcontainers role test: a module role cannot read another module's schema; the API role cannot CREATE, ALTER or DROP. Wolverine test: a message without a tenant is rejected
