# 0010. Run Claude Code agents in a network-isolated devcontainer

- Status: Accepted
- Date: 2026-09-23
- Deciders: Marco
- Tags: security, tooling

## Context and problem statement

Claude Code agents run natively on Marco's Windows 11 Home machine. There, the only controls are the `PreToolUse` hooks and the `settings.json` deny rules. Both are guardrails, not a boundary (threat model `docs/security/threat-models/claude-config.md`: T-01 Bash gets around the write boundaries, T-02 pattern-scoped `tools:` is not enforced, T-04 the secret guard is pattern-based, T-10 prompt injection). An agent can read `%APPDATA%\Microsoft\UserSecrets`, the host `.claude` credentials and any `.env` file. It can write anywhere Marco can, and it can reach any host on the internet. Issue #36 asks for a real boundary: writes outside the repository fail, secret files can't be read, egress is limited to an allow-list, and `dotnet build -warnaserror` and `dotnet test` stay green inside the boundary.

These facts constrain the choice. They come from the Claude Code docs and were reported by another agent, not verified here (see "Unverified assumptions" in `docs/architecture/agent-sandbox.md`):

- The built-in Claude Code sandbox runs on macOS, Linux and WSL2, not on native Windows. It restricts Bash, PowerShell and Monitor commands only. It does not restrict the Read, Write and Edit tools, and it does not restrict a subagent's non-Bash tools.
- Anthropic publishes a reference devcontainer that uses an iptables and ipset default-deny firewall. VS Code Dev Containers runs it on Windows through Docker Desktop with the WSL2 backend.
- Visual Studio 2026 has no official Claude Code integration and no documented devcontainer or WSL workflow.

Windows 11 Home has no Hyper-V Manager and no Windows Sandbox, so a full VM per session is not practical.

## Decision drivers

- **Security:** the boundary has to hold for every tool (Read, Write, Edit, Bash, WebFetch, MCP, subagents), not only for Bash. Host secrets must be absent, not just hidden by a pattern.
- **Testcontainers and Aspire need a Docker daemon.** Mounting the host Docker socket gives root on the host, which would undo the boundary.
- **Solo-developer time:** one command to start the sandbox, one tree shared with VS 2026, no second clone to keep in sync.
- **Robust egress control:** NuGet, npm and the container registries are served from CDNs whose IP addresses change. A filter keyed on IP addresses breaks restores at random.
- **Portability:** a standard `devcontainer.json` works with VS Code, the `devcontainer` CLI and GitHub Codespaces later.
- **Cost:** no paid Docker features. Enhanced Container Isolation needs Docker Business.

## Considered options

1. **Status quo: native Windows with hooks and deny rules only.** Rejected: there is no OS-level sandbox on native Windows, so T-01 and T-04 stay open by construction.
2. **A WSL2 distro with the built-in Claude Code sandbox.** Rejected as the primary boundary. The sandbox covers Bash only. The Read, Write and Edit tools and the Claude process's own egress (WebFetch, MCP) rely on pattern-based permission rules, which is the same class of control as T-04 and T-05. WSL2 also mounts `C:` at `/mnt/c` by default, which exposes `UserSecrets` and `%USERPROFILE%\.claude`. Its Docker Desktop integration hands the host daemon socket to the distro, so `docker run -v /mnt/c:/c` escapes.
3. **The Anthropic reference devcontainer as-is (in-container iptables) plus the host Docker socket.** Rejected. The socket is root on the host. The firewall filters by IP address, which is flaky for CDN-hosted NuGet, npm and registries, and it runs inside the container that the agent controls, with a sudo script and `NET_ADMIN`.
4. **A devcontainer with privileged Docker-in-Docker inside the workspace container.** Rejected: the agent user gets root in the workspace. With `NET_ADMIN` it can flush the firewall, and privileged mode lets it reach the Docker Desktop VM.
5. **A network-isolated devcontainer compose project** (chosen). It has three services on an `internal` Docker network with no route to the internet:
   - `workspace`: Claude Code, .NET 10 SDK, Node and Python. It runs as a non-root user without sudo or setuid (`no-new-privileges`).
   - `egress`: a forward proxy with a domain allow-list. It is the only container attached to an external network.
   - `docker`: an optional, rootless Docker-in-Docker sidecar for Testcontainers.

   The only host bind mount is the repository. `.git`, `.claude`, `CLAUDE.md`, `.devcontainer` and `.vscode` are mounted read-only on top of it. Claude credentials live in a sandbox-only named volume. The built-in Claude Code sandbox runs inside `workspace` as a second, per-command layer, but only if G4 proves that it works there without extra container privileges.
