<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #56 -->
# Threat delta: host development by default; agent sandbox optional (issue #56)

- Scope: ADR-0011 (Accepted, 2026-09-26), the G2 note `docs/architecture/host-development-default.md`, and the uncommitted `CLAUDE.md` and `.claude/hooks/secret_guard.py` docstring changes. This is a delta against `docs/security/threat-models/agent-sandbox.md` (cited as **sb:T-xx**) and `claude-config.md` (**cc:T-xx**). It is not a full model.
- The decision is Marco's and is recorded, not re-argued. This delta rates what the decision gives up and names the controls that bound it.
- Mode: G3, before any code. Change class docs-only. No Decisya application boundary changes.
- Reviewer: security-reviewer agent, 2026-09-26. Re-check 2026-09-26: C1 to C6 applied (uncommitted on `issue/56-host-development`, verified in `git diff`); FU-1 to FU-4 filed as #58 to #61; #62 filed for C6; C7 moved to #60.

## Verdict

**PASS-WITH-NOTES** (re-check, 2026-09-26). Marco's G3 decisions are applied as recorded in the manifest ("G3 decisions", "Applied by the main session"). No High remains without a mitigation: (a) and sb:T-12 are Medium now, and (b) is Low today, with #58 due before the SPA. The notes still open are listed under "Open items" below.

As first rated: the ADR states the accepted risk plainly and accurately, and the docstring now says what the guard is: on the host, nothing stands behind it. Three gaps were High as found: (a) a direct edit of `Directory.Packages.props`, (b) `npm install`/`npm ci` (latent until the SPA exists), and sb:T-12 (the subscription credential is readable on the host). Each one has either a config change that fits inside #56 or a follow-up issue below, so none is unmitigated. All other items are Medium or Low.

**Boundary statement (review 15, ruling 1):** #56 runs on the **host**, as its manifest records, and it has no G4. From now on the manifest records `host` or `sandbox` per issue. This delta rates the T-04 class (apphost T-04, sb:T-03: agent-written MSBuild or launch files run on the host) against the **host** boundary, because that is the default. On a host run, isolation plays no part in the mitigation. Only the inputs, the lanes, the #39 hook and review after the fact do.

## ADR-0010 threats on a host run

On a host run, TB6/SB7 (sandbox to host) does not exist. The agent writes and runs code as Marco, during the gate. The sb:T-01 to T-06 group was about content *crossing* into the host. That group becomes a single direct threat, **H-1**, below. ASVS is mapped by analogy, at section level, as in the parent models.

