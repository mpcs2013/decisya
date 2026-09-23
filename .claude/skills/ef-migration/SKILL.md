---
name: ef-migration
description: "Creates an EF Core 10 migration scoped to one module's schema, generates the idempotent SQL script, and documents rollback. Use whenever an entity, configuration or DbContext changes, or when the user says \"add a column\", \"new table\", \"update the schema\" or \"migration\"."
---
# ef-migration

Each module owns its schema and its own migrations history table; never run a migration from another module's context.

## Prerequisites
Run `python .claude/scripts/prereqs.py ef-migration` from the repository root (VS 2026: *View → Terminal*). If anything is missing, report the listed issues and stop.

## Steps

`dotnet-ef` is a local tool pinned in `dotnet-tools.json`; run `dotnet tool restore` once per clone. Paths below are from the repository root.

| Step | Visual Studio 2026 (Package Manager Console) | CLI |
| --- | --- | --- |
| Restore tools | — (PMC uses the EF Core Tools package) | `dotnet tool restore` |
| Add migration | Default project: `Decisya.Modules.<Name>`; `Add-Migration <Verb><Noun> -Context <Name>DbContext -OutputDir Infrastructure/Migrations -StartupProject Decisya.Api` | `dotnet ef migrations add <Verb><Noun> --project src/Modules/<Name>/Decisya.Modules.<Name> --startup-project src/Decisya.Api --context <Name>DbContext --output-dir Infrastructure/Migrations` |
| Idempotent script | `Script-Migration -Idempotent -Context <Name>DbContext -Output deploy/sql/<schema>/<timestamp>.sql` (check where PMC resolves the relative path on first use and correct this line) | `dotnet ef migrations script --idempotent --project src/Modules/<Name>/Decisya.Modules.<Name> --startup-project src/Decisya.Api --context <Name>DbContext -o deploy/sql/<schema>/<timestamp>.sql` |

1. Name migrations `<Verb><Noun>` (`AddInvitations`, `RenameBudgetPeriod`).
2. Review the generated `Up`: no data loss without a two-step migration (add → backfill → drop in a later migration).
3. Amounts are `bigint` minor units plus `char(3)` currency; instants are `timestamptz`; calendar dates are `date`.
4. Every table gets `tenant_id uuid not null` and an index starting with `tenant_id`.
5. Add a rollback note to the migration file header (what `Down` cannot restore).
6. Apply in the integration test fixture (`Database.Migrate()`) and run the integration lane:

| Visual Studio 2026 | CLI |
| --- | --- |
| Docker Desktop running; *Test Explorer → Group by Traits → Category: Integration → Run* | `dotnet test --project tests/Modules/Decisya.Modules.<Name>.Tests --filter-trait "Category=Integration" --minimum-expected-tests 1` |

7. Commit migration, model snapshot and SQL script together.

## Output
Migration files, snapshot and `deploy/sql/<schema>/<timestamp>.sql`; record them in the manifest under G4.
