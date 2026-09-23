---
name: architecture-note
description: "Writes the gate G2 architecture note for an issue under docs/architecture/: C4 excerpt, module boundaries, contracts, ADR links and the NetArchTest rules to add, or a short N/A note with a reason when nothing structural changes. Use for gate G2 of every feature issue."
---
# architecture-note

## Steps
1. Read the issue (`gh issue view <n>`), the G1 requirements named in `docs/ai/pipeline/<n>.md`, `docs/adr/README.md` and any existing note under `docs/architecture/`.
2. Decide: does the issue add a module, a public contract, a cross-module dependency, an external integration, or change auth, tenancy or data flow?
   - **No:** write the N/A form of `assets/template.md` with the reason.
   - **Yes:** fill the full form. A decision that changes an earlier ADR or an invariant needs an ADR (`adr-writer`) first.
3. Save as `docs/architecture/<slug>.md` (same slug as the requirements file).

## Done when
The note exists with its verdict line, and every new boundary names the NetArchTest rule that enforces it.
