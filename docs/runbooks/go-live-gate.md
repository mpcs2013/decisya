# Go-live gate: what blocks the first real account and the first real data

- Owner: devops · Last verified: 2026-10-07 (G4 of #121; Keycloak 26.7.5). **No command below has been run by its author**: the agent that wrote it could run only `python .claude/scripts/lint.py`. Every command is therefore marked **(unverified)** until Marco or G5 has run it once.
- When to use: before the first account that is not synthetic exists, before any real data enters the stack, and before #132 (NAS bring-up) or #29 (deployment) is closed.
- Design: ADR-0016 (Phase 0 holds synthetic users only), `docs/architecture/production-identity.md` (D2, D5), threat model `docs/security/threat-models/production-identity.md` (G4-121-05), the release blockers in `docs/security/threat-models/system-baseline.md` (C-01 to C-20).
- Column 1 is Visual Studio 2026 or VS Code (Marco runs terminal steps in the VS Code PowerShell terminal; the Keycloak admin console is a browser step, Firefox, from the workstation address). Column 2 is the CLI. `$STACK` and `$C` are those of `docs/runbooks/deployable-stack.md` ("Prerequisites" and "Compose shortcut").

## The rule

**No account that is not synthetic exists until MFA for tenant users and the breached-password check are on.**

That sentence is item C-02 below. Phase 0 users are synthetic (ADR-0016): each carries the attribute `synthetic=true`. `stackctl.py verify` counts the users without it and fails while either C-02 switch is off, so a real account is a visible failure, not a silent one. Everything else in the list below blocks the same moment: the first real account, or the first real data, whichever comes first.

## The blocking list

| Item | What it protects | Owner | Status when #121 merges |
| --- | --- | --- | --- |
| C-02 | tenant-user MFA and the breached-password check | #121 designs it; the switch-on is this gate | Open: both switches off by design |
| C-03 | the production realm (no dev data, TLS, console off the app host) | #121 | Closed by #121 on merge; re-check with `verify` |
| C-03b | a non-destructive realm migration | decided at this gate | Open: `realm rebuild` is the Phase 0 stop-gap |
| C-05 | the BFF Data Protection key ring: where it lives, who reads it, protection at rest, backup | #29, backup with #30 | Open |
| C-09 | rate limiting (anti-automation) in the BFF and at the edge | #122 | Open |
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

The key ring is the `bff-keyring` volume, read-write in the BFF only. It decides whether sessions survive a restart and who can forge a cookie.

**Closed when:** the owner, the permissions and the protection at rest of the volume are decided and implemented (#29); the backup and a restore drill that brings the key ring back are recorded (#30); the decision is written into `deployable-stack.md`.

### C-09: rate limiting

**Closed when** (#122): an ASP.NET Core rate limiter partitions the BFF by session, else by client address from the edge; `/bff/login`, `/bff/backchannel-logout`, `/api/*` and `/api/admin` have limits; the edge has its own limit; a 429 ProblemDetails is returned; tests exist for each route class.

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
| `python3 -m unittest discover -s deploy/tests -p "test_go_live_gate.py" -v` | the gate file names C-02, C-03, C-03b, C-05, C-09, C-10 and C-20, #132 and #29, and carries the C-02 sentence |
| The Done-when of #132 and #29 | each points to `docs/runbooks/go-live-gate.md` (a manual check on GitHub) |

## Rollback

| Situation | CLI |
| --- | --- |
| A switch broke sign-in for synthetic users | Set `level-2-tenant` back to *Disabled* in the console, or `kcadm.sh update` with `"requirement":"DISABLED"` (same command as A1). While no real account exists, `realm rebuild --confirm` returns the realm to the committed file. |
| A real account exists and a switch must go off | Do not rebuild: `realm rebuild` refuses. Change the setting in the console and record the exception; that is C-03b's case. |
