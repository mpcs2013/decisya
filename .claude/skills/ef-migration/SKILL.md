---
name: ef-migration
description: "Creates an EF Core 10 migration scoped to one module's schema, generates the idempotent SQL script, and documents rollback. Use whenever an entity, configuration or DbContext changes, or when the user says \"add a column\", \"new table\", \"update the schema\" or \"migration\"."
---
# ef-migration

Each module owns its schema and its own migrations history table; never run a migration from another module's context.

## Steps (both paths)

| Visual Studio 2026 (Package Manager Console) | CLI |
| --- | --- |
| Default project: `Decisya.Modules.<Name>` | `cd src/Modules/<Name>/Decisya.Modules.<Name>` |
| `Add-Migration <Verb><Noun> -Context <Name>DbContext -OutputDir Infrastructure/Migrations -StartupProject Decisya.Api` | `dotnet ef migrations add <Verb><Noun> --context <Name>DbContext --output-dir Infrastructure/Migrations --startup-project ../../../Decisya.Api` |
| `Script-Migration -Idempotent -Context <Name>DbContext -Output ../../../../deploy/sql/<schema>/<timestamp>.sql` | `dotnet ef migrations script --idempotent --context <Name>DbContext --startup-project ../../../Decisya.Api -o ../../../../deploy/sql/<schema>/<timestamp>.sql` |

1. Name migrations `<Verb><Noun>` (`AddInvitations`, `RenameBudgetPeriod`).
2. Review the generated `Up`: no data loss without a two-step migration (add → backfill → drop in a later migration).
3. Amounts are `bigint` minor units plus `char(3)` currency; instants are `timestamptz`; calendar dates are `date`.
4. Every table gets `tenant_id uuid not null` and an index starting with `tenant_id`.
5. Add a rollback note to the migration file header (what `Down` cannot restore).
6. Apply in the integration test fixture (`Database.Migrate()`), run `dotnet test --filter Category=Integration`.
7. Commit migration, model snapshot and SQL script together.
