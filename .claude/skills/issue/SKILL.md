---
name: issue
description: "Runs the Decisya per-issue pipeline for one GitHub issue: creates or resumes the branch and the pipeline manifest, delegates each gate to its owner agent in order, and checks every gate with .claude/scripts/gates.py. Invoked by Marco as /issue <n>."
argument-hint: "<GitHub issue number>"
disable-model-invocation: true
---
# issue

The main session is the orchestrator: only it can start agents. This skill tells it which agent to start, with which inputs, and how to check the result. The manifest `docs/ai/pipeline/<n>.md` is the state, so `/issue <n>` can be re-run at any time and resumes at the first gate that is not passed.

## Gates

| Gate | Owner | Artifact (path recorded in the manifest) | Passes when |
| --- | --- | --- | --- |
| G0 | orchestrator | `docs/ai/pipeline/<n>.md`, branch `issue/<n>-<slug>` | Issue open, branch and manifest exist, change class recorded |
| G1 | product-owner | `docs/requirements/<phase>/<slug>.md` | Verdict line `G1`, no unanswered open questions |
| G2 | architect | `docs/architecture/<slug>.md` | Verdict line `G2`: PASS, or N/A with `reason:` (no new module, contract or cross-module dependency) |
| G3 | security-reviewer | `docs/security/threat-models/<slug>.md` | Verdict line `G3`; every High mitigated or linked to an issue |
| G4 | backend-dev / frontend-dev | manifest § G4 evidence | The orchestrator re-ran build and tests: both green |
| G5 | test-engineer | § Traceability in the G1 requirements file | Verdict line `G5`; every acceptance criterion maps to a test or is marked manual with a reason |
| G6 | security-reviewer | `docs/security/reviews/<n>.md` | Verdict line `G6` PASS or PASS-WITH-NOTES; no Open High finding |
| G7 | orchestrator | manifest § G7 PR body draft | Contains `Closes #<n>` and one line per new package |

Verdict line: exactly one per gate, on its own line, outside code blocks (quoted or example lines never count; two lines for the same gate fail as AMBIGUOUS):
```
<!-- gate: G<k> | verdict: PASS|PASS-WITH-NOTES|N/A|BLOCK | issue: #<n> [| reason: …] -->
```

## Change classes and skips

| Class | Typical paths | Gates run |
| --- | --- | --- |
| feature | `src/`, `tests/` | all |
| docs-only | `docs/`, `*.md`, `.claude/` | G0, G7 |
| ci-tooling | `.github/`, `Directory.Build.props`, `global.json`, `dotnet-tools.json`, pre-commit | G0, G3, G6, G7 |
| dependency | `Directory.Packages.props`, `package.json` only | G0, G6, G7 |

`gates.py init` writes the skipped gates as `approved: PENDING`. Ask Marco to approve the skips; only after he says yes, replace `PENDING` with `Marco <YYYY-MM-DD>`. Any other skip (e.g. G2 on a feature) needs the same explicit approval and a reason.

## Steps

1. **G0 – issue, branch, manifest**
   - `gh issue view <n> --json number,title,state,body,labels,milestone`. Stop if the issue is closed or does not exist.
   - Hooks: `python .claude/scripts/prereqs.py hooks`. If a hook is missing, ask Marco to run `pre-commit install` before any commit (the pre-push check, #59).
   - Branch: if the current branch is not `issue/<n>-*`, check `git status --porcelain` is clean (ask Marco otherwise), `git fetch origin`, then `git switch -c issue/<n>-<slug> origin/main`.
   - Manifest: if `docs/ai/pipeline/<n>.md` is missing, choose the class from the issue's scope (`python .claude/scripts/gates.py classify` helps once files have changed; ask Marco when unsure), then `python .claude/scripts/gates.py init <n> --title "<title>" --class <class>`, copy the issue's "Done when" into it, and get the skips approved.
2. **Loop:** `python .claude/scripts/gates.py <n> --next`. For the gate it names:
   - **Agent gates (G1, G2, G3, G5, G6):** decide the artifact path, write it in the manifest's Artifact column, then start the owner agent with this prompt:
     > Issue #<n>: <title>. Done when: <line>. Manifest: docs/ai/pipeline/<n>.md (read it for the paths of earlier artifacts). Your gate: G<k>. Write your artifact to <path> and include the verdict line `<!-- gate: G<k> | verdict: … | issue: #<n> -->`. Do not edit the manifest.
   - **G4:** start backend-dev and/or frontend-dev with the same prompt shape (inputs: G1, G2, G3 artifacts). When it returns, run the checks yourself, paste the summary under "## G4 evidence", then set G4 to `passed` with a one-line note:

     | Visual Studio 2026 | CLI |
     | --- | --- |
     | *Build → Rebuild Solution*; *Test Explorer → Run All* | `dotnet build -warnaserror` then `dotnet test --no-build` |

   - **G7:** draft the PR body under "## G7 PR body draft": summary, `Closes #<n>`, one line per package added in `Directory.Packages.props`, `dotnet-tools.json` or `package.json` (with justification), then set G7 to `passed`. Never push; give Marco the commit/push/PR commands.
   - After every agent: run `python .claude/scripts/gates.py <n>` and show the table. If the gate still fails, report why and stop.
3. **BLOCK:** G3 BLOCK goes back to the architect (or to Marco for a scope decision). G6 BLOCK goes back to the dev agent with the findings, then G6 is re-run as a re-check (the reviewer edits statuses and the verdict line in place).
4. **Finish:** `python .claude/scripts/gates.py <n>` exits 0. Report the table and hand over to Marco.

## Rules
- Only the owner agent writes its artifact. The orchestrator writes only the manifest; it never adds or edits a verdict line to make a gate pass.
- Do not start an agent for a gate whose predecessors are not passed or approved as skipped.
- Resuming: re-run `/issue <n>`; the manifest shows what is done.
