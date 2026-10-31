---
name: threat-model
description: "Produces a STRIDE threat model with trust boundaries and mitigations mapped to OWASP ASVS 5.0 control ids for a component, an endpoint group, a hook, the CI or the release path. Use before implementing any public surface, token, session, trust or logging change, CI or release change, and whenever the user asks \"is this secure\" or \"what could go wrong\"."
---
# threat-model

## Steps
1. Read the issue's requirements and architecture note (paths are in `docs/ai/pipeline/<n>.md`), the component's public surface and endpoints if they exist.
2. Draw the data flow as a Mermaid diagram with trust boundaries (client | service | data stores | external providers). For a library or value type with no I/O, say so and model its callers' misuse instead.
3. For each element and flow, enumerate STRIDE threats: Spoofing (forged or replayed credentials), Tampering (a claim, option or hook that could be misconfigured), Repudiation (missing audit records), Information disclosure (secrets in logs, telemetry or errors), Denial of service (unbounded inputs, a dependency that fails open), Elevation of privilege (a missing check every consumer then inherits).
4. Map every mitigation to an ASVS **5.0** control id using `../asvs-checklist/references/l2-controls.md`.
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
