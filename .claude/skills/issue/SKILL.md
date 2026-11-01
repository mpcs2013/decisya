---
name: issue
description: "Runs the per-issue pipeline for one GitHub issue: creates or resumes the branch and the pipeline manifest, delegates each gate to its owner agent in order, and checks every gate with .claude/scripts/gates.py. Invoked by the owner as /issue <n>."
argument-hint: "<GitHub issue number>"
disable-model-invocation: true
---
# issue

The main session is the orchestrator: only it can start agents. This skill tells it which agent to start, with which inputs, and how to check the result. The manifest `docs/ai/pipeline/<n>.md` is the state, so `/issue <n>` can be re-run at any time and resumes at the first gate that is not passed.

## Addendum invariant
A project may add context, inputs and checks in `.claude/addenda/issue.md`. An addendum may not weaken or override the gates, verdict lines, skip approvals, deny handling or fail-closed behaviour in this file. Where they conflict, this file wins and the orchestrator reports the conflict.

## Gates

| Gate | Owner | Artifact (path recorded in the manifest) | Passes when |
| --- | --- | --- | --- |
| G0 | orchestrator | `docs/ai/pipeline/<n>.md`, branch `issue/<n>-<slug>` | Issue open, branch and manifest exist, change class recorded |
| G1 | product-owner | `docs/requirements/<slug>.md` | Verdict line `G1`, no unanswered open questions |
| G2 | architect | `docs/architecture/<slug>.md` | Verdict line `G2`: PASS, or N/A with `reason:` (no new component, no change to a boundary or public surface) |
| G3 | security-reviewer | `docs/security/threat-models/<slug>.md` | Verdict line `G3`; every High mitigated or linked to an issue |
| G4 | the implementer agents (by path, below) | manifest § G4 evidence | The orchestrator re-ran build and tests: both green |
| G5 | test-engineer | § Traceability in the G1 requirements file | Verdict line `G5`; every acceptance criterion maps to a test or is marked manual with a reason |
| G6 | security-reviewer | `docs/security/reviews/<n>.md` | Verdict line `G6` PASS or PASS-WITH-NOTES; no Open High finding |
| G7 | orchestrator | manifest § G7 PR body draft | Contains `Closes #<n>`, a `Docs:` line, and one line per new package |

Verdict line: exactly one per gate, on its own line, outside code blocks (quoted or example lines never count; two lines for the same gate fail as AMBIGUOUS):
```
<!-- gate: G<k> | verdict: PASS|PASS-WITH-NOTES|N/A|BLOCK | issue: #<n> [| reason: …] -->
```

## Change classes and skips

The class is chosen from the paths the change touches. The CI-tooling and dependency path classes come from `gates.ci_tooling_paths` and `gates.dependency_paths` in `.claude/project.json`; documentation-only paths are a core convention (`docs/`, `*.md`, `.claude/`, `.vscode/`, `LICENSE`, issue templates, the Dependabot file).

| Class | Typical paths | Gates run |
| --- | --- | --- |
| feature | source and tests | all |
| docs-only | documentation | G0, G7 |
| ci-tooling | the project's CI-tooling paths | G0, G3, G6, G7 |
| dependency | the project's dependency manifests only | G0, G6, G7 |

A path that is review-required (the core floor plus `gates.review_required_paths`) always forces G3 and G6, whatever the class.

`gates.py init` writes the skipped gates as `approved: PENDING`. Ask the owner to approve the skips; only after an approver says yes, replace `PENDING` with `<name> <YYYY-MM-DD>`, keeping the `;` before `approved:`: the name of the approver who said yes, exactly as `gates.approvers` in `.claude/project.json` lists it (`gates.py` accepts no other name). Any other skip (for example G2 on a feature) needs the same explicit approval and a reason.

## Tiers and findings

One issue, one PR, finished. These rules keep review and discovery from growing an issue into a chain.

**Tiers.** Propose the tier at G0; the owner approves it together with the skips. The class and the review-required rule decide which gates run, and the tier decides how they run. One tier rule adds a gate: a new third-party package always gets G3, whatever its class; when `gates.py init` wrote G3 as `skipped` (the `dependency` class), change its row to `required` with an empty note, and never ask for that skip's approval. At the Light tier, G3 becomes the `N/A` line of the combined review (never a skip), and G4 is kept only when code or tests change; leaving it out is an approved skip.

