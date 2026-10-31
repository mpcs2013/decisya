---
name: runbook
description: "Writes or updates an operational or developer runbook under docs/runbooks/ with every step shown for each tool in templates.runbook_tools, each command verified against the repository. Use for setup guides, deployment, backup and restore, incident and release procedures, or when documentation drifts from real tool behaviour."
---
# runbook

Owners: the implementer who owns operations writes the commands and procedure; the tech-writer edits the prose. Both use this format.

## Steps
1. Start from `assets/template.md`; file name `docs/runbooks/<kebab-topic>.md`. The columns are the tools listed in `templates.runbook_tools` of `.claude/project.json`, in that order.
2. Every step is a table row with one cell per tool. Where a tool has no equivalent, write "— (not applicable)" and say why; never leave a cell empty without saying why.
3. Verify each command before writing it: run it if your tools allow, otherwise run its `--help` or `--version` form, and mark anything you could not run with **(unverified)**. Never invent a flag.
4. Secrets appear only as environment variable names or secret-store keys, never values.
5. Add a *Verify* section (how to tell the procedure worked) and a *Rollback* section where the procedure changes state: what to revert, in what order, and how to verify recovery.

## Done when
Every step has a cell per tool (or a stated reason), every command is verified or marked (unverified), and the Verify and Rollback sections exist where they apply.
