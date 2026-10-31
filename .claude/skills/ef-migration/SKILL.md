---
name: ef-migration
description: "Creates an Entity Framework Core migration scoped to one context, generates the idempotent SQL script, and documents rollback. Use whenever an entity, configuration or DbContext changes, or when the user says \"add a column\", \"new table\", \"update the schema\" or \"migration\"."
---
# ef-migration

Part of the dotnet pack. Each context owns its schema and its own migrations history table; never run a migration from another context. Schema conventions (naming, tenant columns, money and time types) belong to the project's addendum, not to this skill.

## Prerequisites
Run `python .claude/scripts/prereqs.py ef-migration` from the repository root. If anything is missing, report the listed issues and stop.
A listed agent cannot run `python` (core denies interpreters to listed agents), so the orchestrator (the main session) runs the prerequisites. An agent runs `dotnet tool restore` and `dotnet ef` only when its own tools line declares them; no pack or `boundaries.json` grant can name them. Otherwise the agent makes the entity, configuration and test changes and asks the orchestrator to run the CLI steps below.

## Steps

`dotnet-ef` is a local tool pinned in `dotnet-tools.json`; run `dotnet tool restore` once per clone. Paths below are from the repository root.

| Step | CLI |
| --- | --- |
| Restore tools | `dotnet tool restore` |
| Add migration | `dotnet ef migrations add <Verb><Noun> --project <context project> --startup-project <migrator project> --context <Name>DbContext --output-dir <migrations folder>` |
| Idempotent script | `dotnet ef migrations script --idempotent --project <context project> --startup-project <migrator project> --context <Name>DbContext -o <scripts folder>/<timestamp>.sql` |

1. Name migrations `<Verb><Noun>` (for example `AddInvitations`).
2. Review the generated `Up`: no data loss without a two-step migration (add, backfill, then drop in a later migration).
3. Follow the project's schema conventions from its addendum.
4. Add a rollback note to the migration file header (what `Down` cannot restore).
5. Apply the migration in the integration test fixture and run the integration lane: `dotnet test --project tests/<Project> --filter-trait "Category=Integration" --minimum-expected-tests 1`.
6. Commit the migration, the model snapshot and the SQL script together.

## Output
Migration files, snapshot and the idempotent script; record them in the manifest under G4.