| Tier | When | Gates |
| --- | --- | --- |
| Light | Tooling, docs and CI tweaks that do not touch authentication, secrets, hooks or permissions | G0; one combined security diff review, whose file carries G6 and a G3 `N/A` line with `reason: light tier`; G7. Keep G4 only when code or tests change. |
| Full | Code of the product, anything touching authentication, tokens, sessions, secrets, hooks or permissions, CI required checks, rulesets or workflow permissions, the publish and release path, the gate machinery (`gates.py`, these rules, the reviewer agents), or a new third-party package | Every gate the class and the review-required rule call for, each with its own artifact. The G3 threat model holds at most about five MUSTs; everything else is a SHOULD. |

**Review findings.**
- Fix High and Medium findings in the PR.
- Fix a Low in the PR only when it takes a few lines in a file the PR already changes.
- Every other Low or Info goes to the project's backlog issue (`CLAUDE.md` names it). It gets no new issue and no extra round.
- G7 waits until every fix-now finding is marked Fixed.

**Re-check.** Run at most one G6 re-check, and only to confirm the fixes. New findings from the re-check go to the backlog unless they are High or Medium.

**Discoveries.** Anything found during an issue goes to the backlog, unless it blocks this issue: a required check fails, or the Done-when cannot be met. Propose no new issue mid-stream. There is one exception: a High or Medium security flaw is reported to the owner at once, even in unchanged code.

**Security-reviewer prompts** add: "Report only findings that change the merge decision. Do not re-audit unchanged areas. Mark each finding fix-now (High, Medium, or a Low of a few lines in a changed file) or backlog."

## Steps

1. **G0 – issue, branch, manifest**
   - `gh issue view <n> --json number,title,state,body,labels,milestone`. Stop if the issue is closed or does not exist.
   - Hooks: `python .claude/scripts/prereqs.py hooks`. If a hook is missing, ask the owner to run `pre-commit install` before any commit.
   - Branch: if the current branch is not `issue/<n>-*`, check `git status --porcelain` is clean (ask the owner otherwise), `git fetch origin`, then `git switch -c issue/<n>-<slug> origin/main`.
   - Manifest: if `docs/ai/pipeline/<n>.md` is missing, choose the class from the issue's scope (`python .claude/scripts/gates.py classify` helps once files have changed; ask the owner when unsure), then `python .claude/scripts/gates.py init <n> --title "<title>" --class <class>`, copy the issue's "Done when" into it, and get the skips approved.
   - Usage start: `python .claude/scripts/capture.py start <n> --where this-machine`, or `--where sandbox` when the
     issue's agents run in a sandbox (ask the owner when unsure). A second run keeps the first start. On exit 1 (for
     example, the capture folder is not git-ignored), report the message to the owner and go on; nothing is written.
