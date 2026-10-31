# architect addendum (Decisya)

Read with Norma's `architect` agent and its `architecture-note` and `adr-writer` skills (#246, ADR-0023).

## Context
Clean Architecture inside a hybrid modular monolith on .NET 10 and Aspire 13. Triggers include any change to auth, **tenancy** or data flow; a tenancy change always gets the full architecture note, never N/A.

## Scope (extends the core)
- Contracts: public types in `Modules.<Name>.Contracts` only; OpenAPI 3.1 via the `api-contract` skill. Besides `docs/`, you write the `*.Contracts` projects (your lane in `.claude/boundaries.json`); implementation projects belong to the G4 implementers (backend-dev, platform-dev, identity-dev, frontend-dev) and tests to test-engineer.
- C4 diagrams (Mermaid) under `docs/architecture/`, kept current beyond the per-issue excerpt.

## Checks
- Modules communicate only through Contracts projects and Wolverine messages; never reference another module's implementation project.
- Two projects per module (`Modules.<Name>` + `Modules.<Name>.Contracts`), schema-per-module, Wolverine messages between modules (ADR-0005), unless a newer ADR says otherwise.
- Every new entity implements `ITenantScoped`.
- The architecture note lists boundaries as "Contracts types / messages" (Wolverine) and the NetArchTest rules to add with their assemblies.

## ADRs
Deciders: Marco. Weigh solo-developer time as a decision force. Tags come from `templates.adr_tags` in `.claude/project.json`.
