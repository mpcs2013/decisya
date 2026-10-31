---
name: adr-writer
description: "Writes an Architecture Decision Record in MADR format under docs/adr and links it from the index. Use whenever a structural, security, data, hosting or tooling decision is made or changed, including when the user just says \"let's go with X\" or \"we decided\"."
---
# adr-writer

## Steps
1. Next number: highest `NNNN` in `docs/adr/` plus one.
2. Create `docs/adr/NNNN-<kebab-slug>.md` from `assets/template.md`. Tags come from `templates.adr_tags` in `.claude/project.json`.
3. Status is `Proposed` until the owner accepts it; superseding an ADR sets the old one to `Superseded by NNNN`. An accepted ADR may take dated, attributed amendments that never rewrite earlier text; a change to the decision itself is a new ADR that supersedes it.
4. Add a row to `docs/adr/README.md` (number, title, status, date).
5. If the decision changes a project invariant stated in `CLAUDE.md`, say so in *Consequences* and open an issue to update `CLAUDE.md` and the architecture tests.

## Quality bar
- Context states the forces (cost, security, portability, time).
- At least two considered options with a one-line reason each was rejected.
- Consequences list what becomes easier, harder, and which tests enforce the decision.
