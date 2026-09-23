---
name: isolation-test
description: "Writes the two-tenant isolation tests for a module or entity: tenant A can neither read nor change tenant B's rows. Use whenever a tenant-scoped entity, DbContext or query is added, and for the isolation criterion of gate G5."
---
# isolation-test

Platform invariant 1: every persisted aggregate is tenant-scoped and no query crosses tenants. Every tenant-scoped feature ships these tests.

## Prerequisites
Run `python .claude/scripts/prereqs.py module-scaffold --phase tenancy` from the repository root (VS 2026: *View → Terminal*). If anything is missing, report the listed issues and stop.

## Steps
1. Create `tests/Modules/Decisya.Modules.<Name>.Tests/<Name>IsolationTests.cs` from `references/template.md`: one read test and one update/delete-by-id test per aggregate root.
2. Run the integration lane (Docker running):

| Visual Studio 2026 | CLI |
| --- | --- |
| *Test Explorer → Group by Traits → Category: Integration → Run* | `dotnet test --filter-trait "Category=Integration" --minimum-expected-tests 2` |

## Done when
Both tests pass for every aggregate root, and they are listed in the G5 traceability table against the isolation acceptance criterion.
