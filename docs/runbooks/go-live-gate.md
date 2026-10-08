# Go-live gate: what blocks the first real account and the first real data

- Owner: devops · Last verified: 2026-10-08 (G4 part 4 of #122; the C-02 steps date from G4 of #121, Keycloak 26.7.5). **No command below has been run by its author**: the agent that wrote it could run only `python .claude/scripts/lint.py`. Every command is therefore marked **(unverified)** until Marco or G5 has run it once.
- When to use: before the first account that is not synthetic exists, before any real data enters the stack, and before #132 (NAS bring-up) or #29 (deployment) is closed.
- Design: ADR-0016 (Phase 0 holds synthetic users only), `docs/architecture/production-identity.md` (D2, D5), threat model `docs/security/threat-models/production-identity.md` (G4-121-05), the release blockers in `docs/security/threat-models/system-baseline.md` (C-01 to C-20).
- Column 1 is Visual Studio 2026 or VS Code (Marco runs terminal steps in the VS Code PowerShell terminal; the Keycloak admin console is a browser step, Firefox, from the workstation address). Column 2 is the CLI. `$STACK` and `$C` are those of `docs/runbooks/deployable-stack.md` ("Prerequisites" and "Compose shortcut").

## The rule

**No account that is not synthetic exists until MFA for tenant users and the breached-password check are on.**

That sentence is item C-02 below. Phase 0 users are synthetic (ADR-0016): each carries the attribute `synthetic=true`. `stackctl.py verify` counts the users without it and fails while either C-02 switch is off, so a real account is a visible failure, not a silent one. Everything else in the list below blocks the same moment: the first real account, or the first real data, whichever comes first.

## The blocking list

| Item | What it protects | Owner | Status when #122 merges |
| --- | --- | --- | --- |
| C-02 | tenant-user MFA and the breached-password check | #121 designs it; the switch-on is this gate | Open: both switches off by design |
| C-03 | the production realm (no dev data, TLS, console off the app host) | #121 | Closed by #121 on merge; re-check with `verify` |
| C-03b | a non-destructive realm migration | decided at this gate | Open: `realm rebuild` is the Phase 0 stop-gap |
| C-05 | the BFF Data Protection key ring: where it lives, who reads it, protection at rest, backup | #122 (the ring), #30 (backup and restore drill) | Open until #122 has merged and the #30 restore drill is recorded |
| C-09a | rate limiting in the BFF, per route class | #122 | Closed by #122 on merge |
| C-09b | rate limiting at the Caddy edge, the app host and the id host | decided at this gate (ADR-0016 R4 amendment) | Open: blocks public-internet exposure |
| C-10 | authentication event logging | #121 | Closed by #121 on merge |
| C-20 | production secret store and rotation | #29; file secrets and `secrets rotate` came with #120 | Open: the signing-key and client-secret rotation after go-live |

Related and not repeated here: C-01 (admin MFA) is closed by #121 and kept closed by the master-realm check in `verify`; C-04 (hash key, collector retention) is #29's own list.

**Pointers.** The Done-when of #132 and of #29 each carries a line that points to this file. Those lines are GitHub issue edits that Marco applies (the orchestrator proposes the wording at G7); a repository test cannot see issues, so the test of this file checks only that this file names both issues. #132 may not be closed with an item unchecked if a real account exists.

## How each item is closed

### C-02: MFA for tenant users and the breached-password check

No account that is not synthetic exists until MFA for tenant users and the breached-password check are on. Enforced now: admin MFA and Keycloak's native password checks (length 12 to 128, not the username, not the email, history 3, `common-passwords.txt`). Configured but off: tenant-user MFA. Designed only: the breached-password list and the context-specific checks for a tenant name or a person's name.

**Closed when:**
1. Tenant-user MFA is on: the `level-2-tenant` step is Conditional, and a synthetic tenant user without an OTP is made to enrol at its next sign-in (steps A below).
2. The breached-password check is on: the list is approved by G3, mounted read-only and named in the realm's password policy (steps B below).
3. `stackctl.py verify` prints `tenant_mfa=on breached_list=on` and reports no problem. Only then is the first real account created.

### C-03: the production realm

**Closed when** (by #121 on merge): the realm imports with no users, `sslRequired` is `all`, the client's secret and URIs hold no placeholder or wildcard, the admin console and `/realms/master` answer 403 from every address but the workstation's, and `verify` is OK. At go-live, run the stack smoke (`deploy/tests/stack_smoke.py`, step 9 of `deployable-stack.md`) once more on the VPS and record `/realms/decisya/` 200 and `verify: OK`.

### C-03b: a non-destructive realm migration

Keycloak imports the realm file once, on an empty database. Until the first real user, a change that the admin console or `kcadm.sh` cannot make small is made by `stackctl.py realm rebuild --confirm`, which drops the identity database. That command refuses as soon as one user lacks `synthetic=true` (or when its check cannot run), so it cannot destroy a real account. After that point only a versioned, non-destructive migration is possible.

**Closed when:**
1. The migration tool is decided with G3 and recorded (an ADR if it adds a tool, for example keycloak-config-cli: a new third-party tool and a new secret).
2. It is tried on a copy of the identity database, and a change to a flow, a mapper and the client secret is shown to apply without losing a user.
3. `realm rebuild` is removed from `stackctl.py`, or restricted to a throwaway stack, in the same PR.

### C-05: the Data Protection key ring

The key ring is the `bff-keyring` volume, read-write in the BFF only. It decides whether sessions survive a restart and who can forge a cookie. Since #122 its keys are wrapped with a certificate (RSA 4096, a base64 PKCS#12 and its password, four BFF-only file secrets under `$STACK/secrets/`), the volume belongs to uid 1654 (folder 0700, files 0600), and the BFF refuses to start when a live key does not decrypt or a key file is plaintext.

**Closed when** both of these hold:
1. #122 has merged: the ring is persisted, wrapped at rest, readable by the BFF only, and `stackctl.py verify` reports no plaintext key file and the right owner and modes.
2. The #30 restore drill is recorded: the backup copy of the ring is restored with `keyring prepare`, the BFF starts with the certificate pair and unprotects what it protected before.

**What the backup may and may not hold** (G3 G4-122-04; ADR-0016 R7.1):
- The Hyper Backup source is the `dumps` folder alone. The backup copy of the ring is encrypted key XML only.
- `secrets/` is never inside `dumps`, and neither the certificate pair nor any other secret goes into a backup. Marco keeps a copy of the two current certificate files off the NAS, beside the Hyper Backup password (Q5).
- Snapshots of the stack folder (DSM Snapshot Replication) hold `secrets/` and the ring copy together, so stack-folder snapshots never leave the box. Replicating them off the box needs a new decision first.
- A restore writes into the ring, so it is an integrity boundary: #30 confirms that Hyper Backup's client-side encryption authenticates the set, or adds an integrity check before `keyring prepare` (S-122-10).

### C-09a: rate limiting in the BFF

**Closed when** (by #122 on merge): the BFF's ASP.NET Core rate limiter partitions `/bff/login` (with the sign-out and OIDC callback paths), `/bff/backchannel-logout`, `/api/*` and `/api/admin` by session, else by the client address that the edge supplies; a request past its limit gets a 429 ProblemDetails with a whole-second `Retry-After`; each route class has a test; the stack smoke check sends eleven `GET /bff/login` requests with forged `X-Forwarded-For` values and sees one 429 and one `ratelimit.rejected` event (login, ip).

**What C-09a does not do:** it does not stop password guessing. The password is typed into Keycloak's login form and goes from the browser straight to Keycloak, so it never passes the BFF limiter. Protection against guessing rests on Keycloak's brute-force detection (#121) and, at go-live, on C-09b. Do not cite C-09a as the control for ASVS V6.3.

### C-09b: rate limiting at the Caddy edge

Stock Caddy has no rate limiter, so an edge limit needs a custom Caddy build with the third-party `caddy-ratelimit` module. That amends ADR-0016 R4 and ADR-0018 (stock Caddy), and brings the image-scan and parity rules of ADR-0015 and ADR-0017 to a self-built image.

**Closed when:**
1. The ADR-0016 R4 amendment is accepted by Marco (a custom Caddy build, with the module pinned to a commit and reviewed at G3).
2. The edge applies a coarse per-address ceiling above the BFF limits on the app host **and the id host** (Keycloak's login, token and logout endpoints), and a 429 from it carries a whole-second `Retry-After`.
3. The stack smoke check proves it, and the two Caddyfile guards (`header_up` on forwarding headers, `handle_response` and `Retry-After`) stay green.

C-09b is open and **blocks public-internet exposure**: the first VPS that faces the internet does not go live without it. The BFF limiter stays underneath it as defence in depth.

### C-10: authentication event logging

**Closed when** (by #121 on merge): Keycloak login and admin events are on with 90 days of retention and no `jboss-logging` listener; the BFF logs `auth.signin.succeeded`, `auth.signin.failed` and `auth.signout`; the Api logs `auth.token.rejected` and `auth.admin.mfa_required`; the canary tests prove no token, password or raw identifier reaches a log. `verify` keeps the Keycloak settings pinned. Retention of stdout and of the collector is C-04, not this item.

### C-20: production secret store and rotation

**Closed when:** every secret is a file under `$STACK/secrets/` (done in #120) and each has a rehearsed rotation: the `secrets rotate` table in `deployable-stack.md` has been run once on the VPS for each credential; the realm signing key has been rotated once with the procedure in `deployable-stack.md` (add a key provider with a higher priority, keep the old one passive for the longest token or session lifetime, then disable it); and the BFF client secret has a rotation that does not need `realm rebuild` (this depends on C-03b).

## Switching on C-02

Do these in the Keycloak admin console from the workstation address, or with `kcadm.sh` inside the Keycloak container. `kcadm.sh` signs in with a password grant, which an account that holds an OTP credential cannot complete: if it cannot sign in, use the console column and say so in the change entry.

### A. Tenant-user MFA

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| A1 | **Console (Firefox):** the id host's `/admin/`, realm `decisya`, *Authentication*, flow `decisya-browser`, the sub-flow `decisya-forms`: set the step `level-2-tenant` from *Disabled* to *Conditional*. | `$C exec -it keycloak /opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kcadm.config --server http://localhost:8080 --realm master --user <your admin>` (it asks for the password) **(unverified)**, then `$C exec keycloak /opt/keycloak/bin/kcadm.sh get authentication/flows/decisya-forms/executions --config /tmp/kcadm.config -r decisya --fields id,displayName,requirement` to find the id of `level-2-tenant`, then `$C exec keycloak /opt/keycloak/bin/kcadm.sh update authentication/flows/decisya-forms/executions --config /tmp/kcadm.config -r decisya -n -b '{"id":"<id>","requirement":"CONDITIONAL"}'` **(unverified)** |
| A2 | **Prove it (Firefox, private window):** sign in as a synthetic tenant user that has no OTP: Keycloak must ask to configure one before the app opens. Sign in as a user that has one: Keycloak asks for the code. | — (browser only) |
| A3 | Run `verify` and read the note line. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)**: expect `tenant_mfa=on` |

### B. Breached-password check

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| B1 | **Decide and approve the list (G3).** The file is named `breached-passwords.txt`: that is the name `identity-check.sql` looks for in the realm's password policy. The same table as for `common-passwords.txt`: source with pinned release, licence with attribution, build steps, line and byte count, SHA-256, in a `breached-passwords.SOURCE.md` record. Keycloak's `passwordBlacklist` reads plaintext, so a list of hashes (HIBP) needs a hash-checking extension and is a different decision. No breach corpus without a licence. | — (review) |
| B2 | **Measure the memory.** Keycloak loads the list into a Bloom filter in memory; the limit is 1024 MB. Note the figure before and after. | `docker stats --no-stream` before and after the restart in B4 **(unverified)** |
| B3 | **Open a PR** that adds the list as a third read-only file for Keycloak: the mount table in `stackguards.py` (`BIND_MOUNTS`), the overlay, the assemble map in `stackctl.py`, the hash test in `deploy/tests`, and `.gitattributes`. Keycloak holds one `passwordBlacklist` entry per policy, so confirm whether the policy names this file instead of `common-passwords.txt`; if so the list is built as a superset of it, and item 12 of `identity-check.sql`, the realm file and its tests change in the same PR. | `python -m unittest discover -s deploy/tests -p "test_*.py" -v` **(unverified)** |
| B4 | **Assemble, restart, set the policy.** Console: *Authentication*, *Policies*, *Password policy*: name the list. | `python3 deploy/compose/stackctl.py assemble …` and `check` as in `deployable-stack.md`, `$C restart keycloak`; then `$C exec keycloak /opt/keycloak/bin/kcadm.sh update realms/decisya --config /tmp/kcadm.config -s 'passwordPolicy=<the whole policy string, with the list named>'` **(unverified)** |
| B5 | **Prove it:** set a password that is on the list for a synthetic user: Keycloak refuses it. | — (browser or console) |
| B6 | Run `verify` and read the note line. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)**: expect `breached_list=on` |

## Verify

| Check | Expect |
| --- | --- |
| `python3 deploy/compose/stackctl.py verify --stack "$STACK"` | `verify: OK`, and a `note: identity:` line with `tenant_mfa=on breached_list=on` before any real account |
| `python3 -m unittest discover -s deploy/tests -p "test_go_live_gate.py" -v` | the gate file names C-02, C-03, C-03b, C-05, C-09a, C-09b, C-10 and C-20, #132 and #29, carries the C-02 sentence, and says that C-09b blocks public exposure and covers the id host, that C-09a does not stop password guessing, and what the C-05 backup may hold |
| The Done-when of #132 and #29 | each points to `docs/runbooks/go-live-gate.md` (a manual check on GitHub) |

## Rollback

| Situation | CLI |
| --- | --- |
| A switch broke sign-in for synthetic users | Set `level-2-tenant` back to *Disabled* in the console, or `kcadm.sh update` with `"requirement":"DISABLED"` (same command as A1). While no real account exists, `realm rebuild --confirm` returns the realm to the committed file. |
| A real account exists and a switch must go off | Do not rebuild: `realm rebuild` refuses. Change the setting in the console and record the exception; that is C-03b's case. |
