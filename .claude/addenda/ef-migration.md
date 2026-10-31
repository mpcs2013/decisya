# ef-migration addendum (Decisya)

Read with the dotnet pack's `ef-migration` skill: Decisya's schema conventions and paths (#246, ADR-0023).

## Schema conventions
- Amounts are `bigint` minor units plus `char(3)` currency; instants are `timestamptz`; calendar dates are `date`.
- Every table gets `tenant_id uuid not null` and an index starting with `tenant_id`.

## Paths and commands
`dotnet-ef` is a local tool pinned in `dotnet-tools.json`; run `dotnet tool restore` once per clone. Paths are from the repository root.

| Step | Visual Studio 2026 (Package Manager Console) | CLI |
| --- | --- | --- |
| Add migration | Default project: `Decisya.Modules.<Name>`; `Add-Migration <Verb><Noun> -Context <Name>DbContext -OutputDir Infrastructure/Migrations -StartupProject Decisya.Infrastructure.Migrator` | `dotnet ef migrations add <Verb><Noun> --project src/Modules/<Name>/Decisya.Modules.<Name> --startup-project src/Decisya.Infrastructure.Migrator --context <Name>DbContext --output-dir Infrastructure/Migrations` |
| Idempotent script | `Script-Migration -Idempotent -Context <Name>DbContext -Output deploy/sql/<schema>/<timestamp>.sql` (check where PMC resolves the relative path on first use) | `dotnet ef migrations script --idempotent --project src/Modules/<Name>/Decisya.Modules.<Name> --startup-project src/Decisya.Infrastructure.Migrator --context <Name>DbContext -o deploy/sql/<schema>/<timestamp>.sql` |

## Verify
Apply in the integration test fixture (`Database.Migrate()`) and run the module's integration lane:

| Visual Studio 2026 | CLI |
| --- | --- |
| Docker Desktop running; *Test Explorer → Group by Traits → Category: Integration → Run* | `dotnet test --project tests/Modules/Decisya.Modules.<Name>.Tests --filter-trait "Category=Integration" --minimum-expected-tests 1` |
