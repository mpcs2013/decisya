---
name: threat-model
description: "Produces a STRIDE threat model with trust boundaries and mitigations mapped to OWASP ASVS 5.0 control ids for a module, endpoint group or integration. Use before implementing any module, external integration, file upload, AI feature or auth change, and whenever the user asks \"is this secure\" or \"what could go wrong\"."
---
# threat-model

## Steps
1. Read the issue's requirements and architecture note (paths are in `docs/ai/pipeline/<n>.md`), the module's Contracts project and endpoints if they exist.
2. Draw the data flow as a Mermaid diagram with trust boundaries (browser | BFF | API | data stores | external providers). For a library or value type with no I/O, say so and model its callers' misuse instead.
3. For each element and flow, enumerate STRIDE threats. Financial context: pay special attention to Tampering (amounts, tenant id), Information disclosure (PII in logs, cross-tenant reads), Elevation (BOLA on account ids), Denial of service (unbounded inputs).
4. Map every mitigation to an ASVS **5.0** control id using `../asvs-checklist/references/l2-controls.md` (e.g. V7 session, V8 authorization, V2 validation and business logic, V16 logging and error handling, V13 configuration).
5. Rate each threat High / Medium / Low; every High needs an existing mitigation or a linked issue.
6. Write `docs/security/threat-models/<slug>.md` using the table in `references/format.md`, and record the path in the manifest under G3.

## Output format
First line (the gate checker reads it):
```
<!-- gate: G3 | verdict: PASS|PASS-WITH-NOTES|BLOCK | issue: #<n> -->
```
BLOCK when a High threat has neither a mitigation nor a linked issue.

## Done when
No High without mitigation-or-issue, and the verdict line is present.
