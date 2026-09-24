# Agent sandbox (headless, write/egress allow-listed, no host secrets)

- Owner: devops · Last verified: 2026-09-24 (Docker 29.6.2, Docker Compose v5.3.1, Python 3.14.4, git 2.55.0, gitleaks 8.30.1, SDK 10.0.401 inside the sandbox)
- When to use: starting or using a Claude Code agent session on Marco's Windows 11 machine (ADR-0010, issue #36). Every agent session that changes code runs here, not natively on the host.

Read first: `docs/architecture/agent-sandbox.md` (the build spec) and `docs/security/threat-models/agent-sandbox.md` (what this protects against and what it does not). This runbook only documents *how*; it does not repeat *why*.

**What this claims, precisely** (word it this way, not more strongly): host secret stores (`%APPDATA%\Microsoft\UserSecrets`, `%USERPROFILE%\.claude`, `~/.ssh`, Git Credential Manager) are absent from the sandbox by construction; working-tree secret files present when the sandbox starts are refused by `precheck.py`; a secret file created on the host *after* the sandbox has started is not caught by that check; and the sandbox's own Claude Code API key is readable by the agent that uses it (bounded by a spend cap, see [Rotation](#rotation-g4-11)).

## Prerequisites

| Visual Studio 2026 | CLI |
| --- | --- |
| Docker Desktop (current), WSL2 backend, `wsl --update` run at least once — *Settings → Resources → WSL Integration* | `wsl --update`; `docker version` (Docker 26 or later) |
| Python 3.12+ on the host — *View → Terminal*, `python --version` | `python --version` |
| Git for Windows (current) — *View → Terminal*, `git --version` | `git --version` |
| gitleaks on the host (the sandbox precheck fails closed without it) — *View → Terminal*, `winget install Gitleaks.Gitleaks` | `winget install Gitleaks.Gitleaks`; verify with `gitleaks version` |
| A dedicated Anthropic Console workspace (e.g. `decisya-sandbox`) with a monthly spend limit, and an API key from it — console.anthropic.com, outside this repo | Same; no CLI equivalent |
| — (one-time, host environment) | `NoDefaultCurrentDirectoryInExePath=1`: *Settings → System → About → Advanced system settings → Environment Variables → User variables → New* (name `NoDefaultCurrentDirectoryInExePath`, value `1`), or `[Environment]::SetEnvironmentVariable('NoDefaultCurrentDirectoryInExePath','1','User')`. Stops Windows' current-directory-first executable search from running a planted `python.exe`/`git.exe` (T-05). |
| — (one-time, host environment) | `DECISYA_PYTHON`: same UI, value = the full path from `(Get-Command python).Source`. Every sandbox command below uses `& $env:DECISYA_PYTHON` so the interpreter is never resolved from `PATH` or the current directory. |
| — (one-time, host environment) | `DECISYA_SANDBOX_ANTHROPIC_API_KEY`: same UI, **paste the key value directly into the dialog** (a UI text field is not shell history); or from a terminal, without the value ever appearing on screen or in history: `$k = Read-Host -AsSecureString 'Sandbox key'; [Environment]::SetEnvironmentVariable('DECISYA_SANDBOX_ANTHROPIC_API_KEY', [Net.NetworkCredential]::new('', $k).Password, 'User')`. Deliberately **not** named `ANTHROPIC_API_KEY`, so a host Claude Code session never picks it up. |
| — (after setting any user environment variable above) | **Fully close and reopen VS 2026 or VS Code before using its integrated terminal**, or use a standalone terminal (Windows Terminal, PowerShell) instead. An editor's integrated terminal inherits the environment the editor itself started with; it does not re-read user environment variables set after the editor launched, even in a brand-new terminal tab. If you don't want to restart the editor, set the variable for the current terminal session only: `$env:DECISYA_SANDBOX_ANTHROPIC_API_KEY = [Environment]::GetEnvironmentVariable("DECISYA_SANDBOX_ANTHROPIC_API_KEY","User")` (run this once per terminal, after the User-scope variable has been set at least once via the steps above). |

