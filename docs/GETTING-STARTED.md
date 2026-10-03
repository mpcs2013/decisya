# Getting started (issue 0.01 → 0.03)

Issues 0.01–0.03 create the solution (`Decisya.AppHost`, `Decisya.ServiceDefaults`, `Decisya.SharedKernel` under `src/`); every step below shows the Visual Studio 2026 path and the CLI path.

Smoke checks by issue:

- [Issues #18, #19, #20, #21: login, API call, tenancy rows in the Keycloak table](#3-keycloak-issue-005)
- [Issue #25: Admin API smoke check](#issue-25-admin-api)
- [Issue #26: SPA shell (build, run, E2E)](#issue-26-spa-shell)

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
| Log in through the BFF (#18) | In Firefox: `https://localhost:7200/bff/login`, then sign in as `dev-alice` with your dev password. You land back on `https://localhost:7200/`, the SPA shell (#26), which shows you as signed in. The shell is served only after `npm run build` (see "Issue #26" below); without a build, `/` returns 404. Open `/bff/me` or `/api/tenancy/me` to see you are signed in | none (the login is a browser flow) |
| Check the session | `https://localhost:7200/bff/me` shows `"isAuthenticated": true` and alice's claims, and no token. Firefox → *Web Developer Tools* → *Storage* → *Cookies*: the session cookie is `HttpOnly`, `Secure`, `SameSite=Strict` | `curl.exe -s https://localhost:7200/bff/me` without a cookie → `{"isAuthenticated":false}` |
| Call the API through the BFF (#19, #20) | `https://localhost:7200/api/whoami` shows alice's `userId` and `tenantId`. The BFF forwarded the call with her access token, and the API validated it. No token appears in the page | `curl.exe -s -o NUL -w "%{http_code}" https://localhost:7200/api/whoami` without a cookie → `401` |
| Check tenancy provisioning (#21) | `https://localhost:7200/api/tenancy/me` shows alice's tenant `id` (`tenant.id`) and membership role `"Owner"` (`membership.role`). Both are created on this call — her first call to a tenancy endpoint | `curl.exe -s -o NUL -w "%{http_code}" https://localhost:7200/api/tenancy/me` without a cookie → `401` |
| Check tenancy membership (#21) | `https://localhost:7200/api/tenancy/members` lists exactly one member: alice's own `userId`, with `role`: `"Owner"` | `curl.exe -s -o NUL -w "%{http_code}" https://localhost:7200/api/tenancy/members` without a cookie → `401` |
| Log out | Click **Sign out** in the shell (it sends `POST /bff/logout` with the antiforgery header). Keycloak then asks "Do you want to log out?"; confirm. There is one extra click because the BFF never sends the ID token to the browser (#18, D3) | none |

Keycloak listens on port 8080 with **https** when this machine trusts the ASP.NET Core dev certificate (`dotnet dev-certs https --trust`, the default on a Visual Studio machine): Aspire terminates HTTPS for the container. Without a trusted dev certificate the same URLs use `http://`. The issuer in tokens follows the scheme (`https://localhost:8080/realms/decisya` here).

**Realm changes and resets.** Keycloak imports the realm only into an empty database. An existing realm is skipped at every restart. So:

- An edited `decisya-realm.json` reaches your local instance only after a volume reset (below). CI always tests the committed file on an empty container, so CI never misses a change.
- Resetting the AppHost's local secrets (for example a new dev password) also needs a volume reset: the database keeps the old role password, client secret and user passwords, and Keycloak then fails to authenticate.

**Postgres, Keycloak and Redis keep running after the AppHost stops.** They are Aspire *persistent* containers named `decisya-postgres`, `decisya-keycloak` and `decisya-redis`. Redis holds only the BFF's session tickets and has no volume, so stopping it signs everyone out and nothing else is lost. The next AppHost start reuses them, which is faster (no Keycloak first start) and guarantees only one Postgres ever uses `decisya-postgres-data`. Two Postgres servers on one volume corrupt it (`PANIC: could not locate a valid checkpoint record`). The AppHost tests use their own throwaway volume and never touch these containers.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Stop the containers when you're done for the day | Docker Desktop → *Containers* → stop `decisya-redis`, `decisya-keycloak`, then `decisya-postgres` | `docker stop decisya-redis decisya-keycloak decisya-postgres` |
| Reset the volume (after a realm edit or a secret reset) | Stop the AppHost → Docker Desktop → *Containers*: delete `decisya-keycloak` and `decisya-postgres` (and any old `keycloak-…`/`postgres-…`) → *Volumes*: delete `decisya-postgres-data` → start the AppHost | `docker rm -f decisya-keycloak decisya-postgres`, then `docker rm $(docker ps -aq --filter volume=decisya-postgres-data)` if anything is left, then `docker volume rm decisya-postgres-data`, then start the AppHost |

**Admin console (dev, loopback only).** `https://localhost:8080/admin/`, user `admin`. The dashboard has no `keycloak-password` parameter: the AppHost calls `AddKeycloak` without an admin-password parameter, so Aspire generates and keeps one. Find it here:

| | VS 2026 | CLI |
| --- | --- | --- |
| Dashboard | *Resources* → `keycloak` → *View details* → *Environment variables* → `KC_BOOTSTRAP_ADMIN_PASSWORD` (eye icon). The user is `KC_BOOTSTRAP_ADMIN_USERNAME` (normally `admin`) | same, in the dashboard |
| User secrets | *Solution Explorer* → right-click `Decisya.AppHost` → *Manage User Secrets* → `Parameters:keycloak-password` | `dotnet user-secrets list --project src/Decisya.AppHost`, then read the `Parameters:keycloak-password` line |

Type the password; don't copy it into notes, chats or screenshots. The console is for local development on this machine only; the ports listen on loopback. Never copy the admin password, the client secret, a dashboard token or the dev password into issues, chats, commits or screenshots.

<a id="issue-25-admin-api"></a>

### Issue #25: Admin API smoke check (verified)

Verified by Marco on 2026-10-02 against `d41a263`.

In short: the route `POST /api/admin/tenants/{tenantId}/trial` (and the PUT and DELETE override routes) returns 204 for `dev-admin`, 403 for `dev-alice`.

Prerequisites: Docker Desktop is running and you are on `main`.

Tip: use a normal Firefox window for `dev-alice` and a Private Window (Ctrl+Shift+P) for `dev-admin`. Each has its own cookies, so there is no logout dance. The shell (#26) has **Sign in** and **Sign out** if you prefer one window.

**(a) Start the stack.** F5 on `Decisya.AppHost` (VS 2026) or `dotnet run --project src/Decisya.AppHost` (CLI). Wait until `postgres`, `keycloak`, `redis`, `decisya-bff` and `decisya-api` are Healthy or Running and `decisya-migrator` shows Finished.

**(b) Add the mapper by hand, or reset the volume.**

Issue #25 adds the client mapper `realm-roles-access-token` to `deploy/keycloak/decisya-realm.json`. It puts a flat `roles` claim into the access token of the `decisya-bff` client. The API reads only that claim to find `platform-admin`. An existing local Keycloak volume does not import it (see "Realm changes and resets"). Without it, every `/api/admin` call returns 403, also for `dev-admin`. Pick one option.

**Option 1: reset the dev volume.** This deletes all local data: Keycloak (realm, users, sessions), tenancy, entitlements and audit. Only synthetic dev data is lost. The steps are the "Reset the volume" row above.

| Step | VS 2026 | CLI |
| --- | --- | --- |
| Reset | Same as the "Reset the volume" row above | Same as the "Reset the volume" row above |
| Start | F5 on `Decisya.AppHost` | `dotnet run --project src/Decisya.AppHost` |

**Option 2: add the mapper by hand.** Local data is kept. Use the admin console (loopback only, section 3) or `kcadm.sh`. Never type the admin password or a token on a command line.

| Step | VS 2026 (admin console in Firefox) | CLI (`kcadm.sh` in the container) |
| --- | --- | --- |
| Open | Find the admin password (see "Admin console" above; type it, do not copy it into notes, chats or screenshots). Open `https://localhost:8080/admin/`, sign in as `admin`. Switch the realm selector to `decisya` | `docker exec -it decisya-keycloak bash` (a shell inside the container; all commands below run there) |
| Sign in | — | `/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user admin`. There is no `--password`: kcadm prompts for it, and the prompt does not echo. If the container serves only https, use `https://localhost:8080` |
| Find the client | *Clients* → `decisya-bff` → *Client scopes* → `decisya-bff-dedicated` | `/opt/keycloak/bin/kcadm.sh get clients -r decisya -q clientId=decisya-bff --fields id`. Note the `id` value (call it `<client-id>`) |
| Add the mapper | *Add mapper* → *By configuration* → *User Realm Role*. Name: `realm-roles-access-token`. Token Claim Name: `roles`. Claim JSON Type: `String`. Multivalued: **On**. Add to ID token: **Off**. Add to access token: **On**. Add to lightweight access token: Off. Add to userinfo: **Off**. Add to token introspection: **On**. Save | `/opt/keycloak/bin/kcadm.sh create clients/<client-id>/protocol-mappers/models -r decisya -s name=realm-roles-access-token -s protocol=openid-connect -s protocolMapper=oidc-usermodel-realm-role-mapper -s consentRequired=false -s 'config."claim.name"=roles' -s 'config."multivalued"=true' -s 'config."jsonType.label"=String' -s 'config."id.token.claim"=false' -s 'config."access.token.claim"=true' -s 'config."userinfo.token.claim"=false' -s 'config."introspection.token.claim"=true'`, then `exit` |
| Leave `realm-roles-id-token` alone | It stays as it is: ID token only | same |

The flags match the mapper in the realm file. Do not change `fullScopeAllowed` or the scope mappings: they limit `roles` to `tenant-user` and `platform-admin`.

Sign in again after the change. A session that started before it holds an access token without `roles`, so use a fresh Private Window, or click **Sign out** in the shell (or clear the site cookies) and sign in again. A signed-in shell is at `https://localhost:7200/`.

**Verify the mapper.** The browser never receives a token (the BFF keeps them, section 3), so you cannot decode an access token in Firefox dev tools. Use one of these:

| Check | VS 2026 (admin console) | CLI |
| --- | --- | --- |
| Mappers tab | *Clients* → `decisya-bff` → *Client scopes* → `decisya-bff-dedicated`: `realm-roles-access-token` is listed next to `realm-roles-id-token`; open it and check *Add to access token* is On and *Add to ID token* is Off | `/opt/keycloak/bin/kcadm.sh get clients/<client-id>/protocol-mappers/models -r decisya --fields name` inside the container lists both names |
| Decoded access token | *Clients* → `decisya-bff` → *Client scopes* → *Evaluate*: user `dev-admin` → *Generated access token*. It has `"roles": ["platform-admin", …]` | none (use the smoke test below) |
| End to end | The smoke test below returns 204 for `dev-admin` | same |

**(c) Turn off Firefox's JSON viewer for the check.** Open `about:config` and set `devtools.jsonview.enabled` to `false`. Set it back afterwards if you like. Why: on JSON pages the viewer hides `document.cookie` from the console and applies its own `Content-Security-Policy: default-src 'none'`, which blocks `fetch`. The BFF sets no such policy.

**(d) `dev-alice`, normal window.**

1. Open `https://localhost:7200/bff/login` and sign in as `dev-alice`.
2. Open `https://localhost:7200/api/tenancy/me`. The first call creates her tenant. Copy `tenant.id`. Without it, admin calls give 404 `tenant_not_found`.
3. Open `https://localhost:7200/bff/me` (now raw text). It sets the readable cookie `__Host-decisya-xsrf`.
4. Press F12 and open *Console* (type `allow pasting` if asked). Check that `document.cookie.includes('__Host-decisya-xsrf')` prints `true`.

**(e) The console helper.** Paste it once per tab. It uses `var`, so pasting it again is safe; a top-level `const` fails with "redeclaration of const". Replace `<alice-tenant-id>`.

```js
var decisyaTenant = '<alice-tenant-id>';
var decisyaAdmin = async function (method, path, body) {
  var xsrf = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('__Host-decisya-xsrf=')).split('=')[1]);
  var r = await fetch(path, { method: method, credentials: 'same-origin',
    headers: Object.assign({ 'X-XSRF-TOKEN': xsrf }, body ? { 'Content-Type': 'application/json' } : {}),
    body: body ? JSON.stringify(body) : undefined });
  console.log(method, path, '→', r.status, await r.text());
};
```

The console prints `undefined` after the definitions. That is normal and sends nothing. The calls below send the requests.

**(f) As `dev-alice`, expect 403.** The body contains only `type`, `title`, `status` and `traceId`.

```js
await decisyaAdmin('POST', `/api/admin/tenants/${decisyaTenant}/trial`);
await decisyaAdmin('PUT', `/api/admin/tenants/${decisyaTenant}/overrides/forecasting.scenarios`, { reason: 'smoke' });
await decisyaAdmin('DELETE', `/api/admin/tenants/${decisyaTenant}/overrides/forecasting.scenarios`);
```

**(g) `dev-admin`, Private Window.**

1. Sign in at `https://localhost:7200/bff/login` as `dev-admin`. If you get "Invalid username or password" with the right password, brute-force protection may have locked the account (5 failures). In the admin console go to *Users* → `dev-admin` and clear the lock, or wait a few minutes. Also check *Enabled* and the role mapping `platform-admin`.
2. Open `https://localhost:7200/bff/me`. It should show `"roles":["platform-admin"]`. This also sets the `__Host-decisya-xsrf` cookie for this window.
3. Open the console (F12, `allow pasting` if asked), paste the helper from (e) with alice's id, and run these lines in order:

```js
await decisyaAdmin('POST', `/api/admin/tenants/${decisyaTenant}/trial`);
await decisyaAdmin('POST', `/api/admin/tenants/${decisyaTenant}/trial`);
await decisyaAdmin('PUT', `/api/admin/tenants/${decisyaTenant}/overrides/forecasting.scenarios`, { reason: 'smoke' });
await decisyaAdmin('DELETE', `/api/admin/tenants/${decisyaTenant}/overrides/forecasting.scenarios`);
await decisyaAdmin('POST', '/api/admin/tenants/11111111-1111-1111-1111-111111111111/trial');
await decisyaAdmin('POST', '/api/admin/tenants/not-a-guid/trial');
await decisyaAdmin('PUT', `/api/admin/tenants/${decisyaTenant}/overrides/forecasting.scenarios`, { reason: 42 });
```

| Call | Expected |
| --- | --- |
| POST …/{alice}/trial | 204 |
| POST …/{alice}/trial again | 409 `entitlements.trial_already_used` |
| PUT …/{alice}/overrides/forecasting.scenarios `{reason:'smoke'}` | 204 |
| DELETE …/{alice}/overrides/forecasting.scenarios | 204 |
| POST …/11111111-1111-1111-1111-111111111111/trial | 404 `entitlements.tenant_not_found` |
| POST …/not-a-guid/trial | 400 `entitlements.tenant_invalid` |
| PUT …/{alice}/overrides/forecasting.scenarios `{reason:42}` | 400, no `code` |

**(h) Optional audit check.**

```text
docker exec -it decisya-postgres psql -U postgres -d decisya -c "select action, feature_key, outcome, occurred_at from audit.audit_records order by occurred_at;"
```

The table shows one row per success (trial start, override grant, override revoke) and none for refusals or failures. If psql asks for a password, skip it; the tests cover this.

**(i) Clean up.** Close the Private Window. Don't share cookie values, the admin password or tokens in screenshots (hide the *Storage* Value column). Set `devtools.jsonview.enabled` back to `true` if you want the viewer again.

Notes:

- Without the `X-XSRF-TOKEN` header the BFF refuses the call before it reaches the API. Without a session it returns 401.
- The `curl.exe` alternative was dropped: the session cookies are `HttpOnly`, so you cannot copy them without exposing them.

<a id="issue-26-spa-shell"></a>

## Issue #26: SPA shell (build, run, E2E)

The React shell lives in `src/Decisya.Web`. The build writes it to `src/Decisya.Bff/wwwroot` (git-ignored), and the BFF serves it at `https://localhost:7200/`. The shell has **Sign in** and **Sign out**, and a menu built from `/api/capabilities`.

Verified by Marco on 2026-10-03.

**Prerequisites.**

| Tool | VS 2026 | CLI (VS Code PowerShell terminal) |
| --- | --- | --- |
| Node 26 | see the Prerequisites table above | `node --version` prints v26.x |
| npm 11 | comes with Node 26 | `npm --version` prints 11.x |
| Install scripts off | — | `npm config get ignore-scripts` prints `true` (user level) |

**First-time install (Marco only; agents cannot install packages, #58).** Every npm command in this section runs in `src\Decisya.Web`, not in the repo root. Run `cd src\Decisya.Web` first, then check with `Test-Path package.json`: it must print `True`. Run in the repo root, the commands fail (see Troubleshooting).

| Step | VS 2026 | CLI (in `src\Decisya.Web`) |
| --- | --- | --- |
| Go to the folder | Open a terminal in VS Code (or *View → Terminal* in VS 2026) | `cd src\Decisya.Web`, then `Test-Path package.json` → `True` |
| Install | none (use the CLI cell) | `npm ci`. The committed `.npmrc` and lockfile are enough; no `--before` is needed |
| Audit | none | `npm audit`, then `npm audit signatures` |
| Browsers for E2E | none | `npx --no playwright install firefox chromium` |

Never run `npm audit fix`, and never use `--force` or `--legacy-peer-deps`. If an audit reports a problem, stop and report it. In VS Code, decline any extension prompt to install dependencies automatically.

**Build and run.**

| Step | VS 2026 | CLI (in `src\Decisya.Web`) |
| --- | --- | --- |
| Build the shell | none (use the CLI cell) | `npm run build`. Output goes to `src/Decisya.Bff/wwwroot` |
| Restart the AppHost after the first build | Stop, then F5 on `Decisya.AppHost` | Stop it, then `dotnet run --project src/Decisya.AppHost` (from the repo root) |
| Open the shell | Firefox: `https://localhost:7200/` | same |

**Checks** (in `src\Decisya.Web`):

| Check | CLI |
| --- | --- |
| Types | `npm run typecheck` |
| Lint | `npm run lint` |
| Unit tests | `npm test` |

**E2E.** Run it in a second terminal, with the stack running (all resources Healthy or Running, `decisya-migrator` Finished). The tests sign in as `dev-admin`, so they need the dev password. Type it at the prompt; it is not echoed.

PowerShell 7 (the VS Code terminal):

```powershell
Set-Location <repo>\src\Decisya.Web
if (-not (Test-Path .\package.json)) { throw "Not in src\Decisya.Web" }
$env:E2E_DEV_PASSWORD = Read-Host -MaskInput 'dev password'
npm run test:e2e
Remove-Item Env:E2E_DEV_PASSWORD
```

Windows PowerShell 5.1 has no `-MaskInput`. Use a secure string and convert it:

```powershell
Set-Location <repo>\src\Decisya.Web
if (-not (Test-Path .\package.json)) { throw "Not in src\Decisya.Web" }
$secure = Read-Host -AsSecureString 'dev password'
$env:E2E_DEV_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
npm run test:e2e
Remove-Item Env:E2E_DEV_PASSWORD
```

Never store the dev password in VS Code `settings.json`, `launch.json`, `terminal.integrated.env.*`, with `setx`, or in a `.env` file.

Expected result: 40 tests pass (20 in Firefox, 20 in Chromium).

**Troubleshooting.**

| Symptom | Cause and fix |
| --- | --- |
| `Missing script` or `ENOENT ... package.json` | You are in the repo root. `cd src\Decisya.Web` and check `Test-Path package.json` |
| E2E fails with dev-admin "Invalid username or password" | The password differs from `dev-user-password`, or the account is locked. The realm's password history (the last 3 passwords) blocks setting it back by hand, so reset the dev volume (the "Reset the volume" row in section 3) |
| The first sign-in after a cold start takes several seconds | Expected. Wait; do not retry in a loop |
| A tenant on a trial shows **Scenarios** in the menu | Expected. E2E checks the menu against the live `/api/capabilities`, so a trial tenant shows Scenarios |
| `https://localhost:7200/` returns 404 | The shell is not built. Run `npm run build`, then restart the AppHost |

### Lockfile check (any npm change, including Dependabot PRs)

Why: a Dependabot npm PR gets no human review of install scripts otherwise (G3 M2, G6-26-02).

| Step | VS 2026 | CLI (PowerShell, in `src\Decisya.Web`) |
| --- | --- | --- |
| Check out the PR | *Git Changes* → *Manage Branches* → check out the PR branch | `git checkout <pr-branch>`, then `cd src\Decisya.Web` and `Test-Path package.json` → `True` |
| Scan the lockfile | none (use the CLI cell) | Run the script below. It only reads `package-lock.json` |
| Audit | none | `npm audit`, then `npm audit signatures`. Never `npm audit fix` |

```powershell
node -e "const l=require('./package-lock.json');for(const[k,v]of Object.entries(l.packages)){if(!k)continue;if(!(v.resolved||'').startsWith('https://registry.npmjs.org/')||!v.integrity)console.log('SOURCE',k);if(v.hasInstallScript)console.log('SCRIPT',k)}"
```

The rule:

- No `SOURCE` line.
- `SCRIPT` at most for `node_modules/fsevents`.
- No High or Critical finding in `npm audit`, and no invalid signature in `npm audit signatures`.

Any new `SOURCE` line, any `SCRIPT` line other than `fsevents`, a High or Critical finding, or an invalid signature means **do not merge**.

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