6. **Keep integration tests out of the sandbox entirely** (the host or CI runs them). This is not chosen as the default, but it stays as the documented fallback: the `docker` sidecar is a compose profile, and without it the sandbox runs `dotnet test --filter-not-trait "Category=Integration"`.

## Decision outcome

Chosen option: **5, the network-isolated devcontainer compose project, with the built-in sandbox as an optional inner layer and option 6 as the fallback for Docker**. The alternatives rely on rules the agent works around, on a socket that grants host root, or on IP filters. Option 5 enforces the three Done-when properties through structure instead:

- **Absent host paths.** Only the repository is mounted, and the paths that execute code on the host are read-only.
- **No route.** Egress goes only through a proxy that the agent can't reconfigure.
- **A separate, unprivileged daemon user for Docker.**

The design and the checklist for G4 are in `docs/architecture/agent-sandbox.md`.

### Consequences

- **Good:**
  - Every Claude Code tool and every subagent is inside the boundary, not only Bash.
  - User-secrets, host `.claude` credentials, `~/.ssh` and every file outside the repository are absent from the container.
  - The egress allow-list is keyed on domain names (NuGet, npm, GitHub, Anthropic, MCR, Docker Hub, Quay, the VS Code marketplace) and lives in one reviewed file.
  - VS 2026 and VS Code share one working tree.
  - The hooks stay in place as guardrails inside the boundary.
- **Bad:**
  - Three containers and a proxy for a solo developer to maintain.
  - Build I/O over the Windows bind mount is slower than on a native file system.
  - `bin/obj` have to be redirected in the sandbox (`Directory.Build.props`) so that Windows and Linux builds don't overwrite each other.
  - Agents can't commit or edit `.claude/**` inside the sandbox. Marco commits on Windows, and issues that change `.claude/**` or `.devcontainer/**` run in a supervised host session.
  - Running the Aspire AppHost inside the sandbox is out of scope, because a remote Docker host breaks Aspire's `localhost` endpoints. The AppHost keeps running on the host.
  - The `docker` sidecar still needs `privileged: true`, even though the daemon runs rootless. A container escape plus a local privilege escalation would reach the Docker Desktop VM. This residual risk is accepted, and G3 must assess it.
- **Residual, not solved by this ADR:**
  - Allow-listed domains that accept uploads with credentials an attacker supplies (npm publish, nuget push, GitHub, the Anthropic API) are still an exfiltration channel.
  - Code that an agent writes (MSBuild targets, npm scripts, tests) runs on the host when Marco builds it in VS 2026. Marco reviews it before building on the host.
  - Lane-level write boundaries inside the repository (T-01, T-02) are still guardrails.
- **Invariants:** no platform invariant in `CLAUDE.md` changes. After acceptance, the working agreements gain one line: "Agent sessions run in the sandbox (`docs/runbooks/agent-sandbox.md`); host sessions only for `.claude/**` or `.devcontainer/**` changes, in default permission mode."
- **Enforced by:**
  - The G4 verification checklist in `docs/architecture/agent-sandbox.md`, re-run whenever `.devcontainer/**` changes.
  - A `sandbox-config` check in `.claude/scripts/lint.py`: no `docker.sock` mount, no host bind mount except the repository, `privileged` only on the `docker` service, `workspace` only on the internal network, and base images pinned by digest.
  - `initializeCommand` refuses to start when a git-ignored secret file is in the working tree.

