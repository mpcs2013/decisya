---
name: asvs-checklist
description: "Evaluates a diff or module against OWASP ASVS 5.0 Level 2 controls and records pass/fail/n-a with evidence. Use on every PR touching auth, session, input handling, data access, file upload, logging, AI prompts or infrastructure, and at every phase exit for the full baseline. Trigger even if the user only asks for \"a quick security look\"."
---
# asvs-checklist

Level 2 everywhere; the V6 (authentication) and V7 (session) rows in `references/l2-controls.md` apply Level 3.

## Steps
1. Scope. For a PR: `git fetch origin`, then `git diff --name-only origin/main...HEAD` **plus** `git status --porcelain` (uncommitted and untracked files are part of the change until committed). For a baseline: all of `src/`.
2. Walk the chapters in `references/l2-controls.md` (ASVS 5.0 numbering). Expand a chapter from the official ASVS 5.0 text the first time a change touches it.
3. For each control: PASS (with file:line evidence), FAIL (with fix), or N/A (with reason).
4. Verdict: BLOCK if any FAIL is High; PASS-WITH-NOTES if only Medium/Low; PASS otherwise.
5. Write `docs/security/reviews/<n>.md` (`<n>` = GitHub issue number; a phase baseline uses `phase-<p>.md`). For a baseline also update `docs/security/asvs-l2.md`.

## Output format
First line of the review (the gate checker reads it):
```
<!-- gate: G6 | verdict: PASS|PASS-WITH-NOTES|BLOCK | issue: #<n> -->
```
Then the findings table: Id, Severity, ASVS 5.0 id, file:line, finding, evidence, fix, Status (Open/Fixed). On a re-check, **edit** the Status column and the verdict line; do not rewrite the file.

## Non-negotiables (always FAIL if violated)
- Token or session identifier reachable from JavaScript.
- Session cookie missing `HttpOnly`, `Secure` or `SameSite=Strict`.
- JWT accepted with `alg` outside the allow-list or without `aud`/`iss`/`exp` checks.
- Query built by string concatenation; tenant filter bypassed.
- PII, token or password present in a log statement or exception message.
- Secret literal in source or config committed to git.
