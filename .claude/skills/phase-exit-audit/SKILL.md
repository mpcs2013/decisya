---
name: phase-exit-audit
description: "Audits the repository against docs/compliance/standards-register.md at the end of a phase and writes docs/compliance/phase-<p>-exit.md with Met / Partial / Gap per standard and evidence paths. Use at every phase exit, quarterly, or when a new data category or sub-processor appears."
---
# phase-exit-audit

## Steps
1. Read `docs/compliance/standards-register.md`; one row per standard in the output.
2. For each row, find evidence: a file path, a CI job name in `.github/workflows/`, or a gate artifact under `docs/ai/pipeline/`, `docs/security/` or `docs/requirements/`. Run `python .claude/scripts/gates.py <n>` for every issue in the phase milestone (`gh issue list --milestone "<milestone>" --state all`) and cite its result.
3. Status: **Met** (evidence cited), **Partial** (evidence plus the named gap), **Gap** (no evidence; name an owner agent and open or link an issue).
4. Write `docs/compliance/phase-<p>-exit.md`; edit `docs/compliance/dpia.md` when data categories, sub-processors or retention changed (create it from the register's GDPR row the first time).
5. Rule: a phase does not exit with any Gap on the ASVS L2, SSDF or GDPR rows. State the exit decision at the top.

## Output
```markdown
# Phase <p> exit audit – YYYY-MM-DD
Exit decision: GO | NO-GO (<reason>)

| Standard | Status | Evidence | Gap / owner / issue |
| --- | --- | --- | --- |
```