## One-time setup

| # | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 1 | Set the three user environment variables above, then close and reopen VS 2026 and every terminal (environment variables are read at process start) | Same; open a new PowerShell window |
| 2 | Migrate build output out of the tree (G4-05): *File → Close Solution*, then in Explorer delete `bin\`, `obj\` and `.vs\` under the repo | `Get-ChildItem -Path src,tests -Recurse -Directory -Include bin,obj \| Remove-Item -Recurse -Force; Remove-Item .vs -Recurse -Force` |
| 3 | Reopen the solution and rebuild once on the host, to confirm the redirect works — *Build → Rebuild Solution* | `dotnet build -warnaserror` |
| 4 | Verify no build output is back in the tree — terminal only | `git status --ignored --porcelain` lists no `bin/`, `obj/` or `artifacts/` |
| 5 | In the Anthropic Console, create the dedicated workspace and set a monthly spend limit, then create the API key used above — console.anthropic.com | Same; no CLI equivalent |

## Starting a session

| # | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 1 | **Close the solution** (*File → Close Solution*). VS 2026 may stay open with no solution loaded. This is rule 1 of [Rules while an agent session runs](#rules-while-an-agent-session-runs-g4-03) — do it before every session, not only the first. | Same rule; VS 2026 is a separate program from the terminal you use next. |
| 2 | Open a terminal (Windows Terminal, or VS 2026 *View → Terminal* **with no solution open**) | Any terminal, repo root as the working directory |
| 3 | Bring the stack up (runs `precheck.py` first; refuses on a secret file, a planted executable, or a gitleaks finding) — terminal only | `& $env:DECISYA_PYTHON .devcontainer\sandbox.py up` |
| 4 | Start Claude Code headlessly inside the sandbox — terminal only | `& $env:DECISYA_PYTHON .devcontainer\sandbox.py claude` (add arguments after `claude`, e.g. `claude -p "..."`) |
| 5 | Work the issue as usual (`/issue <n>`, etc.) inside that terminal session | Same |

The equivalent raw command (`docker compose -p decisya-sandbox exec -e ANTHROPIC_API_KEY=$env:DECISYA_SANDBOX_ANTHROPIC_API_KEY workspace claude`) also works, from any directory. Prefer `sandbox.py claude`: it additionally refuses to start if a VS Code server process is already running inside `workspace` (see [Optional read-only attach](#optional-read-only-attach-g4-07)); the raw command does not enforce that.

### First run: workspace trust

The very first time Claude Code runs in a freshly created `decisya-sandbox-home` volume (a first `up`, or any time after a `reset`), `~/.claude.json` has no trusted project yet, and headless mode (`-p`, or any non-interactive flag) cannot show the interactive trust dialog to accept it. Claude Code then ignores the project's `.claude/settings.json` (its `permissions.allow` entries and hooks) for that run, printing a line such as "Ignoring 18 permissions.allow entries from .claude/settings.json: this workspace has not been trusted." That is expected on an untrusted workspace, not a misconfiguration.

| Visual Studio 2026 | CLI |
| --- | --- |
| — (terminal only) | The first time only: `& $env:DECISYA_PYTHON .devcontainer\sandbox.py claude` **with no `-p`/prompt argument**, so it starts interactively. Accept the workspace trust prompt it shows. Then exit (`Ctrl+D` or `exit`). |
| — (terminal only) | Every run after that, including `-p`, uses the trust decision recorded in `~/.claude.json` on the `decisya-sandbox-home` volume. |

A `sandbox.py reset` deletes `decisya-sandbox-home`, and with it the trust decision — repeat the interactive first run once after any `reset`.

## Rules while an agent session runs (G4-03)

1. **The VS 2026 solution stays closed** for the whole session (started above). VS 2026's own restore and design-time builds run on file change, not only on a deliberate build, so "review before building" is too late once the solution is open (T-03).
2. Before **reopening the solution**, any **host build, run or test**, `git commit`, or a **host Claude session**, run the review script and read its output and `git diff` first:

   | Visual Studio 2026 | CLI |
   | --- | --- |
   | — (terminal only) | `& $env:DECISYA_PYTHON .devcontainer\host-review.py` (add `--base <ref>` if the branch's base is not `main`) |

   A finding does not mean something malicious happened; it means "read this part of the diff before VS 2026 or git runs it". Exit code `0` means clean.
3. A **host** Claude session (for `.claude/**`, `CLAUDE.md`, `.devcontainer/**`, `.pre-commit-config.yaml`, `global.json` or `.vscode/**` changes only — everything else in the sandbox is read-only anyway) runs only on a tree where `git status --porcelain --ignored` shows no pending agent changes (commit, stash, or clean first), or on a fresh clone.
4. **Never attach a VS Code window while `claude` is running.** Run `attach-prep` first if you need the optional read-only attach (below).
5. If you edit an overlaid file on the host (`.pre-commit-config.yaml`, `global.json`, `CLAUDE.md`, anything under `.claude/`, `.devcontainer/` or `.vscode/`) while the sandbox is up, the container may keep the old content (a single-file bind is fixed to the file's inode). Run `down` then `up` again afterwards.

## Daily loop

| # | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 1 | Start the session (see above) | `sandbox.py up` then `sandbox.py claude` |
| 2 | Work the issue | — |
| 3 | End the session: `Ctrl+D` or `exit` inside the `claude` session | Same |
| 4 | Stop the stack (keeps `~/.claude`, `~/.nuget`, build output on the sandbox's own volumes) — terminal only | `& $env:DECISYA_PYTHON .devcontainer\sandbox.py down` |
| 5 | Run the host review script and read `git diff` (rule 2 above) — terminal only | `& $env:DECISYA_PYTHON .devcontainer\host-review.py` |
| 6 | Only once it is clean (or every finding is understood): reopen the solution, build, test, and commit as usual | Same |

## Egress allow-list

The current allow-list, source of truth in `.devcontainer/egress/allowlist-tls.txt` (port 443, CONNECT + SNI) and `.devcontainer/egress/allowlist-http.txt` (port 80, GET only, for NuGet certificate revocation) and, redundantly, `.devcontainer/managed-settings.json`'s `sandbox.network.allowedDomains` (only used if the built-in Claude Code sandbox is ever enabled, currently `"enabled": false`):

| Host | Port | Why |
| --- | --- | --- |
| `api.nuget.org` | 443 | `dotnet restore` / `dotnet tool restore` |
| `api.anthropic.com` | 443 | Claude Code model API |
| `platform.claude.com` | 443 | Interactive Claude Code startup's connectivity check (see [Troubleshooting](#troubleshooting)). Not needed by `-p` (headless prompt) mode, but needed once for the [First run: workspace trust](#first-run-workspace-trust) interactive session. Approved: Marco 2026-09-24. |
| `crl3.digicert.com`, `crl4.digicert.com`, `ocsp.digicert.com`, `crl.sectigo.com`, `ocsp.sectigo.com`, `s.symcb.com`, `s.symcd.com`, `ts-crl.ws.symantec.com`, `ts-ocsp.ws.symantec.com` | 80 | NuGet package-signature certificate revocation (CRL/OCSP), read from the egress deny log during a clean `dotnet restore`. Approved: Marco. |
| `www.microsoft.com`, path `/pkiops/` only (a `url_regex` rule in `squid.conf`, not a domain entry — the rest of that host stays denied) | 80 | Same, for Microsoft-issued certificates. |

Every host here is justified by a deny-log line or a failing command, recorded in `docs/ai/pipeline/36.md`'s G4 evidence, and approved by Marco — never add one yourself; ask first (same rule as the note under [Reviewing the egress log](#reviewing-the-egress-log) below). `claude.ai`, `console.anthropic.com` and `downloads.claude.ai` are deliberately **not** allow-listed (no interactive subscription login or auto-update path from inside the sandbox; see [Troubleshooting](#troubleshooting) and the removed `forceLoginMethod` entry above).

## Reviewing the egress log

Read this after any session that saw untrusted input (an issue body pasted from outside, a fetched dependency's README, etc.).

| Visual Studio 2026 | CLI |
| --- | --- |
| — (terminal only) | `docker compose -f .devcontainer\compose.yaml -p decisya-sandbox exec egress tail -n 200 /var/log/squid/access.log` (or `docker compose -f .devcontainer\compose.yaml -p decisya-sandbox logs egress`) |

Each line is `<timestamp> <duration> <client> <action>/<code> <bytes> <method> <host:port> - <hierarchy>/<upstream> -`. There is no request body, no query string and no URL path — only host names, status codes and byte counts (G4-16). An unexpected denied host is worth asking Marco whether to add to the allow-list (never add one yourself); an unexpected *allowed* host that you did not expect the agent to need is worth investigating first.

## Optional read-only attach (G4-07)

Only for reading code in a VS Code window; never while an agent session runs (T-07: an attached window trusts the remote enough to open host URLs and reach host git credentials). Unsupported if the VS Code server cannot install without a host that is not on the egress allow-list — that is acceptable, because nothing in issue #36 depends on this working.

| Visual Studio 2026 | CLI |
| --- | --- |
| N/A (this is a VS Code, not Visual Studio, feature) | `& $env:DECISYA_PYTHON .devcontainer\sandbox.py attach-prep` (refuses if a `claude` process is running; otherwise stops the stack and deletes the `decisya-sandbox-vscode` volume) |
| N/A | In VS Code: *Dev Containers: Reopen in Container*, pick `.devcontainer/devcontainer.json`. No extensions are installed by design. |
| N/A | Close the attach (*Dev Containers: Reopen Folder Locally*) before the next `sandbox.py claude` session. |

Host VS Code settings that must also be off, for the rare case this is used (`dev.containers.copyGitConfig: false`, `dev.containers.gitCredentialHelperConfigLocation: none` — set once, in your own VS Code user `settings.json`, not this repo's).

## Rotation (G4-11)

Rotate `DECISYA_SANDBOX_ANTHROPIC_API_KEY` on: suspected prompt injection, an unexpected egress denial in the log ([above](#reviewing-the-egress-log)), unexpected spend, or every 90 days.

| # | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 1 | Create a new key in the dedicated Console workspace — console.anthropic.com | Same |
| 2 | Update the user environment variable (same dialog as setup) | `[Environment]::SetEnvironmentVariable('DECISYA_SANDBOX_ANTHROPIC_API_KEY', '<new key>', 'User')` |
| 3 | Revoke the old key in the Console — console.anthropic.com | Same |
| 4 | In a **new** terminal: bring the stack down, then up | `sandbox.py down` then `sandbox.py up` |
| 5 | After a suspected injection only: also reset the sandbox's persistent state (below), because the home volume may hold planted `~/.claude.json`/transcript state | `sandbox.py reset` |

## Reset

Removes the sandbox's persistent volumes: `~/.claude` (including transcripts under `~/.claude/projects` — this volume is sensitive; never export or back it up), `~/.nuget/packages`, and the build output under `~/.decisya-build`. The next `up` recreates them from the image, so any user-level setting an agent changed for "next time" (cc:T-01/T-02) cannot survive a reset.

| Visual Studio 2026 | CLI |
| --- | --- |
| — (terminal only) | `& $env:DECISYA_PYTHON .devcontainer\sandbox.py reset` |

## Integration tests (until #41)

The sandbox has no Docker access at all (no socket, no `DOCKER_HOST`, no docker CLI) — that is issue #41's Docker sidecar, deliberately split out of #36. Until it ships:

| Visual Studio 2026 | CLI |
| --- | --- |
| Run integration tests (`Category=Integration`) on the host, with Docker Desktop running, or let CI run them | `dotnet test --filter-trait "Category=Integration"` on the host |
| Inside the sandbox, run only the unit and architecture lanes — terminal only | `& $env:DECISYA_PYTHON .devcontainer\sandbox.py claude` then, inside that session, `dotnet test --filter-not-trait "Category=Integration"` |

The repository currently has no test tagged `Category=Integration`, so this filter runs the full suite either way; re-verify this runbook's guidance once the first one lands.

The Aspire AppHost (`Decisya.AppHost`) also stays out of the sandbox's scope — run it on the host (F5, or `dotnet run --project src/Decisya.AppHost`), after rule 1 above (solution closed only while an agent session runs; AppHost needs the solution open).

## Verify

| Check | Expected |
| --- | --- |
| `docker compose -f .devcontainer\compose.yaml -p decisya-sandbox ps` | Exactly two services, `workspace` and `egress`, both `Up` |
| `& $env:DECISYA_PYTHON .devcontainer\sandbox.py claude` then, inside, `dotnet --version` | `10.0.401` (or later within the `10.0.1xx` band `global.json` allows) |
| Inside the session: `dotnet restore` then `dotnet build -warnaserror` | Exit `0` |
| Inside the session: `dotnet test --filter-not-trait "Category=Integration"` | Exits `0` once `tests/Decisya.SharedKernel.Tests/RepoPaths.cs` reads the `Decisya.RepoRoot` assembly metadata (see the note below); until then, exactly the two `RepoPaths`-dependent tests fail and every other test passes — this is the known, tracked gap, not a new failure. |
| Inside the session: `curl -sS -o /dev/null -w '%{http_code}' https://api.nuget.org/v3/index.json` | `200` |
| Inside the session: `curl -sS -m 10 https://example.com` | Fails (proxy denial) |
| `& $env:DECISYA_PYTHON .devcontainer\host-review.py` on a tree with no pending agent changes | `host-review: clean`, exit `0` |

**Known gap, not a defect in this runbook or the sandbox config**: `tests/Decisya.SharedKernel.Tests/RepoPaths.cs` currently finds the repo root by walking up from the test assembly's own output folder to `decisya.slnx`. Because build output now lives outside the tree (`%LOCALAPPDATA%\decisya\artifacts\...` on the host, `~/.decisya-build/artifacts` in the sandbox), that walk no longer reaches the repository, and the two tests that use `RepoPaths` fail with `Could not locate the repo root`. `Directory.Build.props` already emits an `AssemblyMetadata` item named `Decisya.RepoRoot` with the repo root's path for exactly this purpose; `RepoPaths.cs` needs to read `typeof(RepoPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()` for that key first, falling back to the existing walk-up. That change is test-engineer's, not this runbook's or `Directory.Build.props`'s.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `sandbox.py claude` (or the raw `docker compose exec` command) refuses to start with: *"This machine's managed settings require a first-party login, but an Anthropic-issued credential (ANTHROPIC_API_KEY…) is configured. A non-OAuth Anthropic credential cannot satisfy the org pin."* | `/etc/claude-code/managed-settings.json` had `"forceLoginMethod": "console"` baked into an earlier image. In Claude Code 2.1.273, that key pins an interactive OAuth Console login and rejects any API-key credential — the opposite of what it was added for (G4-11: a dedicated, spend-capped API key, never an interactive login). Fixed: the key has been removed from `managed-settings.json`. | Rebuild the image so the fix takes effect: `& $env:DECISYA_PYTHON .devcontainer\sandbox.py up` (rebuilds `workspace`). If you built the sandbox before this fix landed, this is the only step needed; no login host is or was reachable from inside `workspace` either way (`claude.ai`/`console.anthropic.com` are not on the egress allow-list), so removing this key does not reopen a subscription-login path. |
| `sandbox.py claude -p "..."` prints *"Ignoring N permissions.allow entries from .claude/settings.json: this workspace has not been trusted."* | Expected on the first run of a freshly created `decisya-sandbox-home` volume, or any run after a `reset` — see [First run: workspace trust](#first-run-workspace-trust). Headless mode cannot show the trust dialog. | Run `sandbox.py claude` once with no `-p` argument, accept the trust prompt, exit, then retry with `-p`. |
| A rebuilt `workspace` image still shows old `managed-settings.json` content | Docker layer cache reused the `COPY managed-settings.json` layer from before an edit. This should not happen (`--build` always re-evaluates whether the copied file changed), but if it does | `docker compose -f .devcontainer\compose.yaml -p decisya-sandbox build --no-cache workspace`, then `sandbox.py up` |
| Interactive `sandbox.py claude` (no `-p`) fails with *"Unable to connect to Anthropic services. Failed to connect to platform.claude.com: DEPTH_ZERO_SELF_SIGNED_CERT"* | Interactive Claude Code startup runs a connectivity check against `platform.claude.com` that cannot be disabled (code.claude.com/docs/en/network-config, "Required domains"); if it isn't on the egress allow-list, `egress` terminates the TLS connection at the ClientHello (T-09/T-10 SNI enforcement), which the client reports as a self-signed-cert error rather than a clean denial. Headless `-p` mode does not run this check. Fixed: `platform.claude.com` is on `allowlist-tls.txt` (Approved: Marco 2026-09-24). | Rebuild so the fix takes effect: `sandbox.py up`. If you still see this after rebuilding, confirm `platform.claude.com` is actually in `.devcontainer/egress/allowlist-tls.txt` and that `sandbox.py down` then `up` ran after the edit (rule 5 under [Rules while an agent session runs](#rules-while-an-agent-session-runs-g4-03)). |

## Rollback

| Situation | Visual Studio 2026 | CLI |
| --- | --- | --- |
| Stop the sandbox, keep its state | *File → Close Solution* not required (the sandbox is separate from VS 2026) | `sandbox.py down` |
| Undo persistent sandbox state (suspected injection, or just start clean) | — | `sandbox.py reset` |
| Undo the build-output migration (fall back to in-tree `bin`/`obj`, e.g. if U-29 in `docs/architecture/agent-sandbox.md` turns out to block VS 2026) | Edit `Directory.Build.props`: remove the `UseArtifactsOutput`/`ArtifactsPath` block, then *Build → Rebuild Solution* | Same edit, then `dotnet build -warnaserror`. This edit is itself something `host-review.py` flags (`msbuild-project-or-props-file`) if it comes from inside the sandbox — read the diff. |
| An egress host was wrongly allow-listed | Ask Marco before removing it; edit `.devcontainer/egress/allowlist-tls.txt` or `allowlist-http.txt`, then `sandbox.py down` and `up` | Same |
| Completely remove the sandbox (uninstall) | Delete `.devcontainer/` from the repo (host-review.py and CI would then need updating too — this is a larger change than this runbook covers) | `sandbox.py down`; `docker volume rm decisya-sandbox-home decisya-sandbox-vscode decisya-sandbox-empty decisya-sandbox-egress-logs`; `docker rmi decisya-sandbox-workspace:local decisya-sandbox-egress:local` |

## What the sandbox does not protect against

- **Agent-written code that Marco chooses to run on the host.** The read-only overlays and the build-output move close the *automatic* paths (T-01, T-02, T-04, T-05); a deliberate host build, run or commit of sandbox-written code still depends on the review discipline above (rules 1–2), not a technical control.
- **Upload-capable allow-listed hosts.** `api.nuget.org` (with an attacker's own NuGet API key) and `api.anthropic.com` (with an attacker's own Anthropic account) can both receive data if an agent is tricked into sending it there. The allow-list is intentionally short (no GitHub, no npm registry, no container registries) to shrink this.
- **A secret file created after the sandbox has started.** `precheck.py` only checks at start. Do not create `.env` or other secret files on the host while a session is running.
- **The sandbox's own Claude Code API key.** It is readable by the agent process that uses it (`ANTHROPIC_API_KEY` in that one process's environment). The spend limit on the dedicated Console workspace, and prompt rotation, bound the damage — see [Rotation](#rotation-g4-11).
- **Prompt injection.** Nothing here reduces the chance of it; the sandbox reduces what a successful injection can reach (no host secrets, no arbitrary egress, no automatic host code execution).
- **Lane boundaries inside the repository** (`.claude/boundaries.json`, `agent_boundaries.py`, `secret_guard.py`). These remain guardrails, not a security boundary, same as before issue #36 — they are about which files a named subagent is expected to touch, not about containing a compromised session.
