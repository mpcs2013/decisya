# 0009. Target .NET 11 RC1 (go-live) and Aspire 13.5 from day one

- Status: Proposed
- Date: 2026-09-20
- Deciders: Marco
- Tags: hosting, tooling

## Context and problem statement

.NET 11 RC1 shipped 8 September 2026 with a go-live licence; GA is scheduled for 10 November 2026. Aspire 13.5 (18 August 2026) ships templates that target the .NET 11 preview. Phase 0 will not finish before GA, so starting on .NET 10 LTS means an upgrade mid-phase.

## Decision drivers

- Avoid a framework upgrade in the middle of Phase 0
- .NET 11 is STS (supported ~24 months); .NET 12 LTS lands November 2027
- Some third-party packages (Wolverine, Npgsql EF provider, NetArchTest, Testcontainers) may not yet publish net11 builds

## Considered options

1. **.NET 10 LTS now, upgrade to 11 after Phase 0** — safest packages; guaranteed upgrade work mid-project.
2. **.NET 11 RC1 now (go-live), move to GA on 10 Nov** — templates and Aspire 13.5 already target it; RC → GA is a version bump.
3. **Wait for GA before starting** — loses seven weeks.

## Decision outcome

Chosen option: **.NET 11 RC1 with `rollForward: latestFeature` and `allowPrerelease: true`**, moving to GA in the week of 10 November 2026 by editing `global.json` and `Directory.Packages.props` only.

### Consequences

- Good: no mid-phase framework migration; Aspire 13.5 templates and `AspireUseCliBundle` work out of the box.
- Bad: Visual Studio 2026 **Insiders** channel is required for .NET 11 until GA; any package without a net11 build runs on its net10 asset (usually fine) or blocks the issue — record each such package and its chosen version in the table below.
- Bad: .NET 11 is STS; plan the .NET 12 LTS move for Q4 2027.
- Enforced by: `global.json`, `Directory.Build.props` (`net11.0`), CI `dotnet-quality: preview` until GA.

## Package compatibility log (fill during issues 0.01–0.04)

| Package | Version pinned | net11 asset? | Note |
| --- | --- | --- | --- |
| Aspire.* | 13.5.0 | yes | never mix with 13.4.x |
| Microsoft.EntityFrameworkCore.* | | | expect 11.0.0-rc.1 |
| Npgsql.EntityFrameworkCore.PostgreSQL | | | check for an 11 preview; else latest 10.x |
| WolverineFx | | | net10 asset expected to load on net11 |
| NetArchTest.Rules | | | netstandard |
| Testcontainers.* | | | netstandard/net8 |