| ADR-0010 threat | Sandbox mitigation lost on a host run | New status | Residual |
| --- | --- | --- | --- |
| sb:T-01 pre-commit, sb:T-02 `global.json`/tool manifests, sb:T-04 git-ignored host files, sb:T-05 executable search, sb:T-06 nested `.git`, sb:T-15 symlinks | Read-only overlays, `host-review.py`, the review step before first run | Folded into **H-1**. The agent can already run any code as Marco through Bash, so these files add no new capability. They only add persistence (see sb:T-19). | Covered by H-1 |
| sb:T-03 MSBuild/NuGet/npm surface run by VS 2026 | "Solution closed" rule, `host-review.py` | Folded into **H-1**. The solution is now open, so design-time builds run agent edits immediately. Gaps (a), (b) and (d) are the supply-chain part. | Medium (accepted, ADR-0011) |
| sb:T-11 upload-capable egress, sb:T-09/T-10 proxy | Egress allow-list | No egress control on the host. Exfiltration is bounded only by what agents can read (sb:T-12, sb:T-14). | Medium (accepted) |
| sb:T-12 Claude credential | API key with a spend cap, no host `~/.claude` | The **subscription** OAuth credential in `%USERPROFILE%\.claude\.credentials.json` is readable from Bash and from build code. This is the case sb:T-12 rated **High**. | High as found. **Medium now** (C4 applied; the Bash side is #61). Guardrails only; accepted, ADR-0011. |
| sb:T-14 secret files, cc:T-04, cc:T-05 | Nothing mounted, so nothing to read; `precheck.py` | User-secrets, `.env`, `~/.ssh`, GCM and `gh` tokens are readable by Bash and build code. The secret guard and the Read denies are the only controls, and both are patterns. Gitleaks catches secrets committed to the tree, not exfiltration. | Medium (accepted). The user-secrets hold dev-only values. |
| sb:T-19 host Claude sessions | Clean-tree rule before a host session | Now the normal case. An agent can use Bash to write **git-ignored** files that later sessions load: `.claude/settings.local.json` (hooks and allow rules), `CLAUDE.local.md`, `.mcp.json`, nested `CLAUDE.md`. `git diff` does not show ignored files. | **Low** (C5 applied) |
| sb:T-24 Anthropic-side connectors | API-key login, so no claude.ai connectors | Under the subscription login, claude.ai connectors load into sessions and agents. This G3 run had two in context (Claude Docs, Postman). Both are channels outside every local control, for exfiltration and for injected instructions. | **Low** (C6 applied: `mcp__claude_ai_Claude_Docs`, `mcp__claude_ai_Postman` and `mcp__claude_ai_Microsoft_Learn` denied; the Postman use case moves to #62) |
| sb:T-20 workflows to CI | `host-review.py` flagged `.github/**` | Only `git diff` before commit remains. The FU-3 follow-up from #28 is unchanged. | Medium (unchanged; FU-3 from #28) |
| sb:T-22 detection | Proxy access log | Only transcripts and `.agent-logs/hooks.jsonl` remain. | Low (accepted) |
| cc:T-01, T-02, T-10 (lanes are guardrails, prompt injection) | Contained to the repository | Reopened for host runs, as ADR-0011 states. Bounded by the triggers: external content means sandbox, which is gap (e). | Medium (accepted) |
| sb:T-07, T-08, T-13, T-16, T-17, T-18, T-21, T-23, T-25, T-26 | n/a | These concern the sandbox's own machinery. They do not arise on a host run and still apply unchanged to sandbox runs. | Unchanged |

**H-1: agent-written and package-supplied code runs as Marco, with his secrets, his network and the Docker Desktop daemon.** STRIDE: E, T, I. ASVS by analogy: V15.2, V8.2, V13.3. Inherent High. Accepted by ADR-0011 on one premise: agents work from reviewed repository documents and first-party or already-pinned packages, and anything else triggers the sandbox. Residual **Medium**, if that premise holds. Gaps (a), (b) and (e) are where it can fail.

## The architect's gaps

| Gap | Rating (as found) | Why | Mitigation | Residual |
| --- | --- | --- | --- | --- |
| **(a)** A direct edit of `Directory.Packages.props` or `.csproj` goes around the #39 hook | **High** | `Directory.Packages.props` is in backend-dev's and devops's lanes. A new `PackageVersion` is restored and its `build/*.props`/`.targets`, analyzers and generators run on the next `dotnet build`, which is on the allow-list with no prompt. That is supply-chain code at Marco's privilege, during G4. A mistyped or invented package name is enough (no attacker in the repository needed). CPM makes the change visible, but only after it has run. A `.csproj` `VersionOverride` also goes around the central file. `<Exec>`/`<Import>` of local files is agent-written code and adds nothing beyond H-1. | **C1 (in #56):** remove `Directory.Packages.props` from the `backend-dev` and `devops` lanes in `.claude/boundaries.json`. A new package then goes through the main session, the same "report it, Marco runs it" rule as #39. **C2 (in #56, recommended):** add `.claude/settings.json` `permissions.ask` rules for `Edit`/`Write` of `Directory.Packages.props`, `**/NuGet.config`, `**/package.json`, `**/package-lock.json` and `**/.npmrc`. The main session in auto mode then still stops for Marco. Confirm the path syntax with one test Edit. **FU-2:** `CentralPackageVersionOverrideEnabled=false`, plus the pre-push check that ADR-0011 names. | **Medium** (C1 and C2 applied; `VersionOverride` and the pre-push check open in #59) |
| **(b)** `npm install`/`npm ci` run lifecycle scripts | **High** once the SPA exists. Today it is latent, because the tree has no `package.json`. | `Bash(npm ci*)` is on the `settings.json` allow-list, so it runs without a prompt for every session, agents included. frontend-dev owns `src/Decisya.Web/**`, including `package-lock.json`. `npm ci` from an agent-edited lockfile runs `preinstall`/`postinstall` of any package, as Marco. | **C3 (in #56):** remove `Bash(npm ci*)` from `permissions.allow`. **FU-1:** must land before or with the SPA scaffold issue. | **Low today** (C3 applied). Medium after #58, which must land before the SPA. |
| **(c)** The Read denies miss `%USERPROFILE%\.claude` and `~/.ssh` | Medium | These are guardrails only: Bash and build code can read the files anyway. The value is in stopping an injected or careless Read, Grep or Glob call. Denying all of `~/.claude/**` would break reading persisted tool output under `~/.claude/projects`, so the deny must be narrow. | **C4 (in #56):** add `Read(~/.claude/.credentials.json)`, `Read(~/.ssh/**)`, `Read(~/.git-credentials)` and `Read(~/AppData/Roaming/GitHub CLI/**)` to `permissions.deny`. **FU-4** covers the Bash side. | **Medium, accepted** (C4 applied; the Bash side is #61) |
| **(d)** CI's package gates run after the push | Medium | This is inherent to host-first work, and ADR-0011 already states it. The vulnerable-package gates catch known-bad versions, including GitHub malware advisories, but only after build-time code has run. The planned pre-push check does not change that either: it runs before the push, still after the host build. The only checkpoint before code runs is the moment a package is added, which C1 and C2 create. | C1 and C2. Runbook practice after a lockfile or `Directory.Packages.props` change: run `dotnet list package --vulnerable --include-transitive` or `npm audit` before the first build or `npm ci`. | **Medium, accepted** (ADR-0011; C1 and C2 applied; pre-push check in #59) |
| **(e)** The sandbox triggers and the manifest's host/sandbox record are not enforced | Medium | If the triggers are skipped, H-1's premise fails silently. Without the record, G3 and G6 cannot rate the boundary that was actually used (ruling 1). The trigger stays a judgement call, as option 4 of the ADR explains. The record, however, can be checked. | **C7 (optional in #56):** add a `- Runs on: host` / `- Runs on: sandbox (<trigger>)` line to the manifest template in `.claude/skills/issue/SKILL.md`, set at G0. **FU-3** adds the `gates.py` check. | **Medium** until #60 lands (C7 moved there), then Low |

## Changes inside #56 (status at re-check)

| Id | Change | Status |
| --- | --- | --- |
| C1 | `.claude/boundaries.json`: `Directory.Packages.props` removed from the `backend-dev` and `devops` lanes | **Applied.** Verified in `git diff`. No lane text for it in `.claude/agents/*.md`. |
| C2 | `.claude/settings.json` `permissions.ask`: Edit and Write of `/Directory.Packages.props`, `**/Directory.Packages.props`, `**/NuGet.config`, `**/nuget.config`, `**/package.json`, `**/package-lock.json`, `**/.npmrc` | **Applied.** Live probe: the `/Directory.Packages.props` form prompts. The `**/` forms have no observed probe (open item 1). |
| C3 | `Bash(npm ci*)` removed from `permissions.allow` | **Applied** |
| C4 | Read denies: `~/.claude/.credentials.json`, `~/.ssh/**`, `~/.git-credentials`, `~/AppData/Roaming/GitHub CLI/**` | **Applied.** Live probe: a read under `~/.ssh` was denied before any file access. |
| C5 | `docs/runbooks/issue-pipeline.md` step 7: `git status --porcelain --ignored`, both columns | **Applied** |
| C6 | `mcp__claude_ai_Claude_Docs`, `mcp__claude_ai_Postman`, `mcp__claude_ai_Microsoft_Learn` denied | **Applied.** Claude Docs and Postman disconnected at once. Replacement for Postman: #62. |
| C7 | `Runs on:` field in the manifest template | **Moved to #60** |

## Follow-ups (filed)

| Id | Issue | Covers | Linked rating |
| --- | --- | --- | --- |
| FU-1 | #58 | `agent.npm-install` rule; `.npmrc` `ignore-scripts=true` in the SPA scaffold | (b), High once the SPA exists. **Must land before or with the SPA scaffold.** |
| FU-2 | #59 | Pre-push check, CI parity, `CentralPackageVersionOverrideEnabled=false` | (a), (d) |
| FU-3 | #60 | Host/sandbox record in the manifest, plus `gates.py` check (includes C7) | (e) |
| FU-4 | #61 | Secret guard for host credential paths | sb:T-12 |
| — | #62 | Postman collection generated from the OpenAPI contract, replacing the live connector | sb:T-24 (keeps C6 in place) |

## Open items

1. **C2, `**/` probe:** confirm once that a subagent Edit of `src/**/package.json` (or a nested `Directory.Packages.props`) prompts. Until then, only the root form is proven. Low, because C1 already keeps project agents out of the root file.
2. **(b) ordering:** #58 must merge before or with the SPA scaffold issue. Otherwise (b) returns to High, and G3 of that issue should BLOCK.
3. **(e):** the host/sandbox record stays practice only until #60. Ruling 1 is met for #56 by its manifest line "Host session: … runs on the host".
4. **#62:** it must not reintroduce a live claude.ai connector. It generates collections offline from the contract.

## Notes on the #56 diff

- The `CLAUDE.md` bullets and the `secret_guard.py` docstring match the G2 note word for word and describe the boundary accurately. No objection.
- ADR-0011 "Controls that bound it" says the secret guard and Read denies stop reading "`.env` and user-secrets files by name". That is accurate. It should not be read as covering the host credential stores until C4 and FU-4 land.
