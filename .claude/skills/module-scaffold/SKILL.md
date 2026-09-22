---
name: module-scaffold
description: Scaffolds a new Decisya module pair (Modules.<Name> + Modules.<Name>.Contracts) with its own Postgres schema, DbContext, Wolverine handler folders, NetArchTest fixture and integration test project. Use this whenever a new bounded context, module, feature area or "add X to the platform" comes up, even if the user only says "create the tenancy stuff" or "start the entitlements module".
---
# module-scaffold

Creates the standard hybrid-modular-monolith module layout. Never hand-create a module; run this so every module is identical.

## Inputs
- `Name` (PascalCase, singular, e.g. `Tenancy`)
- `schema` (lowercase, e.g. `tenancy`)

## Steps
1. Create projects (CLI shown; in VS 2026 use *Add → New Project → Class Library* with the same names, then edit the csproj):
   ```bash
   dotnet new classlib -n Decisya.Modules.<Name>.Contracts -o src/Modules/<Name>/Decisya.Modules.<Name>.Contracts
   dotnet new classlib -n Decisya.Modules.<Name> -o src/Modules/<Name>/Decisya.Modules.<Name>
   dotnet new xunit -n Decisya.Modules.<Name>.Tests -o tests/Modules/Decisya.Modules.<Name>.Tests
   dotnet sln add src/Modules/<Name>/*/*.csproj tests/Modules/Decisya.Modules.<Name>.Tests/*.csproj
   ```
2. References: `Modules.<Name>` → `Modules.<Name>.Contracts`, `Decisya.SharedKernel`. Contracts references only `SharedKernel`. Tests reference both plus `Decisya.TestInfrastructure`.
3. Folder layout inside `Modules.<Name>`:
   ```
   Domain/          entities (implement ITenantScoped), value objects, domain events
   Application/     Wolverine handlers, validators (FluentValidation), queries
   Infrastructure/  <Name>DbContext (schema "<schema>"), configurations, repositories
   Endpoints/       minimal API group /api/<schema>, authorization policies
   <Name>Module.cs  Add<Name>Module(this IHostApplicationBuilder) registration
   ```
4. `<Name>DbContext : TenantDbContext` with `modelBuilder.HasDefaultSchema("<schema>")` and `MigrationsHistoryTable("__EFMigrationsHistory", "<schema>")`.
5. Register `ActivitySource` and `Meter` named `Decisya.<Name>` (see otel-instrumentation).
6. Add the module to `tests/Decisya.ArchitectureTests/ModuleBoundaryTests.cs` data set so these rules cover it:
   - no reference to any other `Decisya.Modules.*` implementation assembly
   - every `Domain` type that is an entity implements `ITenantScoped`
   - no type in `Domain` references `Microsoft.EntityFrameworkCore`
7. Create `tests/.../<Name>IsolationTests.cs` from `references/isolation-test.md`.
8. Run `dotnet build -warnaserror` and `dotnet test --filter Category=Architecture`; report exact errors if any.

## Done when
Build is green, the architecture test includes the new module, and the isolation test exists (it may be skipped until the first entity lands).
