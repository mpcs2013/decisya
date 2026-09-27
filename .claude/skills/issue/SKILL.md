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
| G4 | backend-dev / platform-dev / identity-dev / frontend-dev (by path, below) | manifest § G4 evidence | The orchestrator re-ran build and tests: both green |
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

## Keep issues small (#84)

One issue, one PR, finished. These rules keep review and discovery from growing an issue into a chain.

**Tiers.** Propose the tier at G0; Marco approves it together with the skips.

| Tier | When | Gates |
| --- | --- | --- |
| Light | Tooling, docs and CI tweaks that don't touch auth, secrets, hooks or permissions | G0; one combined security diff review, whose file carries G6 and a G3 `N/A` line with `reason: light tier`; G7. Add G4 only when code or tests change. |
| Full | Product features, and anything touching auth, secrets, hooks or permissions, `.devcontainer/**` (the sandbox boundary), CI required checks, rulesets or workflow permissions, the gate machinery (`gates.py`, these rules, the reviewer agents), or a new third-party package | All gates. The G3 threat model holds at most about five MUSTs; everything else is a SHOULD. |

**Review findings.**
- Fix High and Medium findings in the PR.
- Fix a Low in the PR only when it takes a few lines in a file the PR already changes.
- Every other Low or Info goes to the backlog issue #83. It gets no new issue and no extra round.
- G7 waits until every fix-now finding is marked Fixed.

**Re-check.** Run at most one G6 re-check, and only to confirm the fixes. New findings from the re-check go to #83 unless they are High or Medium.

**Discoveries.** Anything found during an issue goes to #83, unless it blocks this issue: a required check fails, or the Done-when can't be met. Propose no new issue mid-stream. There is one exception: a High or Medium security flaw is reported to Marco at once, even in unchanged code. It is exempt from #83 and from the tooling freeze.

**Tooling freeze** (until the Phase 0 exit). Start no pipeline or tooling issue unless it blocks a product issue. #83 is reviewed once, at phase exit.

**Security-reviewer prompts** add: "Report only findings that change the merge decision. Don't re-audit unchanged areas. Mark each finding fix-now (High, Medium, or a Low of a few lines in a changed file) or backlog."

## Steps

1. **G0 – issue, branch, manifest**
   - `gh issue view <n> --json number,title,state,body,labels,milestone`. Stop if the issue is closed or does not exist.
   - Hooks: `python .claude/scripts/prereqs.py hooks`. If a hook is missing, ask Marco to run `pre-commit install` before any commit (the pre-push check, #59).
   - Branch: if the current branch is not `issue/<n>-*`, check `git status --porcelain` is clean (ask Marco otherwise), `git fetch origin`, then `git switch -c issue/<n>-<slug> origin/main`.
   - Manifest: if `docs/ai/pipeline/<n>.md` is missing, choose the class from the issue's scope (`python .claude/scripts/gates.py classify` helps once files have changed; ask Marco when unsure), then `python .claude/scripts/gates.py init <n> --title "<title>" --class <class>`, copy the issue's "Done when" into it, and get the skips approved.
2. **Loop:** `python .claude/scripts/gates.py <n> --next`. For the gate it names:
   - **Agent gates (G1, G2, G3, G5, G6):** decide the artifact path, write it in the manifest's Artifact column, then start the owner agent with this prompt:
     > Issue #<n>: <title>. Done when: <line>. Manifest: docs/ai/pipeline/<n>.md (read it for the paths of earlier artifacts). Your gate: G<k>. Write your artifact to <path> and include the verdict line `<!-- gate: G<k> | verdict: … | issue: #<n> -->`. Do not edit the manifest.
   - **G4:** pick the owner(s) by the paths the change touches, using the table in "G4 routing" below (their write lanes are in `.claude/boundaries.json`), and run several in sequence when a change spans lanes (as #17 did).

     Start each with the same prompt shape (inputs: G1, G2, G3 artifacts). If the lane hook denies a write, the agent reports it and the orchestrator decides the owner; nobody works around a deny. Changing a lane or an agent's tools is its own issue, with G3 and G6. When they return, run the checks yourself, paste the summary under "## G4 evidence", then set G4 to `passed` with a one-line note:

     | Visual Studio 2026 | CLI |
     | --- | --- |
     | *Build → Rebuild Solution*; *Test Explorer → Run All* | `dotnet build -warnaserror` then `dotnet test --no-build` |

   - **G7:** draft the PR body under "## G7 PR body draft": summary, a **Rollback** line (how to undo it: revert the PR, plus any data or config step such as a volume reset), a **Verification (local | CI)** table (what ran where, with counts), `Closes #<n>`, one line per package added in `Directory.Packages.props`, `dotnet-tools.json` or `package.json` (with justification), and a `Docs:` line at column 0 listing the docs this change updated (repository-relative paths, comma-separated) or `none: <reason>` (`gates.py` fails G7 without it from #76 on), then set G7 to `passed`. Never push; give Marco the commit/push/PR commands.
   - After every agent: run `python .claude/scripts/gates.py <n>` and show the table. If the gate still fails, report why and stop.
3. **BLOCK:** G3 BLOCK goes back to the architect (or to Marco for a scope decision). G6 BLOCK goes back to the dev agent with the findings, then G6 is re-run as a re-check (the reviewer edits statuses and the verdict line in place). This is the one re-check; see "Keep issues small".
4. **Finish:** `python .claude/scripts/gates.py <n>` exits 0. Report the table and hand over to Marco.

## G4 routing

Generated from `routing.G4` in `.claude/boundaries.json` by `python .claude/scripts/roster.py`; edit the config, not this table.

<!-- routing:begin (generated by python .claude/scripts/roster.py; do not edit) -->

| Paths | Owner |
| --- | --- |
| `src/Modules/**`, `src/Decisya.Api/**` (business endpoints), `src/Decisya.SharedKernel/**`, `src/Decisya.ServiceDefaults/**`, `src/Decisya.Infrastructure*/**`, their tests | backend-dev |
| `src/Decisya.AppHost/**`, `deploy/postgres/**`, container images, Testcontainers fixtures, `tests/Decisya.AppHost.Tests/**`, `tests/Decisya.TestInfrastructure/**` | platform-dev |
| `src/Decisya.Bff/**`, `src/Decisya.Api/Authentication/**`, `src/Decisya.Api/Program.cs` wiring, `deploy/keycloak/**`, `tests/Decisya.Identity.Tests/**` | identity-dev |
| `src/Decisya.Web/**`, `tests/e2e/**` | frontend-dev |
| `.github/**`, `.devcontainer/**`, release and observability stack | devops |

<!-- routing:end -->

## Rules
- Only the owner agent writes its artifact. The orchestrator writes only the manifest; it never adds or edits a verdict line to make a gate pass.
- Do not start an agent for a gate whose predecessors are not passed or approved as skipped.
- Resuming: re-run `/issue <n>`; the manifest shows what is done.
