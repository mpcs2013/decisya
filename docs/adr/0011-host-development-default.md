# 0011. Host development by default; the agent sandbox is optional

- Status: Accepted
- Date: 2026-09-26
- Deciders: Marco
- Tags: security, tooling
- Partly supersedes: [ADR-0010](0010-agent-sandbox-devcontainer.md) (the rule that every agent session that changes code runs in the sandbox)

## Context and problem statement

ADR-0010 made the network-isolated devcontainer the place where every agent session that changes code runs. It was built because on native Windows the only controls on Claude Code are the `PreToolUse` hooks and the `settings.json` deny rules, which are guardrails, not a boundary (threat model `docs/security/threat-models/claude-config.md`: T-01, T-02, T-04, T-10). On the host, an agent and any code it runs can read `%APPDATA%\Microsoft\UserSecrets`, `%USERPROFILE%\.claude`, `~/.ssh` and the Git and `gh` credentials, write anywhere Marco can, and reach any host. The built-in Claude Code sandbox does not run on native Windows. The sandbox removes those host paths by construction, limits egress to a short allow-list, and puts a review step (`host-review.py`, solution closed) between agent-written code and its first run on the host.

Issue #15 was the first feature issue with code gates (G4, G5). Its costs showed up there (`docs/ai/pipeline/15.md`, Decisions):

- **Money:** the sandbox uses a dedicated, spend-capped Anthropic Console API key (ADR-0010 amendment item 3), billed per token on top of Marco's Claude subscription, which host sessions already use.
- **Friction:** the VS 2026 solution stays closed for the whole session, `host-review.py` runs before every reopen, build, test or commit, sessions are headless only, and `.claude/**`, `CLAUDE.md` and `.devcontainer/**` are read-only inside.
- **Work that needs the host anyway:** the Aspire AppHost does not run in the sandbox (ADR-0010, Bad consequences), so #15's AppHost test and its Done-when dashboard check ran on the host regardless. Testcontainers in the sandbox needs the opt-in Podman sidecar (#41), which carries its own Medium residual (T-41-01) and differs from CI's Docker with Ryuk.
- **Low risk for this kind of issue:** #15's only new package was Microsoft's `Aspire.Hosting.Testing`, the #39 hook stops project agents from adding packages by command, and the agents worked only from the repository's own G1 to G3 documents.

Marco therefore ran #15's G4 and G5 on the host as a recorded exception, and the G6 review accepted it with conditions (`docs/security/reviews/15.md`, ruling 1): run `host-review.py` and read `git diff` before commit, and have #56's threat model state whether G4 runs in the sandbox or on the host. This ADR turns that exception into the standing rule.

## Decision drivers

