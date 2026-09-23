---
name: asvs-checklist
description: "Evaluates a diff or module against OWASP ASVS 5.0 Level 2 controls and records pass/fail/n-a with evidence. Use on every PR touching auth, session, input handling, data access, file upload, logging, AI prompts or infrastructure, and at every phase exit for the full baseline. Trigger even if the user only asks for \"a quick security look\"."
---
# asvs-checklist

## Steps
1. Scope: `git diff main --name-only` for a PR; whole `src/` for a baseline.
2. Walk the chapters in `references/l2-controls.md` (curated subset; expand from the official ASVS 5.0 when a chapter is touched for the first time).
3. For each control: PASS (with file:line evidence), FAIL (with fix), or N/A (with reason).
4. Verdict: BLOCK if any FAIL is High; PASS-WITH-NOTES if only Medium/Low; PASS otherwise.
5. Write `docs/security/reviews/<issue-or-phase>.md`; for a baseline also update `docs/security/asvs-l2.md`.

## Non-negotiables (always FAIL if violated)
- Token or session identifier reachable from JavaScript.
- Session cookie missing `HttpOnly`, `Secure` or `SameSite=Strict`.
- JWT accepted with `alg` outside the allow-list or without `aud`/`iss`/`exp` checks.
- Query built by string concatenation; tenant filter bypassed.
- PII, token or password present in a log statement or exception message.
- Secret literal in source or config committed to git.