## Amendment 2026-09-23

Status stays **Accepted**. Option 5 (a network-isolated compose project, with the repository as the only host bind and a proxy as the only exit) is unchanged. The G3 threat model (`docs/security/threat-models/agent-sandbox.md`) led to four decisions by Marco that change how option 5 is realised. `docs/architecture/agent-sandbox.md` holds the build spec.

1. **Headless agent sessions.** Claude Code runs in a terminal inside `workspace`, started from any host terminal through the host launcher `.devcontainer/sandbox.py` (`docker compose … exec workspace claude`). No VS Code window is attached while an agent runs, and the `anthropic.claude-code` extension is not used. Reason: an attached VS Code window trusts the container, and that is an egress and credential bypass (threat T-07). `devcontainer.json` remains only for an optional attach to read code, which is mutually exclusive with agent sessions. The drivers "VS Code and VS 2026 share one working tree" and "portability to Codespaces" are therefore secondary for now.
2. **No Docker in #36.** The `docker` sidecar, the registry hosts and Testcontainers inside the sandbox move to issue **#41**. The residual "privileged sidecar" does not exist until #41 lands and is reassessed there. Until then, option 6 applies: integration tests run on the host or CI, and the sandbox runs `dotnet test --filter-not-trait "Category=Integration"`.
3. **A dedicated Anthropic Console API key with a monthly spend limit**, passed per `exec` from a host user environment variable. No claude.ai login in the sandbox.
4. **Host build output leaves the working tree.** `Directory.Build.props` uses the SDK artifacts layout: `%LOCALAPPDATA%\decisya\artifacts\<per-clone key>` on the host and `/home/vscode/.decisya-build/artifacts` in the sandbox. That way no git-ignored `obj/` file written in the sandbox is ever imported by host MSBuild (T-04). This replaces the Bad consequence "`bin/obj` redirected in the sandbox only".

Further G3 requirements now part of the design:

- read-only overlays for `.pre-commit-config.yaml` and `global.json`;
- a masked `.vs`;
- SNI-checked egress (squid peek/splice);
- a host review script and runbook rules for agent changes that host tools will run.

The egress allow-list is reduced to what #36 needs (NuGet, Anthropic, and CRL/OCSP only if proven), subject to Marco's approval at G4. So the Good consequence listing MCR, Docker Hub, Quay, the VS Code marketplace and GitHub no longer holds for #36. The `lint.py` `sandbox-config` rules in "Enforced by" are superseded by the parse-based table in the architecture note.

## Amendment 2026-09-24

Status stays **Accepted**, and option 5 is unchanged. G4 changed these decisions, and Marco approved each change (evidence in `docs/ai/pipeline/36.md`):

- **Egress allow-list.** It now also holds `platform.claude.com:443`, which Claude Code's interactive startup connectivity check requires and which can't be disabled. It also holds the NuGet revocation hosts on port 80, GET only: the 9 CA hosts from the deny log, plus `www.microsoft.com` limited to `/pkiops/`.
- **The login control moves off the network.** `platform.claude.com` also serves the OAuth token endpoint, so a claude.ai subscription login is no longer blocked there. Amendment item 3 ("no claude.ai login in the sandbox") is now enforced by two things: `sandbox.py` refuses to start while `~/.claude/.credentials.json` exists in the sandbox, and the runbook rule never to run `/login` there.
- **`forceLoginMethod` is dropped from managed settings.** It pinned OAuth in the pinned Claude Code and rejected the API key.
- **Accepted residuals.** Marco accepted T-09 "no SNI": the connection falls back to the allow-listed CONNECT host, and an SNI mismatch is still terminated. He also accepted the missing proxy-side DNS capture, based on the config and the probes.

Follow-ups:

- #41: the Docker sidecar.
- #39: the parse-based lint.
- #28: the CI key in a GitHub Environment.
- #42, #43, #44: the G6 Low items.

Details are in `docs/architecture/agent-sandbox.md`.
