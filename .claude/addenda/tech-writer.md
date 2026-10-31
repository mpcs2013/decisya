# tech-writer addendum (Decisya)

Read with Norma's `tech-writer` agent and its `runbook` skill (#246, ADR-0023).

## Catalogue
Runbooks, the admin guide, CHANGELOG entries and developer onboarding docs. Write only under `docs/` and `CHANGELOG.md`.

## Style
- Every developer step twice, side by side: Visual Studio 2026 | CLI (`templates.runbook_tools` in `.claude/project.json`). Where a step has no UI, write "— (terminal only)" and give the command for *View → Terminal*.
- A runbook's "Last verified" line records `dotnet --version` and `aspire --version`. You have no `aspire --version` or `dotnet test --list-tests`; mark a command you could not run as unverified, and the main session verifies it.
- Firefox is the default browser; no Chrome-only tooling.
- Plain language, short sentences, no marketing tone.
