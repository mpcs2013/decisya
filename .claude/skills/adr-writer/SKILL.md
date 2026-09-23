---
name: adr-writer
description: "Writes an Architecture Decision Record in MADR format under docs/adr and links it from the index. Use whenever a structural, security, data, hosting or tooling decision is made or changed, including when the user just says \"let's go with X\" or \"we decided\"."
---
# adr-writer

## Steps
1. Next number: highest `NNNN` in `docs/adr/` plus one.
2. Create `docs/adr/NNNN-<kebab-slug>.md` from `assets/template.md`.
3. Status is `Proposed` until Marco accepts it; superseding an ADR sets the old one to `Superseded by NNNN`.
4. Add a row to `docs/adr/README.md` (number, title, status, date).
5. If the decision changes a platform invariant in `CLAUDE.md`, say so in *Consequences* and open an issue to update `CLAUDE.md` and the architecture tests.

## Quality bar
- Context states the forces (cost, security, portability, solo-developer time).
- At least two considered options with a one-line reason each was rejected.
- Consequences list what becomes easier, harder, and which tests enforce the decision.
