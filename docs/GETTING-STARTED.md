# Getting started (issue 0.01 → 0.03)

Issues 0.01–0.03 create the solution (`Decisya.AppHost`, `Decisya.ServiceDefaults`, `Decisya.SharedKernel` under `src/`); every step below shows the Visual Studio 2026 path and the CLI path.

## Prerequisites

| Tool | VS 2026 | CLI |
| --- | --- | --- |
| .NET 10 SDK (LTS) | Installed with the *ASP.NET and web development* workload of stable VS 2026 Community | `dotnet --list-sdks` shows a `10.0.1xx` entry; `global.json` rolls forward to the latest 10.0.x feature band |
| Aspire 13.5 CLI | Not installed by VS; use the CLI path | `winget install Microsoft.Aspire.Cli` (or `irm https://aspire.dev/install.ps1 \| iex`), then `aspire --version` → 13.5.x and `aspire doctor` |
| Docker | Docker Desktop or Podman with the Docker API enabled | same |
| Node 26 (Current; becomes LTS 28 Oct 2026, EOL Apr 2029) | — | `node --version` → v26.x; `.node-version` in the repo pins it for fnm/nvm-windows |
| Claude Code | VS 2026 extension by Rishi Gulati (publisher `firish`) | `claude` in the repo root |
| gitleaks | — | `winget install Gitleaks.Gitleaks`, then **close and reopen** Developer PowerShell (PATH is read at start), then `gitleaks version` |
| pre-commit (needs Python) | — | `pip install pre-commit`, then `pre-commit --version` (if not on PATH, use `py -m pre_commit …`); then in the repo, after `git init -b main`: `pre-commit install --hook-type pre-commit --hook-type commit-msg` and `pre-commit run --all-files` |

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
| *Add → New Project → Aspire App Host*, name `Decisya.AppHost`, folder `src/`, framework **.NET 10** | `aspire new` (interactive picker → AppHost only, .NET 10, name `Decisya.AppHost`, output `src/`) — 13.5 templates set `AspireUseCliBundle=true`, so `dotnet run` and `aspire run` behave the same |
| *Add → New Project → Aspire Service Defaults*, name `Decisya.ServiceDefaults`, framework .NET 10 | `dotnet new aspire-servicedefaults -n Decisya.ServiceDefaults -o src/Decisya.ServiceDefaults -f net10.0` |
| *Add → New Project → Class Library*, `Decisya.SharedKernel` | `dotnet new classlib -n Decisya.SharedKernel -o src/Decisya.SharedKernel` |
| Add projects to solution via Solution Explorer | `dotnet sln add src/*/*.csproj` |
| *Manage NuGet Packages* per project (Postgres, Redis, Keycloak hosting; OTel packages) | `dotnet add src/Decisya.AppHost package Aspire.Hosting.PostgreSQL` etc. — each call pins the version in `Directory.Packages.props` |
| Set `Decisya.AppHost` as startup project, F5 | `dotnet run --project src/Decisya.AppHost` |

Verify (issue #15: a trace for a health call):

| VS 2026 | CLI |
| --- | --- |
| Set `Decisya.AppHost` as startup project, F5; the dashboard opens in Firefox | `dotnet run --project src/Decisya.AppHost`, then open the dashboard URL with the login token printed in the console |
| Dashboard → *Resources*: `decisya-api` is **Running**, and its details show `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_SERVICE_NAME=decisya-api` | Same, in the dashboard |
| Open the `decisya-api` http endpoint in Firefox and append `/alive`; the response is `Healthy` | `curl.exe http://localhost:<port>/alive` (port from the resource's endpoint) |
| Dashboard → *Traces*: a `GET /alive` trace for `decisya-api` | Same, in the dashboard |
| Dashboard → *Console logs* → `decisya-api`: one-line JSON records whose `trace_id` matches that trace; `tenant_id` and `user_id` are `null` until the tenancy and auth issues | Same, in the dashboard |

`/health` and `/alive` are mapped only in the Development environment. Postgres, Redis and Keycloak are not resources yet; they appear once a later issue adds `builder.AddPostgres(…)`, `builder.AddRedis(…)` and `builder.AddKeycloak(…)` to `AppHost.cs`.

If a template name above does not match what your SDK offers, run `dotnet new list aspire` and use the listed short name; report the exact output if it fails rather than guessing.

Every `Aspire.*` package and the `Aspire.AppHost.Sdk/<version>` line in `Decisya.AppHost.csproj` must be on the same version (currently 13.5.4); mixing Aspire versions fails at runtime with `TypeLoadException`. Do not list `Aspire.Hosting.AppHost` in `Directory.Packages.props`: the SDK adds it implicitly (`NU1009`).

## 3. First Claude Code session

```text
claude
> Use the product-owner agent to write the story and acceptance criteria for issue 0.04 (SharedKernel money and time types), then hand it to backend-dev.
```

VS 2026: open the Claude panel, pick the repo, and send the same instruction. Agents and skills are discovered from `.claude/` automatically.

## Working order per issue

product-owner → architect → security-reviewer (threat delta) → backend-dev / frontend-dev → test-engineer → security-reviewer (diff) → you review and merge. One issue, one PR; comment `@claude` on the PR for the on-demand review workflow.