- **Security:** the sandbox's value is highest where untrusted input enters, meaning a new third-party package (supply chain) or external text read by an agent (prompt injection). It is lowest when agents work only from reviewed repository documents and first-party packages.
- **Cost:** no second paid credential for routine work.
- **Solo-developer time:** one environment (VS 2026, VS Code, Claude Code on the host), the solution open, and no review ritual before every build.
- **Parity:** the AppHost, Aspire tests and Testcontainers run against the same engine as CI (Docker with Ryuk) and the same place as Marco's manual checks.
- **Keep the option:** the sandbox (#36, #41) is built and verified; it stays available for the cases where it pays for itself.

## Considered options

1. **Sandbox always (ADR-0010 as written).** Rejected: it charges the API-key cost and the per-session friction to every issue, including low-risk ones like #15. Aspire and AppHost work still has to run on the host, so the isolation is partial exactly where most platform work sits.
2. **Host always; retire the sandbox.** Rejected: it drops the only real boundary for the two cases where it matters most (a new third-party package, external content fed to an agent), and throws away working, verified infrastructure.
3. **Host by default; sandbox optional and recommended on named triggers** (chosen).
4. **Host by default; sandbox mandatory on the same triggers.** Not chosen: whether an input counts as "external content" is a judgement call that no hook or lint can detect. A rule that can't be checked would be a recommendation anyway, so Marco decides per issue and the manifest records it.

## Decision outcome

Chosen option: **3, host by default with the sandbox optional**, because it keeps the boundary available for the high-risk cases without charging its cost to every issue.

1. **Development runs on the host**: VS 2026, VS Code, and Claude Code on Marco's subscription, including the agents' code gates G4 and G5.
2. **The ADR-0010 sandbox stays available and optional.** It is **recommended** when an issue:
   - brings in a new third-party package (NuGet, npm, .NET tool or container image), or
   - feeds external content to an agent: web pages, third-party issues or PRs, package READMEs.
3. **The issue manifest records where the code gates ran** (`host`, or `sandbox` with the trigger), set at G0 and confirmed at G4. G3 rates T-04-class threats against that boundary, and G6 checks that the record matches (the #15 G6 condition).
4. **After a sandbox run**, the ADR-0010 rules still apply in full: the solution stays closed while the agent runs, and `host-review.py` plus a `git diff` read come before reopening the solution, any host build, run or test, a commit, or a host Claude session. After a host run, the normal `git diff` review before commit applies.
5. **The subscription-token login for the sandbox is dropped.** The sandbox keeps the dedicated, spend-capped API key (ADR-0010 amendment item 3), and `sandbox.py` still refuses to start while `~/.claude/.credentials.json` exists inside it.
6. **Out of scope and unchanged:** the sandbox's design, amendments, procedures and its `lint.py` `sandbox-config` checks; the #41 amendment's status; the hooks' role as guardrails.

### Consequences

- **Good:**
  - No per-token API spend for routine work; one credential, the subscription.
  - The solution stays open, host sessions can change `.claude/**` and `CLAUDE.md` without a special exception, and no review ritual is needed before each build.
  - The AppHost, `Category=AppHost` tests and Testcontainers run where the agents run, on Docker Desktop, the same engine family as CI.
  - The sandbox's cost is paid only on issues where untrusted input actually enters.
- **Bad (the accepted risk, stated plainly):**
  - **Build-time and test-time code runs as Marco's user, with the host's secret stores and network.** This covers MSBuild `.props`/`.targets` (including those shipped inside NuGet packages), analyzers and source generators, npm lifecycle scripts, test code, and the AppHost with DCP. That code can read user-secrets, `%USERPROFILE%\.claude` (the subscription credential), `~/.ssh`, the Git Credential Manager and `gh` tokens, and browser profiles. It can reach the Docker Desktop daemon, which is root on the VM that mounts all of `C:`, and it can reach any host on the internet. No boundary contains a compromised package or a successful prompt injection on a host run.
  - **The agents' own tools are bounded only by guardrails.** Read, Write, Edit, WebFetch and Bash on the host are limited by permission rules and the hooks (claude-config T-01, T-02, T-04, T-05 and T-10 are open again for host runs). The secret guard is pattern-based, and the `settings.json` Read denies do not cover `%USERPROFILE%\.claude` or `~/.ssh`.
  - **No review step before the first run.** On a host run, agent-written code runs during the gate itself, so the `git diff` review before commit catches problems after the code has already executed, not before. `host-review.py`'s patterns protect only sandbox runs.
  - **The #39 package hook has known gaps.** It blocks `dotnet add … package`, `dotnet package add|update`, `dotnet nuget add`, `dotnet tool|workload|new install` and `aspire add|update` for project agents. It does not block a direct edit of `Directory.Packages.props` or a `.csproj` (inside backend-dev's and devops's write lanes), `npm install` or `npm ci` (which run lifecycle scripts), or anything the main session does after Marco's permission prompt.
  - **The triggers are judgement, not a check.** Choosing the host for an issue that deserved the sandbox is possible and would not be caught automatically.
  - **A Dependabot bump runs new package code on the next host build** once merged, so reviewing those PRs becomes part of the boundary.
- **Controls that bound it:**
  - **Central package management:** every version is declared in `Directory.Packages.props`, and transitive pinning is on, so a new or changed package is always a visible one-file diff.
  - **Dependabot review:** Marco reviews every Dependabot PR before merging.
  - **The #39 hook:** project agents cannot add packages or install tools by command.
  - **The secret guard and the Read deny rules:** they stop agents reading `.env` and user-secrets files by name, in every session.
  - **gitleaks:** it runs on every commit and in CI, and catches secrets written into the tree (not exfiltration).
  - **CI's vulnerable-package gates:** a High or Critical advisory on a direct or transitive NuGet package fails the build, and so does `npm audit --audit-level=high` for the SPA. These run after the push, so they catch known-bad versions, not code that has already run on the host.
  - **Planned pre-push check (a separate issue, to be filed):** it runs `host-review.py`-style patterns over the pushed range. It flags changes to `Directory.Packages.props`, `package.json` or lock files, `.props`/`.targets` files, a `.csproj` with `Import`, `Exec`, `UsingTask` or `VersionOverride`, and analyzer references.
- **Invariants:** no platform invariant in `CLAUDE.md` changes. Two working-agreement bullets change: the sandbox is optional with named triggers, and `host-review.py` applies after sandbox runs. The security principles are unchanged, including "hooks are guardrails, not a security boundary"; on the host, nothing else stands behind them.
- **Enforced by:**
  - `Directory.Packages.props` (`ManagePackageVersionsCentrally`, `CentralPackageTransitivePinningEnabled`): restore fails for a `PackageReference` with no central version (NU1010) and for an inline `Version` (NU1008).
  - `.claude/hooks/agent_boundaries.py` rules `agent.dotnet-package`, `agent.dotnet-install`, `agent.dotnet-nuget` and `agent.aspire-add`, with unit tests under `.claude/tests` that run in CI's `claude-config` job.
  - `.claude/hooks/secret_guard.py` and the `Read(...)` deny rules in `.claude/settings.json`, for every session (a guardrail).
  - gitleaks, in `.pre-commit-config.yaml` and CI's `Secret scan` step.
  - CI's `Vulnerable packages` step (`dotnet list package --vulnerable --include-transitive`, High and Critical fail) and the `npm audit --audit-level=high` line in `SPA lint, audit, build`.
  - When the sandbox is used: everything ADR-0010 and its amendments list, including `lint.py` `sandbox-config`, `precheck.py` and `host-review.py`.
- **Not enforced (practice only, until the named follow-up lands):**
  - the sandbox triggers;
  - the manifest's host-or-sandbox record (follow-up: a field in the `issue` skill's manifest template, and a `gates.py` check that it is present once G4 has started);
  - Dependabot review;
  - the pre-push check (not built yet).
