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
| Start Docker Desktop | Docker Desktop must be running (Postgres, Keycloak and Redis are containers) | same |
| Generate a dev password | none (use the CLI cell) | PowerShell: `$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b \| ForEach-Object { $_.ToString('x2') })` |
| Store it | *Solution Explorer* → right-click `Decisya.AppHost` → *Manage User Secrets* → add `"Parameters": { "dev-user-password": "<value>" }` | `dotnet user-secrets set "Parameters:dev-user-password" "<value>" --project src/Decisya.AppHost` |
| Start | F5 on `Decisya.AppHost` | `dotnet run --project src/Decisya.AppHost` |
| Verify healthy | Dashboard → *Resources*: `postgres`, `keycloak`, `redis` and `decisya-bff` are **Healthy** (Keycloak takes up to a minute on first start). `decisya-migrator` (issue #21) runs to completion before `decisya-api` starts and shows as **Finished**, not Healthy — that is expected | same |
| Log in through the BFF (#18) | In Firefox: `https://localhost:7200/bff/login`, then sign in as `dev-alice` with your dev password. You land back on `https://localhost:7200/` | none (the login is a browser flow) |
| Check the session | `https://localhost:7200/bff/me` shows `"isAuthenticated": true` and alice's claims, and no token. Firefox → *Web Developer Tools* → *Storage* → *Cookies*: the session cookie is `HttpOnly`, `Secure`, `SameSite=Strict` | `curl.exe -s https://localhost:7200/bff/me` without a cookie → `{"isAuthenticated":false}` |
| Call the API through the BFF (#19, #20) | `https://localhost:7200/api/whoami` shows alice's `userId` and `tenantId`. The BFF forwarded the call with her access token, and the API validated it. No token appears in the page | `curl.exe -s -o NUL -w "%{http_code}" https://localhost:7200/api/whoami` without a cookie → `401` |
| Check tenancy provisioning (#21) | `https://localhost:7200/api/tenancy/me` shows alice's tenant `id` (`tenant.id`) and membership role `"Owner"` (`membership.role`). Both are created on this call — her first call to a tenancy endpoint | `curl.exe -s -o NUL -w "%{http_code}" https://localhost:7200/api/tenancy/me` without a cookie → `401` |
| Check tenancy membership (#21) | `https://localhost:7200/api/tenancy/members` lists exactly one member: alice's own `userId`, with `role`: `"Owner"` | `curl.exe -s -o NUL -w "%{http_code}" https://localhost:7200/api/tenancy/members` without a cookie → `401` |
| Log out | Sign out from the app (a `POST /bff/logout` with the antiforgery header). Keycloak then asks "Do you want to log out?"; confirm. There is one extra click because the BFF never sends the ID token to the browser (#18, D3) | none |

Keycloak listens on port 8080 with **https** when this machine trusts the ASP.NET Core dev certificate (`dotnet dev-certs https --trust`, the default on a Visual Studio machine): Aspire terminates HTTPS for the container. Without a trusted dev certificate the same URLs use `http://`. The issuer in tokens follows the scheme (`https://localhost:8080/realms/decisya` here).

**Realm changes and resets.** Keycloak imports the realm only into an empty database. An existing realm is skipped at every restart. So:

- An edited `decisya-realm.json` reaches your local instance only after a volume reset (below). CI always tests the committed file on an empty container, so CI never misses a change.
- Resetting the AppHost's local secrets (for example a new dev password) also needs a volume reset: the database keeps the old role password, client secret and user passwords, and Keycloak then fails to authenticate.

**Postgres, Keycloak and Redis keep running after the AppHost stops.** They are Aspire *persistent* containers named `decisya-postgres`, `decisya-keycloak` and `decisya-redis`. Redis holds only the BFF's session tickets and has no volume, so stopping it signs everyone out and nothing else is lost. The next AppHost start reuses them, which is faster (no Keycloak first start) and guarantees only one Postgres ever uses `decisya-postgres-data`. Two Postgres servers on one volume corrupt it (`PANIC: could not locate a valid checkpoint record`). The AppHost tests use their own throwaway volume and never touch these containers.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Stop the containers when you're done for the day | Docker Desktop → *Containers* → stop `decisya-redis`, `decisya-keycloak`, then `decisya-postgres` | `docker stop decisya-redis decisya-keycloak decisya-postgres` |
| Reset the volume (after a realm edit or a secret reset) | Stop the AppHost → Docker Desktop → *Containers*: delete `decisya-keycloak` and `decisya-postgres` (and any old `keycloak-…`/`postgres-…`) → *Volumes*: delete `decisya-postgres-data` → start the AppHost | `docker rm -f decisya-keycloak decisya-postgres`, then `docker rm $(docker ps -aq --filter volume=decisya-postgres-data)` if anything is left, then `docker volume rm decisya-postgres-data`, then start the AppHost |

**Admin console (dev, loopback only).** `https://localhost:8080/admin/`, user `admin`, password shown under the dashboard's `keycloak-password` parameter. It is for local development on this machine only; the ports listen on loopback. Never copy the admin password, the client secret, a dashboard token or the dev password into issues, chats, commits or screenshots.

### Admin API: realm refresh and smoke test (issue #25)

Issue #25 adds the client mapper `realm-roles-access-token` to `deploy/keycloak/decisya-realm.json`. It puts a flat `roles` claim into the access token of the `decisya-bff` client. The API reads only that claim to find `platform-admin`. An existing local Keycloak volume does not import it (see "Realm changes and resets"). Without it, every `/api/admin` call returns 403, also for `dev-admin`. Pick one option.

**Option 1: reset the dev volume.** This deletes all local data: Keycloak (realm, users, sessions), tenancy, entitlements and audit. Only synthetic dev data is lost. The steps are the "Reset the volume" row above.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Reset | Same as the "Reset the volume" row above | Same as the "Reset the volume" row above |
| Start | F5 on `Decisya.AppHost` | `dotnet run --project src/Decisya.AppHost` |

**Option 2: add the mapper by hand.** Local data is kept. Use the admin console (loopback only, section 3) or `kcadm.sh`. Never type the admin password or a token on a command line.

| Step | VS 2026 (admin console in Firefox) | CLI (`kcadm.sh` in the container) |
| --- | --- | --- |
| Open | Read the password in the dashboard's `keycloak-password` parameter (type it, do not copy it into notes). Open `https://localhost:8080/admin/`, sign in as `admin`. Switch the realm selector to `decisya` | `docker exec -it decisya-keycloak bash` (a shell inside the container; all commands below run there) |
| Sign in | — | `/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user admin`. There is no `--password`: kcadm prompts for it, and the prompt does not echo. If the container serves only https, use `https://localhost:8080` |
| Find the client | *Clients* → `decisya-bff` → *Client scopes* → `decisya-bff-dedicated` | `/opt/keycloak/bin/kcadm.sh get clients -r decisya -q clientId=decisya-bff --fields id`. Note the `id` value (call it `<client-id>`) |
| Add the mapper | *Add mapper* → *By configuration* → *User Realm Role*. Name: `realm-roles-access-token`. Token Claim Name: `roles`. Claim JSON Type: `String`. Multivalued: **On**. Add to ID token: **Off**. Add to access token: **On**. Add to lightweight access token: Off. Add to userinfo: **Off**. Add to token introspection: **On**. Save | `/opt/keycloak/bin/kcadm.sh create clients/<client-id>/protocol-mappers/models -r decisya -s name=realm-roles-access-token -s protocol=openid-connect -s protocolMapper=oidc-usermodel-realm-role-mapper -s consentRequired=false -s 'config."claim.name"=roles' -s 'config."multivalued"=true' -s 'config."jsonType.label"=String' -s 'config."id.token.claim"=false' -s 'config."access.token.claim"=true' -s 'config."userinfo.token.claim"=false' -s 'config."introspection.token.claim"=true'`, then `exit` |
| Leave `realm-roles-id-token` alone | It stays as it is: ID token only | same |

The flags match the mapper in the realm file. Do not change `fullScopeAllowed` or the scope mappings: they limit `roles` to `tenant-user` and `platform-admin`.

Sign in again after the change. A session that started before it holds an access token without `roles`, so sign out first, or wait for the 5-minute token to be refreshed and sign in again.

**Verify the mapper.** The browser never receives a token (the BFF keeps them, section 3), so you cannot decode an access token in Firefox dev tools. Use one of these:

| Check | VS 2026 (admin console) | CLI |
| --- | --- | --- |
| Mappers tab | *Clients* → `decisya-bff` → *Client scopes* → `decisya-bff-dedicated`: `realm-roles-access-token` is listed next to `realm-roles-id-token`; open it and check *Add to access token* is On and *Add to ID token* is Off | `/opt/keycloak/bin/kcadm.sh get clients/<client-id>/protocol-mappers/models -r decisya --fields name` inside the container lists both names |
| Decoded access token | *Clients* → `decisya-bff` → *Client scopes* → *Evaluate*: user `dev-admin` → *Generated access token*. It has `"roles": ["platform-admin", …]` | none (use the smoke test below) |
| End to end | The smoke test below returns 204 for `dev-admin` | same |

**Smoke test: the trial call as `dev-admin` and `dev-alice`.** Route: `POST /api/admin/tenants/{tenantId}/trial`. It needs the session cookie and the antiforgery pair. Expected results: 204 for `dev-admin`, 403 for `dev-alice`. The 403 body has no `code`.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Start | F5 on `Decisya.AppHost`; wait for `decisya-api` and `decisya-bff` to be Healthy | `dotnet run --project src/Decisya.AppHost` |
| Create alice's tenant | In Firefox: `https://localhost:7200/bff/login`, sign in as `dev-alice`. Open `https://localhost:7200/api/tenancy/me`: that call creates the tenant. Copy `tenant.id` (call it `<alice-tenant-id>`) | none (browser flow) |
| Get the antiforgery pair | Open `https://localhost:7200/bff/me` once in the same tab. It sets the cookie `__Host-decisya-xsrf` (readable by script) and the secret cookie `__Host-decisya-af` (`HttpOnly`). The header `X-XSRF-TOKEN` must carry the value of `__Host-decisya-xsrf` | same cookies; see the `curl.exe` row |
| Send as alice (expect 403) | On a `https://localhost:7200` tab: *Web Developer Tools* → *Console* (Firefox asks you to type `allow pasting` first), run the snippet below. Result: `403` | see the next row |
| Send with `curl.exe` (alternative) | not needed | Cookies are `HttpOnly`, so copy the values from *Storage* → *Cookies* into a temp file `cookies.txt` (Netscape format), not onto the command line: `curl.exe -k -s -o NUL -w "%{http_code}" -X POST -b cookies.txt -H "X-XSRF-TOKEN: <xsrf-cookie-value>" https://localhost:7200/api/admin/tenants/<alice-tenant-id>/trial`. Delete `cookies.txt` afterwards. Drop `-k` if the dev certificate is trusted (`dotnet dev-certs https --trust`) |
| Switch user | Sign out (section 3, "Log out"), then open `https://localhost:7200/bff/login` and sign in as `dev-admin` | same |
| Send as admin (expect 204) | Open `https://localhost:7200/bff/me` once, then run the same snippet. Result: `204` | same `curl.exe` call with the admin's cookies |

Console snippet (replace the id):

```js
const tenantId = '<alice-tenant-id>';
const xsrf = decodeURIComponent(
  document.cookie.split('; ').find(c => c.startsWith('__Host-decisya-xsrf=')).split('=')[1]);
const r = await fetch(`/api/admin/tenants/${tenantId}/trial`, {
  method: 'POST',
  headers: { 'X-XSRF-TOKEN': xsrf },
  credentials: 'same-origin',
});
console.log(r.status);
```

Notes:

- Without the header the BFF refuses the call before it reaches the API. Without a session it returns 401.
- A second trial call for the same tenant returns 409 with code `entitlements.trial_already_used`. The 204 check is for the first call.
- Do not paste cookie values, the admin password or tokens into issues, chats or screenshots.

Not run by the writer: the Docker commands, `kcadm.sh` flags and the console steps above were written from the Keycloak and BFF code, not executed (unverified). The mapper flags above match `realm-roles-access-token` in `deploy/keycloak/decisya-realm.json`.

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
> Use the product-owner agent to write the story and acceptance criteria for issue 0.04 (SharedKernel money and time types), then hand it to backend-dev. (Normally `/issue <n>` picks the agents; see `docs/ai/README.md`.)
```

VS 2026: open the Claude panel, pick the repo, and send the same instruction. Agents and skills are discovered from `.claude/` automatically.

## Working order per issue

product-owner → architect → security-reviewer (threat delta) → backend-dev / platform-dev / identity-dev / frontend-dev (by path) → test-engineer → security-reviewer (diff) → you review and merge. One issue, one PR; comment `@claude` on the PR for the on-demand review workflow.
