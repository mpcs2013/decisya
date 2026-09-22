# Getting started (issue 0.01 → 0.03)

This scaffold contains no compiled code yet. Issues 0.01–0.03 create the solution; every step below shows the Visual Studio 2026 path and the CLI path.

## Prerequisites

| Tool | VS 2026 | CLI |
| --- | --- | --- |
| .NET 11 SDK (RC1, go-live) | **Visual Studio 2026 Insiders** channel is required for .NET 11 until GA (10 Nov 2026); install it side by side with your stable VS 2026 and select the RC1 SDK in the installer | Download 11.0.100-rc.1 from dotnet.microsoft.com/download/dotnet/11.0; `dotnet --version` must print `11.0.100-rc.1…` |
| Aspire 13.5 CLI | Not installed by VS; use the CLI path | `winget install Microsoft.Aspire.Cli` (or `irm https://aspire.dev/install.ps1 \| iex`), then `aspire --version` → 13.5.x and `aspire doctor` |
| Docker | Docker Desktop or Podman with the Docker API enabled | same |
| Node 26 (Current; becomes LTS 28 Oct 2026, EOL Apr 2029) | — | `node --version` → v26.x; `.node-version` in the repo pins it for fnm/nvm-windows |
| Claude Code | VS 2026 extension by Rishi Gulati (publisher `firish`) | `claude` in the repo root |
| gitleaks + pre-commit | — | `pip install pre-commit && pre-commit install --hook-type commit-msg --hook-type pre-commit` |

## 1. Create the solution (issue 0.01)

| VS 2026 | CLI |
| --- | --- |
| *File → New → Project → Blank Solution*, name `Decisya`, location = this folder | `dotnet new sln -n Decisya` |
| Add the existing `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `global.json` as *Solution Items* | already in place |
| *Manage NuGet Packages for Solution* → search `Microsoft.CodeAnalysis.BannedApiAnalyzers`, install to any project; the version lands in `Directory.Packages.props` | done in step 2 with `dotnet add … package`, which writes the CPM version |

Verify: `dotnet build -warnaserror` (empty solution builds green).

## 2. Aspire AppHost + ServiceDefaults (issue 0.03)

| VS 2026 | CLI |
| --- | --- |
| *Add → New Project → Aspire App Host*, name `Decisya.AppHost`, folder `src/`, framework **.NET 11** (VS 2026 Insiders) | `aspire new` (interactive picker → AppHost only, .NET 11, name `Decisya.AppHost`, output `src/`) — 13.5 templates set `AspireUseCliBundle=true`, so `dotnet run` and `aspire run` behave the same |
| *Add → New Project → Aspire Service Defaults*, name `Decisya.ServiceDefaults`, framework .NET 11 | `dotnet new aspire-servicedefaults -n Decisya.ServiceDefaults -o src/Decisya.ServiceDefaults -f net11.0` |
| *Add → New Project → Class Library*, `Decisya.SharedKernel` | `dotnet new classlib -n Decisya.SharedKernel -o src/Decisya.SharedKernel` |
| Add projects to solution via Solution Explorer | `dotnet sln add src/*/*.csproj` |
| *Manage NuGet Packages* per project (Postgres, Redis, Keycloak hosting; OTel packages) | `dotnet add src/Decisya.AppHost package Aspire.Hosting.PostgreSQL` etc. — each call pins the version in `Directory.Packages.props` |
| Set `Decisya.AppHost` as startup project, F5 | `dotnet run --project src/Decisya.AppHost` |

Verify: the Aspire dashboard opens (it prints the URL with a login token in the console) and shows Postgres, Redis and Keycloak resources.

If a template name above does not match what your SDK offers, run `dotnet new list aspire` and use the listed short name; report the exact output if it fails rather than guessing. Known 13.5 issue: `aspire new` may still scaffold `net10.0` when the 11 SDK is present (microsoft/aspire#19050) — check the csproj and set `<TargetFramework>net11.0</TargetFramework>` (Directory.Build.props already forces it).

Every `Aspire.*` package must be 13.5.0; mixing 13.4.x and 13.5.x fails at runtime with `TypeLoadException`. Record any package without a net11 build in the table in `docs/adr/0009-dotnet-11-rc-baseline.md`.

## 3. First Claude Code session

```text
claude
> Use the product-owner agent to write the story and acceptance criteria for issue 0.04 (SharedKernel money and time types), then hand it to backend-dev.
```

VS 2026: open the Claude panel, pick the repo, and send the same instruction. Agents and skills are discovered from `.claude/` automatically.

## Working order per issue

product-owner → architect → security-reviewer (threat delta) → backend-dev / frontend-dev → test-engineer → security-reviewer (diff) → you review and merge. One issue, one PR; comment `@claude` on the PR for the on-demand review workflow.
