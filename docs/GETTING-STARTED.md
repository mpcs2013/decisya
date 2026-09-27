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

Do not copy or paste the resource's environment details from the dashboard (into issues, chats or screenshots): they include the OTLP API key (`OTEL_EXPORTER_OTLP_HEADERS`).

`/health` and `/alive` are mapped only in the Development environment. Postgres and Keycloak are added in section 3 (issue 0.05); Redis comes with a later issue.

If a template name above does not match what your SDK offers, run `dotnet new list aspire` and use the listed short name; report the exact output if it fails rather than guessing.

Every `Aspire.*` package and the `Aspire.AppHost.Sdk/<version>` line in `Decisya.AppHost.csproj` must be on the same version (currently 13.5.4); mixing Aspire versions fails at runtime with `TypeLoadException`. Do not list `Aspire.Hosting.AppHost` in `Directory.Packages.props`: the SDK adds it implicitly (`NU1009`).

## 3. Keycloak (issue 0.05)

The AppHost now starts Postgres (Keycloak's database) and Keycloak with the `decisya` realm imported from `deploy/keycloak/decisya-realm.json`. The realm's secrets are placeholders that Keycloak fills from environment variables at import. The BFF client secret and Keycloak's database password are generated automatically on first run. You set the dev-user password once per clone. Agents never read or write the local secret store (CLAUDE.md).

The dev-user password is also the password of the seeded dev users `dev-alice`, `dev-bob` (two tenants) and `dev-admin` (`platform-admin`, no tenant). Use letters, digits, `_` or `-` only: the AppHost refuses to start otherwise, and when the value is missing.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Start Docker Desktop | Docker Desktop must be running (Postgres and Keycloak are containers) | same |
| Generate a dev password | none (use the CLI cell) | PowerShell: `$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b \| ForEach-Object { $_.ToString('x2') })` |
| Store it | *Solution Explorer* → right-click `Decisya.AppHost` → *Manage User Secrets* → add `"Parameters": { "dev-user-password": "<value>" }` | `dotnet user-secrets set "Parameters:dev-user-password" "<value>" --project src/Decisya.AppHost` |
| Start | F5 on `Decisya.AppHost` | `dotnet run --project src/Decisya.AppHost` |
| Verify healthy | Dashboard → *Resources*: `postgres` and `keycloak` are **Healthy** (Keycloak takes up to a minute on first start) | same |
| Open the login page (the Done-when) | In Firefox: `https://localhost:8080/realms/decisya/protocol/openid-connect/auth?client_id=decisya-bff&response_type=code&scope=openid&redirect_uri=https%3A%2F%2Flocalhost%3A7200%2Fsignin-oidc&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeKdcaUlVRNHuA0Q9BSM&code_challenge_method=S256&state=dev` (the RFC 7636 Appendix B example challenge) | `curl.exe -s -o NUL -w "%{http_code}" "<same URL>"` → `200` |
| Log in | `dev-alice` with your dev password. Firefox then shows a connection error on `https://localhost:7200/signin-oidc?...&code=...`; the `code=` in the address bar is the proof. The BFF that receives it arrives in #18 | none |

Keycloak listens on port 8080 with **https** when this machine trusts the ASP.NET Core dev certificate (`dotnet dev-certs https --trust`, the default on a Visual Studio machine): Aspire terminates HTTPS for the container. Without a trusted dev certificate the same URLs use `http://`. The issuer in tokens follows the scheme (`https://localhost:8080/realms/decisya` here).

**Realm changes and resets.** Keycloak imports the realm only into an empty database. An existing realm is skipped at every restart. So:

- An edited `decisya-realm.json` reaches your local instance only after a volume reset (below). CI always tests the committed file on an empty container, so CI never misses a change.
- Resetting the AppHost's local secrets (for example a new dev password) also needs a volume reset: the database keeps the old role password, client secret and user passwords, and Keycloak then fails to authenticate.

**Postgres and Keycloak keep running after the AppHost stops.** They are Aspire *persistent* containers named `decisya-postgres` and `decisya-keycloak`. The next AppHost start reuses them, which is faster (no Keycloak first start) and guarantees only one Postgres ever uses `decisya-postgres-data`. Two Postgres servers on one volume corrupt it (`PANIC: could not locate a valid checkpoint record`). The AppHost tests use their own throwaway volume and never touch these containers.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Stop Postgres and Keycloak when you're done for the day | Docker Desktop → *Containers* → stop `decisya-keycloak`, then `decisya-postgres` | `docker stop decisya-keycloak decisya-postgres` |
| Reset the volume (after a realm edit or a secret reset) | Stop the AppHost → Docker Desktop → *Containers*: delete `decisya-keycloak` and `decisya-postgres` (and any old `keycloak-…`/`postgres-…`) → *Volumes*: delete `decisya-postgres-data` → start the AppHost | `docker rm -f decisya-keycloak decisya-postgres`, then `docker rm $(docker ps -aq --filter volume=decisya-postgres-data)` if anything is left, then `docker volume rm decisya-postgres-data`, then start the AppHost |

**Admin console (dev, loopback only).** `https://localhost:8080/admin/`, user `admin`, password shown under the dashboard's `keycloak-password` parameter. It is for local development on this machine only; the ports listen on loopback. Never copy the admin password, the client secret, a dashboard token or the dev password into issues, chats, commits or screenshots.

## 4. Running tests

Plain `dotnet test` runs everything, including the Keycloak/Postgres integration suite (Testcontainers, about 7 minutes) and the host-only AppHost tests. For everyday work, run only the fast tests; CI runs the integration suite on every PR.

| When | VS 2026 | CLI | Time |
| --- | --- | --- | --- |
| While coding | *Test Explorer* → *Group By* → *Traits* → run everything except the `Category [Integration]` and `Category [AppHost]` groups | `dotnet test --filter-not-trait "Category=Integration" --filter-not-trait "Category=AppHost"` | seconds |
| Before a PR that touches Keycloak or Postgres | *Test Explorer* → run the `Category [Integration]` group (Docker Desktop running) | `dotnet test --project tests/Decisya.Identity.Tests --filter-trait "Category=Integration"` | ~7 min |
| After changing the AppHost | *Test Explorer* → run the `Category [AppHost]` group (Docker Desktop running; the dev-user password set, section 3) | `dotnet test --project tests/Decisya.AppHost.Tests --filter-trait "Category=AppHost"` | ~1 min |
| On push | automatic: the pre-push hook runs the "while coding" filters, plus commit-message and secret checks | automatic | under a minute |
| Every PR | automatic: CI runs the unit tests and the integration suite | automatic | a few minutes |

A new project with `Category=Integration` tests must also be added to the CI Integration step's project list (`.github/workflows/ci.yml`); a `.claude` test fails until it is.

## 5. First Claude Code session

```text
claude
> Use the product-owner agent to write the story and acceptance criteria for issue 0.04 (SharedKernel money and time types), then hand it to backend-dev.
```

VS 2026: open the Claude panel, pick the repo, and send the same instruction. Agents and skills are discovered from `.claude/` automatically.

## Working order per issue

product-owner → architect → security-reviewer (threat delta) → backend-dev / frontend-dev → test-engineer → security-reviewer (diff) → you review and merge. One issue, one PR; comment `@claude` on the PR for the on-demand review workflow.
