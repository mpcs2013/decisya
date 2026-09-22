---
name: threat-model
description: Produces a STRIDE threat model with trust boundaries and mitigations mapped to OWASP ASVS 5.0 control ids for a module, endpoint group or integration. Use before implementing any module, external integration, file upload, AI feature or auth change, and whenever the user asks "is this secure" or "what could go wrong".
---
# threat-model

## Steps
1. Read the module's Contracts project, endpoints and the C4 container diagram.
2. Draw the data flow as a Mermaid diagram with trust boundaries (browser | BFF | API | data stores | external providers).
3. For each element and flow, enumerate STRIDE threats. Financial context: pay special attention to Tampering (amounts, tenant id), Information disclosure (PII in logs, cross-tenant reads), Elevation (BOLA on account ids).
4. Map every mitigation to an ASVS 5.0 control id (V3 session, V4 access control, V5 validation, V7 error handling/logging, V14 configuration).
5. Rate each threat High / Medium / Low; every High needs an existing mitigation or a linked issue.
6. Write `docs/security/threat-models/<module>.md` using the table in `references/format.md`.

## Done when
No High without mitigation-or-issue, and the security-reviewer has recorded the verdict at the top of the file.
