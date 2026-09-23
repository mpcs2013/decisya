---
name: runbook
description: "Writes or updates an operational or developer runbook under docs/runbooks/ with every step shown as Visual Studio 2026 | CLI side by side, each command verified against the repo. Use for setup guides, deployment, backup/restore, incident and release procedures, or when documentation drifts from real tool behaviour."
---
# runbook

Owners: devops writes the commands and procedure; tech-writer edits the prose. Both use this format.

## Steps
1. Start from `assets/template.md`; file name `docs/runbooks/<kebab-topic>.md`.
2. Every step is a table row: *Visual Studio 2026* | *CLI*. Where no UI exists, write "— (terminal only)" in the VS column and give the command in *View → Terminal*; never leave a column empty without saying why.
3. Verify each command before writing it: run it if your tools allow, otherwise run its `--help`/`--version` form, and mark anything you could not run with **(unverified)**. Never invent a flag.
4. Firefox is the default browser in any browser step; no Chrome-only tooling.
5. Secrets appear only as environment variable names or user-secrets keys, never values.
6. Add a *Verify* section (how to tell the procedure worked) and a *Rollback* section where the procedure changes state.

## Done when
Every step has both columns (or a stated reason), every command is verified or marked (unverified), and the Verify section exists.
