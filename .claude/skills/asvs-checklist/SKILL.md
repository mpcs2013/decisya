---
name: asvs-checklist
description: "Evaluates a diff or a component against OWASP ASVS 5.0 Level 2 controls and records pass/fail/n-a with evidence. Use on every PR touching authentication, session, input handling, tokens, cookies, trust, logging, configuration, CI or release, and for a full baseline before a release. Trigger even if the user only asks for \"a quick security look\"."
---
# asvs-checklist

Level 2 everywhere. The project's assurance level and any chapters that apply Level 3 come from the security-reviewer addendum.

## Steps
1. Scope. For a PR: `git fetch origin`, then `git diff --name-only origin/main...HEAD` **plus** `git status --porcelain` (uncommitted and untracked files are part of the change until committed). For a baseline: all of the source tree.
2. Walk the chapters in `references/l2-controls.md` (ASVS 5.0 numbering). Expand a chapter from the official ASVS 5.0 text the first time a change touches it.
3. For each control: PASS (with file:line evidence), FAIL (with fix), or N/A (with reason).
4. Verdict: BLOCK if any FAIL is High; PASS-WITH-NOTES if only Medium or Low; PASS otherwise.
5. Write `docs/security/reviews/<n>.md` (`<n>` = GitHub issue number; a release baseline uses `release-<version>.md`). For a baseline, also update the project's ASVS baseline document if it keeps one.

## Output format
First line of the review (the gate checker reads it):
```
<!-- gate: G6 | verdict: PASS|PASS-WITH-NOTES|BLOCK | issue: #<n> -->
```
Then the findings table: Id, Severity, ASVS 5.0 id, file:line, finding, evidence, fix, Status (Open/Fixed). On a re-check, **edit** the Status column and the verdict line; do not rewrite the file.

## Non-negotiables (always FAIL if violated, where the control applies)
- A token or session identifier reachable from JavaScript.
- A session cookie missing `HttpOnly`, `Secure` or `SameSite=Strict`.
- A token accepted with an algorithm outside the allow-list or without audience, issuer and expiry checks.
- A query built by string concatenation; an application-owned value hard-coded in shared code.
- Personal data, a token or a password present in a log statement or exception message.
- A secret literal in source or configuration committed to git.