2. **Loop:** `python .claude/scripts/gates.py <n> --next`. For the gate it names:
   - **Agent gates (G1, G2, G3, G5, G6):** decide the artifact path, write it in the manifest's Artifact column, then start the owner agent with this prompt:
     > Issue #<n>: <title>. Done when: <line>. Manifest: docs/ai/pipeline/<n>.md (read it for the paths of earlier artifacts). Your gate: G<k>. Write your artifact to <path> and include the verdict line `<!-- gate: G<k> | verdict: … | issue: #<n> -->`, where the verdict is one of PASS, PASS-WITH-NOTES, N/A or BLOCK. Do not edit the manifest.

     End the prompt with the gate's tool rule, because an agent that improvises a command is frozen at the first deny. Every rule also says: "Set the path of each Grep and Glob search to the folders the task needs, never the repository root."
     - **G1, G2** (product-owner, architect; no Bash): "Make no shell call of any kind, not even a placeholder or `echo`. Read and search with Read, Grep and Glob."
     - **G3, G6** (security-reviewer): "Run only the commands your tools line and the installed packs' grants allow, as written: `git fetch origin`, `git diff --name-only origin/<base>...HEAD` and `git diff origin/<base>...HEAD`, `git status --porcelain` and `git diff` for uncommitted changes, `git log`, and the pack's dependency audit. `<base>` is the pull request's base branch. Read files with Read; search with Grep and Glob. Never `git show`, `git -C`, `ls`, `cat` or the gate scripts, not even a placeholder `echo`."
     - **G4** (implementers): "Change files only with the Edit and Write tools, and read and search with Read, Grep and Glob; never a script, heredoc, redirect, `grep`, `cat` or `sed` through the shell. Run no build or test command unless your patterns (your tools line, or a pack or project grant) list it; the orchestrator runs the build and the tests after you. If you have a test pattern, run it as one command, in the foreground, only for your own test classes where it takes a filter, exactly as listed where it takes no arguments, with no pipe, redirect, background run, `sleep` or chained command."
     - **G5** (test-engineer): "Change files only with the Edit and Write tools, and read and search with Read, Grep and Glob; never a script, heredoc, redirect, `grep`, `cat` or `sed` through the shell. Run tests as one command from your patterns, in the foreground, only for your own test classes or files where the command takes a filter, and only with the options your patterns allow. If your patterns list the test command only exactly (the npm pack's `npm test`), run it exactly as listed, without arguments. To see the branch's committed work, run `git diff origin/<base>...HEAD -- <path>` (`<base>` is the pull request's base branch), and `git status --porcelain` and `git diff` for uncommitted changes; never `git show`, `git log` or `git -C`. No pipe, redirect, background run, `sleep` or chained command. If the output is still too long to read, report that; the orchestrator runs the full suite." The stack pack's documentation gives the filtered command (for example `dotnet test --project <X> --filter-class <Class> --no-progress`). Run `git fetch origin` yourself before you start G5: the test-engineer cannot fetch, so its diff against `origin/<base>` is only as current as your last fetch.
   - **G4:** pick the owner(s) by the paths the change touches, using the roster in `docs/ai/README.md` (generated from `routing.G4` in `.claude/boundaries.json` by `python .claude/scripts/roster.py`), and run several in sequence when a change spans lanes. A route whose owner is `main-session` names paths no listed agent may write (build logic, dependency manifests): make that part of the change yourself, and record it under "## G4 evidence". Start each with the same prompt shape (inputs: G1, G2, G3 artifacts). If the lane hook denies a write, the agent reports it and the orchestrator decides the owner; nobody works around a deny. Changing a lane or an agent's tools is its own issue, with G3 and G6. When they return, run the build and test commands yourself (they come from the stack pack or the project's addendum), paste the summary under "## G4 evidence", then set G4 to `passed` with a one-line note.
   - **G7:** draft the PR body under "## G7 PR body draft", following the project's PR template: summary, a **Rollback** line (how to undo it), a **Verification (local | CI)** table (what ran where, with counts), `Closes #<n>`, one line per package added (with justification), and a `Docs:` line at column 0 listing the docs this change updated (repository-relative paths, comma-separated) or `none: <reason>` (`gates.py` fails G7 without it), then set G7 to `passed`. Never push; give the owner the commit, push and PR commands.
   - After every agent: run `python .claude/scripts/gates.py <n>` and show the table. If the gate still fails, report why and stop.
3. **BLOCK:** G3 BLOCK goes back to the architect (or to the owner for a scope decision). G6 BLOCK goes back to the implementer with the findings, then G6 is re-run as a re-check (the reviewer edits statuses and the verdict line in place). This is the one re-check.
4. **Finish:** `python .claude/scripts/gates.py <n>` exits 0. Report the table and hand over to the owner.
5. **Usage capture:** when the owner confirms that the issue's last PR is merged and its branch deleted, ask whether
   another user account also worked on the issue (each account holds only its own session records). Run
   `python .claude/scripts/capture.py finish <n>`, with `--other-accounts` if one did, and report the lines it prints
   (status, message count and file name; nothing else). If another account worked on it, tell the owner to run the
   same command, with `--other-accounts`, under that account in its own clone. It writes the issue's capture, and one for usage outside any issue since the last capture. A
   `partial` or `missing` status is a gap, not an error. On exit 1 or 2, report the message to the owner and stop;
   never move, edit or delete a file in the capture folder.

## Rules
- Only the owner agent writes its artifact. Of the gate files, the orchestrator writes only the manifest; it never writes or edits a gate artifact or a verdict line to make a gate pass. Files that no listed agent may write (the shared `deny` in `.claude/boundaries.json`) are the main session's to change, in the issue's scope.
- Do not start an agent for a gate whose predecessors are not passed or approved as skipped.
- Resuming: re-run `/issue <n>`; the manifest shows what is done.
