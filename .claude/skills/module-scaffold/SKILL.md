---
name: module-scaffold
description: "Scaffolds a new Decisya module pair (Modules.<Name> + Modules.<Name>.Contracts) with its own Postgres schema, DbContext, Wolverine handler folders, NetArchTest fixture and integration test project. Use this whenever a new bounded context, module, feature area or \"add X to the platform\" comes up, even if the user only says \"create the tenancy stuff\" or \"start the entitlements module\"."
---
# module-scaffold

Creates the standard hybrid-modular-monolith module layout. Never hand-create a module; run this so every module is identical. Two phases: **base** works today; **tenancy** needs the tenant infrastructure and stops cleanly until it exists.

## Inputs
- `Name`: PascalCase, singular (e.g. `Tenancy`)
- `schema`: lowercase (e.g. `tenancy`)

## Phase 1: base (projects, registration, boundary tests)

Run every command from the repository root. In VS 2026 use *View → Terminal* (Developer PowerShell) for the script lines; they have no UI equivalent.

| Step | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 1. Generate the projects from `assets/` | *View → Terminal*: `python .claude/skills/module-scaffold/scripts/scaffold.py <Name> <schema>` | `python .claude/skills/module-scaffold/scripts/scaffold.py <Name> <schema>` |
| 2. Add them to the solution | *Solution Explorer → right-click solution → Add → Existing Project…*, pick the three new `.csproj` files | `dotnet sln add src/Modules/<Name>/Decisya.Modules.<Name>.Contracts src/Modules/<Name>/Decisya.Modules.<Name> tests/Modules/Decisya.Modules.<Name>.Tests` |
| 3. Build | *Build → Rebuild Solution* (warnings are errors via `Directory.Build.props`) | `dotnet build -warnaserror` |
| 4. Run the boundary tests | *Test Explorer → Group by Traits → Category: Architecture → Run* | `dotnet test --no-build --filter-trait "Category=Architecture" --minimum-expected-tests 2` |

What step 1 creates:
- `Decisya.Modules.<Name>.Contracts`: references only `Decisya.SharedKernel`.
- `Decisya.Modules.<Name>`: references its Contracts and SharedKernel; `<Name>Module.cs` has `Add<Name>Module(this IServiceCollection)` and the module's `ActivitySource` and `Meter` named `Decisya.<Name>` (see `otel-instrumentation`).
- `Decisya.Modules.<Name>.Tests`: xunit.v3 on Microsoft.Testing.Platform, AwesomeAssertions, NetArchTest (same shape as `tests/Decisya.SharedKernel.Tests`), with two `Category=Architecture` tests: no reference to another module's implementation, and no direct `NodaTime.SystemClock` use.

Do **not** use `dotnet new classlib` or `dotnet new xunit`: they add `Class1.cs`, repeat properties `Directory.Build.props` already sets, and the xunit template is xUnit v2 on VSTest with pinned versions (NU1008 under Central Package Management).

Create these folders in `Decisya.Modules.<Name>` as the first types land (git does not keep empty folders):
```
Domain/          entities (implement ITenantScoped), value objects, domain events
Application/     Wolverine handlers, validators (FluentValidation), queries
Infrastructure/  <Name>DbContext (schema "<schema>"), configurations, repositories
Endpoints/       minimal API group /api/<schema>, authorization policies
```

## Phase 2: tenancy (DbContext, architecture registration, isolation test)

| Step | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 5. Check prerequisites; **stop if any are missing** and report the listed issues | *View → Terminal*: `python .claude/scripts/prereqs.py module-scaffold --phase tenancy` | `python .claude/scripts/prereqs.py module-scaffold --phase tenancy` |

Then:
6. `Infrastructure/<Name>DbContext : TenantDbContext` with `modelBuilder.HasDefaultSchema("<schema>")` and `MigrationsHistoryTable("__EFMigrationsHistory", "<schema>")`.
7. Add the module to the data set in `tests/Decisya.ArchitectureTests/ModuleBoundaryTests.cs` so these rules cover it: every `Domain` entity implements `ITenantScoped`; no `Domain` type references `Microsoft.EntityFrameworkCore`.
8. Add a project reference from the module tests to `Decisya.TestInfrastructure`, then create `<Name>IsolationTests.cs` with the `isolation-test` skill.
9. Build and run both lanes:

| Visual Studio 2026 | CLI |
| --- | --- |
| *Build → Rebuild Solution*, then *Test Explorer → Run All* (Docker Desktop running) | `dotnet build -warnaserror` then `dotnet test --no-build --filter-trait "Category=Architecture"` and `dotnet test --no-build --filter-trait "Category=Integration"` |

## Output
The new projects, listed in the manifest (`docs/ai/pipeline/<n>.md`) under G4 with the build and test summary.

## Done when
- Phase 1: build green, both boundary tests pass.
- Phase 2: build green, the module is in `ModuleBoundaryTests`, and the isolation test exists (it may be skipped until the first entity lands). If phase 2's prerequisites are missing, report them and stop; the scaffold is still valid at phase 1.
