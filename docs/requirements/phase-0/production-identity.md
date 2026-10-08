# Phase 0: Production identity (#121, 0.17c)

Issue #121: production realm, admin MFA, authentication event logging. Manifest: `docs/ai/pipeline/121.md`. Tier: full (auth). Depends on #120 (merged): the stack's Keycloak runs `kc.sh start` with no realm import; `entrypoint-stack.sh` refuses `--import-realm`; the stack guards refuse a realm mount.

Sources read: `docs/ai/pipeline/121.md`, `docs/architecture/deployable-stack.md` (D10, guard 6), `docs/security/threat-models/{deployable-stack,keycloak-realm,admin-api,realm-guard-scope,system-baseline}.md`, `deploy/keycloak/{entrypoint-stack.sh,decisya-realm.json}`, ADR-0016, `docs/requirements/nfr.md` (NFR-15, 20, 21, 26, 38 and 39 stay in force).

## Scope note

Role framing. The actors are the **platform operator** (Marco, holder of `platform-admin`, a role in the platform and in no tenant), a **tenant user** (`tenant-user`, one tenant), and the **security reviewer / auditor** (reads events; not a Decisya role, a person with workstation access to the Keycloak admin console).

In scope:
- **C-03**, the production realm: a separate realm file, no users, `sslRequired: all`, hostname pinned, values as required placeholders, one file mounted, a migration strategy, a no-dev-data check.
- **#25 C-5**, admin MFA: `/api/admin/**` requires an MFA proof in the validated access token, not only the role.
- **C-02**, MFA for every user and the password checks: decided in D1 below.
- **C-10**, authentication event logging: Keycloak login and admin events with retention, three fixed-text BFF events, one Api Warning event.

Out of scope: rate limiting (#122, C-09), the NAS bring-up (#132), log shipping and log retention for stdout and the collector (C-04, #29; Marco's decision there), a user-facing admin UI, self-service registration, email (SMTP) flows, social login.

**Entitlement plan.** None of these stories is gated by a subscription plan. They are platform controls that a plan cannot switch on or off (as in `docs/requirements/phase-0/admin-api.md`). Each row below names the feature key as "none (platform baseline)" and, where an `/api/admin` endpoint is involved, ADR-0008's explicit no-entitlement marker. No `entitlements.*` key is added. A tenant-facing MFA enrolment page, if ever built, would be baseline too, never plan-gated: security controls are not upsold.

## Decisions the requirements make (confirmed by Marco, 2026-10-07)

**D1. C-02 enforcement point: split.**

| Control | Phase 0 (this issue) | At the first non-synthetic user |
| --- | --- | --- |
| MFA for every `platform-admin` | **Enforced now** (also C-5) | stays on |
| MFA for every `tenant-user` | **Designed and configured now, switched off** (the realm file carries the flow and the required action; the switch is one documented setting) | **switched on** |
| Password policy checks Keycloak does natively: length, `notUsername`, `notEmail`, a common-password blacklist file | **Enforced now** (costs nothing; synthetic users and tests use compliant passwords) | stays on |
| Breached-password check (HIBP-style) | **Designed only.** Not configured, not integrated | **switched on**, using the option chosen in G2 (see below) |
| Context-specific words (the product name, the tenant name, the user's name) | Designed; native options used where they exist (G2 lists them) | switched on |

Why: ADR-0016 says Phase 0 has synthetic users only, no account holds real data, and the box has no egress. Enforcing MFA for synthetic tenant users would make every Playwright login and the F2 measurement (login p95 under 3 s) need a TOTP step for no risk reduction. Admin MFA is enforced now because C-5 is a release blocker and admin writes are cross-tenant.

**Breached-password check is a new external integration.** Keycloak has no built-in breached-password policy. The two real options are (a) an extension (SPI) calling the HIBP range API (`api.pwnedpasswords.com`, k-anonymity: the first 5 hex characters of a SHA-1 hash leave the box, never the password): a new third-party package in the Keycloak image and a new egress path from a network that today has none; (b) an offline hash list loaded as the policy's blacklist file: no runtime egress, a periodic data refresh instead. This issue neither adds the extension nor opens egress. **G2 and G3 must treat it as a new external integration or a new data asset** when it is switched on. Default: option (b) first, because it needs no egress.

**The trigger and how it is checked** (so it cannot be forgotten):
1. A go-live gate file, `docs/runbooks/go-live-gate.md` (created by this issue, or its path chosen by G2), lists C-02 as a **blocking** item: "No account that is not synthetic exists until MFA for tenant users and the breached-password check are on."
2. Issue #132 (NAS bring-up) and #29 (deployment) each have a Done-when line that points to that file (added in the PR, so a reviewer sees it). #132 may not be closed with the C-02 item unchecked if any real account exists.
3. An automated tripwire: every user in the production realm in Phase 0 carries the attribute `synthetic=true` (set by the documented provisioning step). A script in `deploy/` (run by the stack's verify command and by #132's smoke) lists the realm's users and **fails** if any user lacks the attribute while the C-02 switches are still off, and fails if the switches are off while a user without it exists. Creating a real account is therefore a visible failure, not a silent one.
4. A static test asserts the go-live gate file exists, names C-02, C-03, C-05, C-09, C-10 and C-20 and that the two issue pointers are present.

**D2. Admin MFA claim: `acr`.** The access token must carry `acr` with the value mapped to the second level (default value `"2"`, Keycloak LoA step-up, `acr.loa.map`). `amr` is **not** used: Keycloak does not emit it by default. How the claim reaches the token (browser flow, conditional authenticators, claim mapper, level mapping) is G2's decision. A token without the required `acr` on `/api/admin/**` gets **403**, the generic `ProblemDetails` body that every other admin refusal carries (no `code`, no reason, no claim value), **zero** database commands, zero audit records, and one Warning log event (Story 3). The MFA check reads the validated token only: no extra call to Keycloak.

**D3. Events.**

*Keycloak* (stored in Keycloak's database, `eventsListeners` limited to the store; the `jboss-logging` listener stays off because Keycloak's stdout format is unmasked and would put raw usernames, IPs and Keycloak user ids into the container log):
- Login events: `LOGIN`, `LOGIN_ERROR`, `LOGOUT`, `LOGOUT_ERROR`, `CODE_TO_TOKEN_ERROR`, `REFRESH_TOKEN_ERROR`, `UPDATE_PASSWORD`, `UPDATE_PASSWORD_ERROR`, `UPDATE_TOTP`, `REMOVE_TOTP`, `USER_DISABLED_BY_TEMPORARY_LOCKOUT`, `USER_DISABLED_BY_PERMANENT_LOCKOUT`. No other type.
- Admin events: on, `adminEventsDetailsEnabled: false` (no representation, which would carry credentials and attributes).
- Retention: `eventsExpiration` = 90 days (7 776 000 s), for login and admin events.
- Kept out of events: tokens, passwords, TOTP secrets, client secrets, the authorization code. (Keycloak stores none of these in an event; a test asserts the configuration that keeps it so.)
- Expected in events (accepted residual R-1, G3): Keycloak always stores the username and client IP in `LOGIN` and `LOGIN_ERROR` events. They are kept 90 days and visible only to realm admins. Decisya's own BFF and Api logs still carry no username, email or raw `sub`.

*BFF*: three events with fixed message templates and these names: `auth.signin.succeeded` (Information), `auth.signin.failed` (Warning), `auth.signout` (Information).
- Allowed fields: event name, `trace_id`, `span_id`, `tenant_id` (when known), the hashed `user_id` (HMAC with the per-environment `UserIdHashKey`, as in the observability rules), for sign-in failure a `reason` from the closed list `access_denied`, `state_invalid`, `token_exchange_failed`, `remote_failure`, `ticket_store_unavailable`, `other`, and for sign-out an `initiator` of `user` or `backchannel`.
- Never: access, ID or refresh token, authorization code, `state`, nonce, cookie value, password, raw `sub`, email, username, the identity provider's `error` or `error_description` text (attacker-controlled; log injection), exception messages, the client IP in the message text.

*Api*: one Warning, `auth.token.rejected`, when a presented bearer token fails validation.
- Allowed fields: event name, `trace_id`, `span_id`, `reason` from the closed list `expired`, `bad_signature`, `bad_issuer`, `bad_audience`, `bad_algorithm`, `malformed`, `other`.
- Never: any token part, claim value, the exception message (`IdentityModel` messages can carry claim values), the Authorization header.
- A request with **no** token is not logged (probe and anonymous noise; C-09 is #122).
- `auth.admin.mfa_required` (Warning): token valid, role `platform-admin`, MFA proof missing. Fields: event name, `trace_id`, `span_id`, hashed `user_id`.

*Retention outside Keycloak's store:* stdout and collector retention is C-04 (Marco's decision at #29). This issue does not set it and says so in the documentation.

**D4. What "production realm" means for the dev AppHost.** The dev AppHost, `deploy/keycloak/decisya-realm.json` (seeded `dev-*` users, `sslRequired: external`) and the dev realm tests **stay unchanged**. The production realm is a **second file** under a production-only path (G2 names it), never the dev file. The stack refuses the dev file (existing guard) and mounts exactly **one** production realm file, never the `deploy/keycloak` directory (F-77-2). Parity: a test compares the two files and allows differences only from a named list (users, `sslRequired`, hostname-derived URIs, MFA and password policy, events, secret placeholders).
- Admin MFA in dev: `dev-admin` has no OTP, so the always-on Api check would refuse it. Default: the Api setting `Authentication:RequireAdminMfa` defaults to **true**; only the `Development` environment may set it to false; start-up **fails closed** if it is false in any other environment. The MFA integration test imports the **production** realm in Testcontainers and creates a synthetic admin with TOTP at test time.

## Open questions for Marco

None. All eight were answered by Marco on 2026-10-07 and take the proposed default. Recorded as decided:

| # | Question | Decision (Marco, 2026-10-07) |
| --- | --- | --- |
| Q1 | C-02 split as in D1 | Split as in D1 |
| Q2 | Breached-password option for go-live | Offline hash list, decided and built at the go-live gate, not here |
| Q3 | MFA proof | `acr` with `"2"`; `amr` not used |
| Q4 | Keycloak events retention | 90 days |
| Q5 | Keycloak events location | Keycloak's event store only, no `jboss-logging` listener; reading them is a manual admin-console step on the workstation |
| Q6 | Api MFA switch (D4) | Development-only opt-out, fail-closed start-up elsewhere |
| Q7 | Gate file and user convention | `docs/runbooks/go-live-gate.md`; `synthetic=true` attribute with the check script |
| Q8 | Admin console exposure | Keep #120's workstation-only rule, port 9000 never routed; no new host |

## Stories

Notation: "the production realm" is the file the stack imports; "the stack" is the Compose project from #120. Test lanes: Unit, Integration (Testcontainers Keycloak), and the Python static guards in `deploy/tests`.

### Story 1: The production realm imports with no seeded users (the Done-when)

*As a platform operator, I want the stack's Keycloak to import a production realm that holds no users, so that no development or synthetic account, password or `.test` address ever exists on the deployed identity provider.*

Entitlement: none (platform baseline).

```gherkin
Scenario: The production realm file holds no users
  Given the committed production realm file
  When it is parsed
  Then it has no "users" entry, or an empty one
  And it defines no service-account user
  And no username starts with "dev-"
  And no email ends with ".test"
  And no credential, password hash or TOTP secret appears in it

Scenario: A fresh Keycloak imports the production realm and has zero users
  Given an empty Keycloak database and the production realm file with every placeholder set
  When Keycloak starts with the realm mounted as one file
  Then the realm "decisya" exists
  And the realm has the roles "tenant-user" and "platform-admin"
  And the realm has 0 users
  And the "decisya-bff" client exists with its tenant_id and roles mappers as in the dev realm

Scenario: The realm requires TLS
  Given the imported production realm
  When the realm settings are read
  Then sslRequired is "all"
  And registrationAllowed, resetPasswordAllowed and rememberMe are false
  And accessTokenLifespan is at most 300

Scenario: A development-only value in the production file is refused
  Given a production realm file that contains the user "dev-alice" or the address "alice@decisya.test"
  When the no-dev-data check runs
  Then it fails and names the offending key, never the value

Scenario: The production realm differs from the dev realm only where allowed
  Given the dev realm file and the production realm file
  When the parity test compares them
  Then every difference is on the allow-list (users, sslRequired, hostname-derived URIs, MFA and password policy, events, secret placeholders)
  And an unlisted difference fails the test
```

### Story 2: Hostname, placeholders, one mounted file and the admin console (C-03)

*As a platform operator, I want the realm values that depend on the environment to be required placeholders, the hostname pinned, exactly one realm file mounted and the admin console unreachable from the app host, so that a missing setting stops the start instead of producing a realm that points at the wrong place.*

Entitlement: none (platform baseline).

```gherkin
Scenario: A required realm variable is unset
  Given the production realm file with one required variable unset, such as the BFF client secret or the app origin
  When the stack's Keycloak starts
  Then it fails to start with a message that names the variable and never a value
  And no realm is created with a literal "${...}" in a redirect URI, secret or origin

Scenario: Redirect URIs, web origins and logout URLs come from the variables
  Given the stack with the app origin "https://app.p0.home.arpa:8443"
  When the realm is imported
  Then the "decisya-bff" client's redirect URIs, post-logout URIs and web origins contain only that origin
  And none contains "*" outside a path suffix, "localhost" or an IP address

Scenario: The hostname is pinned
  Given the stack with KC_HOSTNAME "https://id.p0.home.arpa:8443"
  When the discovery document is requested with any Host header
  Then the issuer is "https://id.p0.home.arpa:8443/realms/decisya"

Scenario: Exactly one realm file is mounted
  Given the stack's Compose definition
  When the static guards run
  Then the realm is mounted as one named file
  And a mount of the "deploy/keycloak" directory, the dev realm file or "/opt/keycloak/data/import" as a directory fails the guard
  And the guard that refused any realm mount in #120 is replaced by this rule, not removed

Scenario: The entrypoint still refuses anything but the production start
  Given entrypoint-stack.sh
  When it is given "start-dev", "build", "export" or "import" as an argument
  Then it exits with an error that names the argument
  And "--import-realm" is allowed only as the stack's fixed, wrapper-added argument, never as a caller argument

Scenario: The admin console stays off the app host
  Given the running stack
  When a client other than the workstation address requests "/admin/" or "/realms/master/" on the identity host
  Then the edge answers 403
  And port 9000 is never routed
  And no published port other than the edge's 8443 exists

Scenario: A restart does not overwrite or break the realm
  Given a realm imported earlier and edited after import
  When Keycloak restarts with the same realm file
  Then the existing realm is kept unchanged
  And the start succeeds
```

### Story 3: `/api/admin` refuses a token without MFA (#25 C-5)

*As a platform operator, I want the API to refuse an admin token that does not prove a second factor, so that a phished or reused admin password cannot change entitlements for any tenant, even if the realm's MFA flow is misconfigured.*

Entitlement: none (platform baseline). Every `/api/admin/**` endpoint carries ADR-0008's no-entitlement marker.

```gherkin
Scenario: A platform-admin token with no MFA claim is refused
  Given a valid access token with the role "platform-admin", no tenant_id, and no "acr" claim
  When it calls any /api/admin/** endpoint through the Api
  Then the response is 403
  And the body is the generic ProblemDetails with status, title and traceId only
  And the body has no "code", no mention of MFA and no claim value
  And zero database commands are issued and zero audit records are written
  And one Warning event "auth.admin.mfa_required" is logged with the hashed user_id and trace_id

Scenario: A token with the wrong authentication level is refused the same way
  Given a valid platform-admin access token whose "acr" is "1"
  When it calls /api/admin/**
  Then the response is 403, byte-identical in shape to the previous scenario
  And the same Warning event is logged

Scenario: A token with the MFA level is accepted
  Given a valid platform-admin access token whose "acr" is "2"
  When it calls an /api/admin/** endpoint with a valid request
  Then the role check and the existing admin behaviour apply unchanged
  And a 2xx response is matched by exactly one audit record (NFR-39)

Scenario: The MFA proof is not enough without the role
  Given a valid tenant-user access token whose "acr" is "2"
  When it calls /api/admin/**
  Then the response is 403 as before, and no MFA event is logged

Scenario: A forged or duplicated claim does not count
  Given a token whose "acr" is duplicated, of the wrong type, or present only in an unsigned or unvalidated token
  When it calls /api/admin/**
  Then the response is 401 or 403 with the generic body, never 2xx

Scenario: The check needs no call to Keycloak
  Given a platform-admin token with the MFA level
  When it calls /api/admin/**
  Then the Api makes zero network calls other than its cached key-set fetch

Scenario: Disabling the check is possible only in Development
  Given the setting Authentication:RequireAdminMfa is false
  When the Api starts in an environment other than Development
  Then start-up fails closed with a message that names the setting
  And when the setting is absent the check is on

Scenario: End to end against the production realm
  Given the production realm imported in Testcontainers Keycloak and a synthetic platform-admin created at test time with a TOTP credential
  When the admin signs in with password only and calls /api/admin/**
  Then the response is 403
  When the admin signs in with password and TOTP and calls /api/admin/**
  Then the token carries the MFA "acr" and the call is not refused for MFA
```

### Story 4: Every platform-admin is made to enrol a second factor (#25 C-5, realm side)

*As a platform operator, I want the production realm to require an OTP (or WebAuthn) factor for every holder of `platform-admin`, so that the first admin sign-in cannot complete without a second factor.*

Entitlement: none (platform baseline).

```gherkin
Scenario: A new platform-admin must enrol before getting a session
  Given the production realm and a user with the role "platform-admin" and no OTP credential
  When the user signs in with the correct password
  Then Keycloak requires configuring a TOTP before issuing any token
  And no authorization code is returned until that is done

Scenario: An admin with an OTP is asked for it on every sign-in
  Given a platform-admin with a configured TOTP
  When the user signs in with the correct password
  Then Keycloak asks for the OTP
  And a wrong OTP counts toward brute-force protection

Scenario: The MFA flow is part of the committed realm file
  Given the production realm file
  When its browser authentication flow is read
  Then a conditional second-factor step applies to holders of "platform-admin"
  And the realm maps that step to the "acr" level used by the Api (default "2")

Scenario: A tenant-user keeps the password-only flow in Phase 0
  Given the production realm with the C-02 tenant-user switch off
  When a tenant-user signs in with the correct password
  Then no second factor is asked
  And the access token has no MFA "acr"
```

### Story 5: MFA for every user and the password checks are designed, configured and gated (C-02)

*As a platform operator, I want the tenant-user MFA and the breached-password check designed and ready now and switched on before the first non-synthetic user, with a check that cannot be forgotten, so that no real person ever holds an account protected by only a password.*

Entitlement: none (platform baseline).

```gherkin
Scenario: The native password policy is enforced now
  Given the production realm
  When the realm's password policy is read
  Then it contains length(12), maxLength(128), notUsername, notEmail and a common-password blacklist
  And passwordHistory is at least 3
  And a password in the blacklist, or equal to the username, is refused when an admin sets it

Scenario: The tenant-user MFA switch is documented and off
  Given the production realm in Phase 0
  Then the C-02 tenant-user MFA switch is off
  And one documented step turns it on without a code change
  And with the switch on, a tenant-user without an OTP must enrol on the next sign-in

Scenario: The breached-password check is designed, not integrated
  Given the architecture note for this issue
  Then it records the chosen option for the check, whether it needs egress and a new package
  And it states that G3 reviews that integration before the switch is turned on
  And no Keycloak extension and no new egress is added by this issue

Scenario: The go-live gate carries C-02 as blocking
  Given docs/runbooks/go-live-gate.md
  Then it lists C-02, C-03, C-05, C-09, C-10 and C-20
  And C-02 reads "no account that is not synthetic exists until tenant-user MFA and the breached-password check are on"
  And the Done-when of #132 and #29 each points to this file

Scenario: A real account in Phase 0 is a visible failure
  Given every user in the realm carries the attribute synthetic=true and the C-02 switches are off
  When the user check runs
  Then it passes
  When a user without that attribute exists and the switches are still off
  Then the check fails and names the count, never a username or email
```

### Story 6: Authentication events are emitted and tested (C-10)

*As a security reviewer, I want sign-in success, sign-in failure, sign-out, rejected bearer tokens and Keycloak login and admin events recorded without tokens, passwords or raw identifiers, so that I can reconstruct an incident without the log becoming a second leak.*

Entitlement: none (platform baseline).

```gherkin
Scenario: Keycloak records login and admin events with a 90 day retention
  Given the production realm
  When the realm's event settings are read
  Then login events are enabled for exactly the types listed in D3 and no others
  And admin events are enabled with adminEventsDetailsEnabled false
  And eventsExpiration is 7776000
  And eventsListeners contains the store and not "jboss-logging"

Scenario: A failed and a successful Keycloak login are recorded
  Given the production realm with a synthetic user
  When a sign-in fails with a wrong password and then succeeds
  Then the event store holds one LOGIN_ERROR and one LOGIN
  And no event holds a password, a token, a TOTP secret or the authorization code

Scenario: BFF sign-in success
  Given a user who completes the OIDC code flow
  When the BFF creates the session
  Then it logs "auth.signin.succeeded" at Information with a fixed message template
  And the fields are only trace_id, span_id, tenant_id and the hashed user_id
  And no log line from the flow contains the access, ID or refresh token, the code, the state or the nonce

Scenario Outline: BFF sign-in failure carries a reason code from the closed list
  Given the OIDC callback fails because "<cause>"
  When the BFF handles the callback
  Then it logs "auth.signin.failed" at Warning with reason "<reason>"
  And the response to the browser is the existing generic failure
  And the log line has no "error_description", no exception message and no query string

  Examples:
    | cause                             | reason                  |
    | the user denies access            | access_denied           |
    | the state does not match          | state_invalid           |
    | the code exchange is rejected     | token_exchange_failed   |
    | Keycloak is unreachable           | remote_failure          |
    | the ticket store is unreachable   | ticket_store_unavailable |

Scenario: An attacker-controlled error text cannot reach the log
  Given the callback carries error_description "line1\nauth.signin.succeeded user=x"
  When the BFF handles it
  Then no log line contains that text
  And exactly one "auth.signin.failed" event is written

Scenario Outline: BFF sign-out
  Given a signed-in session
  When the sign-out is initiated by "<initiator>"
  Then the BFF logs "auth.signout" at Information with initiator "<initiator>" and the hashed user_id
  Examples:
    | initiator  |
    | user       |
    | backchannel |

Scenario Outline: The Api logs a rejected bearer token without token data
  Given a request with a bearer token that is "<defect>"
  When the Api rejects it with 401
  Then it logs one "auth.token.rejected" Warning with reason "<reason>"
  And the log line contains no part of the token, no claim value and no exception message
  And the 401 response body and WWW-Authenticate header are unchanged from NFR-26

  Examples:
    | defect                         | reason        |
    | expired                        | expired       |
    | signed with another key        | bad_signature |
    | wrong issuer                   | bad_issuer    |
    | wrong audience                 | bad_audience  |
    | alg none or HS256              | bad_algorithm |
    | not a JWT                      | malformed     |

Scenario: A request with no token is not an event
  Given a request to /api with no Authorization header
  When the Api answers 401
  Then no "auth.token.rejected" event is written

Scenario: No event carries a secret or a raw identifier (matrix)
  Given the full set of events above, each produced once with a recognisable canary token, password and email
  When every log line and every stored Keycloak event is scanned
  Then no canary token, password, authorization code or client secret appears in any log line or stored Keycloak event
  And no username, email or raw "sub" appears in any BFF or Api log line
  And every user_id field is the keyed hash
  And usernames and client IPs in the Keycloak event store are not counted as findings (accepted residual R-1)
```

## Non-functional requirements (new rows in `docs/requirements/nfr.md`)

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-43 | Security | The production realm holds 0 users after import, and 0 `dev-` usernames, `.test` addresses, credentials or TOTP secrets exist in its file; `sslRequired` is `all` | Realm-file static test and Testcontainers Keycloak import test (`Category=Integration`) plus the `deploy/tests` no-dev-data guard (#121) |
| NFR-44 | Security | 100% of `/api/admin/**` requests whose validated token lacks the MFA `acr` are refused with 403, a generic body, zero database commands, zero audit records and one `auth.admin.mfa_required` Warning; the check makes zero Keycloak calls per request | `Decisya.Api.Tests` unit and Integration cases (production realm, synthetic admin with TOTP) (#121) |
| NFR-45 | Security | 0 occurrences of a token, password, authorization code or client secret in any stored Keycloak login or admin event; 0 occurrences of a token, password, authorization code, `state`, nonce, cookie value, raw `sub`, email, username, provider error text or exception message in any of the four Decisya authentication events (BFF and Api logs); across the event matrix with canary values. Usernames and client IPs in Keycloak's own event store are expected (accepted residual R-1): kept 90 days, visible only to realm admins | `Decisya.Bff.Tests` and `Decisya.Api.Tests` log-capture tests; Keycloak event-store scan (`Category=Integration`) (#121) |
| NFR-46 | Reliability | Keycloak login and admin events are retained 90 days (`eventsExpiration` 7 776 000 s); each of sign-in success, sign-in failure, sign-out and token rejection produces exactly one event per occurrence | Realm configuration test; per-event log-capture tests (#121) |
| NFR-47 | Maintainability | Exactly one realm file is mounted in the stack, and the production realm differs from the dev realm only on the named allow-list; 0 unlisted differences | `deploy/tests` mount guard; realm parity test (#121) |
| NFR-48 | Security | The C-02 trigger is checked: 0 users without `synthetic=true` while the C-02 switches are off; the go-live gate file names the six blockers and is pointed to by #132 and #29 | User-check script test; static gate-file test (#121) |

The F1 target (Keycloak ready within 180 s on the NAS, ADR-0016) already covers the realm import time; no new performance row is added. #132 re-measures F1 with the realm imported.

## Plan mapping

| Story | Entitlement feature key | Plan |
| --- | --- | --- |
| 1 Production realm, no users | none (platform baseline) | all plans |
| 2 Hostname, placeholders, one file, admin console | none (platform baseline) | all plans |
| 3 `/api/admin` refuses a token without MFA | none (platform baseline; ADR-0008 no-entitlement marker) | operator only, role-gated |
| 4 Admin second-factor enrolment | none (platform baseline) | operator only, role-gated |
| 5 C-02 design, configuration and gate | none (platform baseline) | all plans |
| 6 Authentication events | none (platform baseline) | all plans |

## Informational and advice check

No story produces financial output. The disclaimer criterion does not apply.

## Independent delivery

Stories 1 and 2 deliver the realm and the mount rule. Story 4 needs the realm file from Story 1. Story 3 needs Story 4's MFA level to test against Keycloak but its Api check is unit-testable alone. Story 5 adds the policy and the gate. Story 6 splits into Keycloak (realm file), BFF and Api events. The issue is delivered as one PR by the manifest's decision (one issue, one PR); the order above is the build order.

<!-- gate: G1 | verdict: PASS | issue: #121 -->

## Traceability

Gate G5, issue #121. Paths are relative to the repository root. Lanes: Unit (Docker-free), Integration (Testcontainers; `Category=Integration`), AppHost (`Category=AppHost`, host-only), Python (`deploy/tests`, run by the `deploy-guards` CI job; Docker only for `stack_smoke.py`). Project folders: `tests/Decisya.Identity.Tests` = Identity, `tests/Decisya.Api.Tests/Authentication` = Api, `tests/Decisya.Bff.Tests` = Bff, `tests/Decisya.AppHost.Tests` = AppHost, `deploy/tests` = deploy.

Tests added at G5 (no test existed for the criterion):
- `Bff/BffTicketStoreUnavailableEventTests.An_unreachable_ticket_store_logs_exactly_one_ticket_store_unavailable_event_and_still_fails_closed` (Unit, 1 test): the `ticket_store_unavailable` row of the Story 6 sign-in failure outline.
- `Identity/Production/ProductionEventStoreTests` (Integration, 2 tests): `A_failed_and_a_successful_keycloak_login_are_recorded_and_no_event_holds_a_secret` and `Admin_events_are_stored_without_a_representation_and_hold_no_credential`.
- `Identity/Production/ProductionUnsetPlaceholderTests.A_realm_value_left_unset_makes_keycloak_refuse_to_serve_the_realm_with_a_literal_placeholder` (Integration, 1 test): G4-121-03 (d), the layer under the wrapper.
- `Api/AdminEndpointsPostgresTests.An_admin_token_without_the_MFA_level_gets_403_and_writes_no_row_and_no_audit_record` (Integration, Theory with 2 rows: no `acr`, `acr` "1"): zero trial, override and audit rows after an MFA refusal on all three admin verbs. The class's `StartAsync` gained an optional `acr` parameter (default "2", so the existing tests are unchanged).

### Story 1: The production realm imports with no seeded users

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| The production realm file holds no users | `Identity/ProductionRealmFileTests.The_file_holds_no_user_no_credential_and_no_dev_data`, `.The_raw_text_has_no_dev_user_no_test_address_and_no_dev_password_placeholder` | Unit |
| A fresh Keycloak imports the production realm and has zero users | `Identity/Production/ProductionRealmImportTests.The_production_realm_imports_with_no_seeded_users`, `.The_roles_and_the_four_protocol_mappers_are_present`, `.The_decisya_bff_client_has_the_resolved_origin_and_the_wrapper_read_secret`; `Identity/Production/ProductionIdentityCheckTests.Right_after_the_import_the_check_reports_a_healthy_realm_with_zero_users` | Integration |
| The realm requires TLS | `Identity/Production/ProductionRealmImportTests.The_realm_settings_match_the_production_invariants` | Integration |
| A development-only value in the production file is refused (names the key, never the value) | The same two `ProductionRealmFileTests` tests above are the no-dev-data check: findings are JSON paths, never values, and the build fails on any finding. The check is a test, not a separate command, so it has no red-case test of its own | Unit |
| The production realm differs from the dev realm only where allowed | `Identity/ProductionRealmParityTests.The_allow_list_is_exactly_P1_to_P8`, `.Production_differs_from_dev_only_on_the_allowed_paths`, `.Every_allowed_difference_except_P8_matches_at_least_one_real_difference`, `.Production_keeps_the_dev_values_that_the_security_review_relies_on`; red cases `.The_comparer_reports_a_difference_the_list_does_not_allow`, `.The_comparer_matches_clients_by_clientId_not_by_position`, `.A_profile_attribute_other_than_synthetic_is_not_an_allowed_difference` | Unit |

### Story 2: Hostname, placeholders, one mounted file and the admin console

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| A required realm variable is unset (fails to start, names the variable, no literal `${...}`) | Wrapper and Compose layers: `Identity/Production/ProductionWrapperTests.A_missing_host_or_port_is_refused_by_name`, `.A_missing_secret_file_is_refused`, `.A_preset_derived_value_is_refused_by_name_only`, `.A_bad_secret_file_is_refused_and_the_secret_is_never_echoed`; `deploy/test_compose_guards.test_the_keycloak_values_are_required_and_the_derived_ones_are_never_set`. Keycloak layer (added at G5): `Identity/Production/ProductionUnsetPlaceholderTests.A_realm_value_left_unset_makes_keycloak_refuse_to_serve_the_realm_with_a_literal_placeholder` (Keycloak names the client, not the variable, so the "names the variable" part is the wrapper's and Compose's). Detective layer: `Identity/Production/ProductionIdentityCheckTests.A_wildcard_in_a_redirect_uri_or_in_the_post_logout_uris_is_reported_and_reported_clean_again`; `deploy/test_production_identity.DecisionTests.test_each_placeholder_flag_can_fail_and_names_the_field_only`; static: `Identity/ProductionRealmFileTests.Every_placeholder_is_a_plain_known_name_with_no_default_and_at_most_one_per_string`, `.Each_placeholder_stands_alone_in_its_value_or_follows_nothing_but_an_origin`, `.The_wrapper_exports_exactly_the_placeholders_the_realm_uses` | Unit, Integration, Python |
| Redirect URIs, web origins and logout URLs come from the variables | `Identity/Production/ProductionRealmImportTests.The_decisya_bff_client_has_the_resolved_origin_and_the_wrapper_read_secret` (one redirect URI, `post.logout.redirect.uris` and `backchannel.logout.url` on the origin, no web origins, no `${` anywhere in the client); `Identity/Production/ProductionWrapperTests.Valid_inputs_derive_the_origin_pass_the_secret_to_the_process_and_start_with_import_realm` | Integration |
| The hostname is pinned | `Api/AdminMfaProductionRealmTests.An_admin_with_totp_gets_acr_2_and_is_accepted_and_a_tenant_user_gets_acr_1_and_is_refused_403`: the Api validates the issuer `https://id.decisya.example:8443/realms/decisya` on tokens fetched through a different (mapped localhost) address, so the `KC_HOSTNAME` pin is what makes the token valid; `AppHost/ComposeStackPublishTests.Keycloak_starts_through_the_wrapper_with_file_variables_and_never_start_dev_or_a_realm_import`. The discovery-document-with-any-Host-header check is in the manual row M1 | Integration, AppHost |
| Exactly one realm file is mounted | `deploy/test_compose_guards.test_keycloak_has_exactly_the_wrapper_the_realm_and_the_list_read_only`, `.test_each_realm_mount_rule_can_fail`, `.test_each_mount_rule_can_fail`; `deploy/test_production_identity.KeycloakMountInspectTests.test_the_expected_mounts_pass`, `.test_each_hand_edit_is_caught`, `.test_inspect_problems_runs_the_check_for_the_keycloak_container`; `deploy/test_production_realm_scope.StackFilesStayExemptTests.test_the_overlay_names_the_import_folder_only_as_the_exact_production_file`; `AppHost/ComposeStackPublishTests.The_realm_files_and_the_BFF_client_secret_stay_with_the_overlay_not_the_generated_file` | Python, AppHost |
| The entrypoint still refuses anything but the production start | `Identity/Production/ProductionWrapperTests.A_caller_argument_for_the_import_or_the_development_server_is_refused` (Theory); `deploy/test_compose_guards.test_every_launch_argument_stays_banned_for_keycloak`; `Identity/ProductionRealmFileTests.The_wrapper_has_no_trace_no_value_echo_and_one_exec_that_adds_import_realm_itself` | Integration, Python, Unit |
| The admin console stays off the app host | Static edge: `deploy/test_caddyfile.test_adapted_config_equals_the_edge_table`, `.test_each_edge_rule_can_fail` (includes the upstream-on-9000 red case); `deploy/test_compose_guards.test_the_overlay_publishes_exactly_one_port_with_required_references`, `.test_each_network_and_port_rule_can_fail`. The live 403 on `/admin/` and `/realms/master/` is `deploy/stack_smoke.py` (see M1) | Python; live is manual |
| A restart does not overwrite or break the realm | The same Keycloak import mechanism, on the dev realm: `Identity/RealmReimportTests.The_first_start_logs_the_realm_import_and_a_restart_keeps_the_realm_intact`. The production realm is not restarted in any automated test; live restart is in M1 | Integration (dev realm); production is manual |

### Story 3: `/api/admin` refuses a token without MFA

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| A platform-admin token with no MFA claim is refused (403, generic body, zero commands, zero audit records, one Warning) | `Api/AdminMfaPolicyTests.An_admin_with_acr_2_is_accepted_and_one_without_it_is_refused_with_the_generic_403_and_one_warning` (Theory), `.The_handler_succeeds_for_an_MFA_proven_admin_and_logs_one_warning_only_for_an_admin_without_the_proof`; `Api/AdminEndpointsPostgresTests.An_admin_token_without_the_MFA_level_gets_403_and_writes_no_row_and_no_audit_record` (added at G5: Postgres, zero rows and zero audit records); `Api/AdminMfaProductionRealmTests.An_admin_token_issued_without_the_step_up_is_refused_with_one_mfa_warning` | Unit, Integration |
| A token with the wrong authentication level is refused the same way | The `acr` "1" rows of the same tests; `Api/AdminMfaPolicyTests.HasMfaLevel_is_true_only_for_exactly_one_string_acr_claim_equal_to_2` (Theory: "1", "0", " 2", "2 " all false); `Api/AdminMfaProductionRealmTests.An_admin_with_totp_gets_acr_2_and_is_accepted_and_a_tenant_user_gets_acr_1_and_is_refused_403` | Unit, Integration |
| A token with the MFA level is accepted (2xx matched by exactly one audit record) | `Api/AdminMfaPolicyTests.An_admin_with_acr_2_is_accepted_and_one_without_it_is_refused_with_the_generic_403_and_one_warning`; `Api/AdminEndpointsPostgresTests.Every_2xx_admin_response_has_exactly_one_audit_record_and_every_non_2xx_none` (the token carries `acr` "2" since #121) and the other `AdminEndpointsPostgresTests`, `AdminAuthorizationTests`, `AdminRequestValidationTests`, `AdminUnmatchedRouteTests`, `CapabilitiesPostgresTests` (edited to send the MFA claim, behaviour unchanged) | Unit, Integration |
| The MFA proof is not enough without the role | `Api/AdminMfaPolicyTests.A_tenant_user_with_acr_2_is_refused_by_the_role_and_logs_no_mfa_warning`, `.A_tenant_scoped_caller_never_gets_the_proof_even_with_acr_2_and_logs_no_mfa_warning` | Unit |
| A forged or duplicated claim does not count | `Api/AdminMfaPolicyTests.HasMfaLevel_is_true_only_for_exactly_one_string_acr_claim_equal_to_2` (absent, duplicated, numeric, wrong value), `.The_claim_type_is_matched_exactly_not_case_insensitively`; unsigned or unvalidated token: `Api/AdminTokenRejectionTests.An_invalid_admin_token_gets_the_bare_401_on_every_verb_and_no_command_runs`, `Api/TokenRejectionEventTests.Each_invalid_token_row_logs_exactly_one_warning_with_its_closed_reason_and_stays_a_bare_401` (alg=none rows) | Unit |
| The check needs no call to Keycloak | Structural: the claim is parsed once into `CallerIdentity.HasMfaLevel` from the validated principal and the policy handler reads `RequestCaller`, with no HTTP dependency. Every Docker-free Api test above runs with a static test key set and no Keycloak, so a network call would fail them. There is no test that counts network calls; I judge the structure sufficient and say so here | Unit (indirect) |
| Disabling the check is possible only in Development | `Api/AdminMfaPolicyTests.False_outside_Development_fails_validation_naming_the_key`, `.A_value_that_is_not_a_boolean_fails_in_every_environment_and_never_echoes_the_value`, `.The_allowed_combinations_pass`, `.The_setting_defaults_to_true_when_the_key_is_absent`, `.AddApiAuthentication_wires_the_fail_closed_validator_into_ValidateOnStart_and_defaults_to_true`, `.In_Development_the_switch_off_accepts_an_admin_without_the_proof_and_the_default_does_not` | Unit |
| End to end against the production realm | `Api/AdminMfaProductionRealmTests` (both tests); `Identity/Production/ProductionAdminMfaFlowTests` (5 tests: TOTP admin gets one string `acr` "2"; tenant user gets "1" with no OTP prompt; admin without TOTP stops at `CONFIGURE_TOTP`; no `acr_values` gives "1"; "2" survives refresh) | Integration |

### Story 4: Every platform-admin is made to enrol a second factor

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| A new platform-admin must enrol before getting a session | `Identity/Production/ProductionAdminMfaFlowTests.An_admin_without_a_totp_credential_cannot_get_a_code_before_enrolling_then_gets_acr_2`; `Identity/Production/ProductionRealmImportTests.CONFIGURE_TOTP_and_UPDATE_PASSWORD_are_enabled_required_actions` | Integration |
| An admin with an OTP is asked for it on every sign-in | `Identity/Production/ProductionAdminMfaFlowTests.An_admin_with_a_totp_credential_gets_exactly_one_string_acr_2` (OTP prompt shown). The "wrong OTP counts toward brute-force protection" half is only the `bruteForceProtected` setting, pinned by `ProductionRealmImportTests.The_realm_settings_match_the_production_invariants`; no test submits a wrong OTP and checks the lockout counter (Keycloak's own behaviour, not Decisya code) | Integration |
| The MFA flow is part of the committed realm file | `Identity/Production/ProductionRealmImportTests.The_step_up_browser_flow_has_the_level_2_admin_switch_on_and_the_tenant_switch_off`, `.The_realm_settings_match_the_production_invariants` (`acr.loa.map`, `browserFlow`); `Identity/ProductionRealmParityTests.Production_differs_from_dev_only_on_the_allowed_paths` | Integration, Unit |
| A tenant-user keeps the password-only flow in Phase 0 | `Identity/Production/ProductionAdminMfaFlowTests.A_tenant_user_who_requests_acr_values_2_gets_acr_1_and_no_otp_prompt`; `Identity/Production/ProductionIdentityCheckTests.Turning_the_tenant_switch_on_is_reported_as_the_c02_state_and_off_again` | Integration |

### Story 5: C-02 designed, configured and gated

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| The native password policy is enforced now | `Identity/Production/ProductionRealmImportTests.The_realm_settings_match_the_production_invariants` (the exact policy string), `.The_password_list_loads_and_refuses_a_listed_password` (listed password refused, a control accepted, `notContainsUsername` enforced); `deploy/test_common_passwords.PasswordListTests` (hash, source record, format, policy names the file, 3 red cases) and `GitAttributesTests` (LF) | Integration, Python |
| The tenant-user MFA switch is documented and off | Off: `Identity/Production/ProductionRealmImportTests.The_step_up_browser_flow_has_the_level_2_admin_switch_on_and_the_tenant_switch_off`. Documented one-step switch: `deploy/test_go_live_gate.GoLiveGateFileTests.test_the_committed_file_passes` (the file names `level-2-tenant`, `kcadm.sh` and a VS 2026 / VS Code \| CLI table). Reported by the check: `ProductionIdentityCheckTests.Turning_the_tenant_switch_on_is_reported_as_the_c02_state_and_off_again`. "A tenant-user without an OTP must enrol on the next sign-in" once the switch is on is not exercised (the switch stays off in Phase 0; it is a go-live item, C-02) | Integration, Python |
| The breached-password check is designed, not integrated | Manual (M3): a documentation criterion, read in `docs/architecture/production-identity.md` (chosen option: an offline plaintext list merged as `breached-passwords.txt`; no egress, no new package; G3 reviews it before the switch). "No extension and no egress added" is held by `deploy/test_compose_guards.test_each_network_and_port_rule_can_fail` and the unchanged Keycloak image pin | Manual |
| The go-live gate carries C-02 as blocking | `deploy/test_go_live_gate.GoLiveGateFileTests` (all five tests: items C-02, C-03, C-03b, C-05, C-09, C-10, C-20 once each, the C-02 sentence verbatim twice, no address or credential) and `GateCheckCanFailTests` (8 red cases). The Done-when pointers on the GitHub issues #132 and #29 are not visible to a repository test: manual (M2) | Python; pointers manual |
| A real account in Phase 0 is a visible failure | `deploy/test_production_identity.DecisionTests.test_the_c02_trip_wire`, `.test_the_master_realm_otp_check_starts_when_the_bootstrap_secret_is_retired`, `.test_problem_text_holds_counts_and_names_of_settings_only`; `VerifyTests` (6 tests), `RealmRebuildTests` (10 tests), `NoSecretCanaryTests`; `Identity/Production/ProductionIdentityCheckTests.A_synthetic_user_is_counted_and_a_user_without_the_attribute_is_flagged`, `.The_output_holds_only_counts_and_booleans_never_a_name_or_an_id`; `Identity/Production/ProductionRealmImportTests.The_synthetic_attribute_is_admin_only_and_only_accepts_true` | Python, Integration |

### Story 6: Authentication events are emitted and tested

| Scenario | Test(s) | Lane |
| --- | --- | --- |
| Keycloak records login and admin events with a 90 day retention | `Identity/Production/ProductionRealmImportTests.The_keycloak_event_settings_are_pinned` (12 event types, `eventsListeners` empty, admin events on without details, `eventsExpiration` and `adminEventsExpiration` 7776000); static pin in `deploy/test_production_identity.DecisionTests.test_each_hardening_flag_can_fail` | Integration, Python |
| A failed and a successful Keycloak login are recorded | `Identity/Production/ProductionEventStoreTests.A_failed_and_a_successful_keycloak_login_are_recorded_and_no_event_holds_a_secret` (added at G5: one `LOGIN_ERROR`, one `LOGIN`, no password, token, TOTP secret, code or client secret) | Integration |
| BFF sign-in success | `Bff/BffAuthEventsIntegrationTests.A_dev_login_with_acr_values_2_completes_and_logs_exactly_one_succeeded_event` (Theory, 2 users; hashed `user_id`, tenant, no raw `sub`); `Bff/BffAuthEventsTests.OnSignedIn_logs_one_Information_event_with_the_hashed_user_and_the_tenant_and_restores_the_scope`, `.A_tenant_claim_that_is_not_a_canonical_non_empty_guid_is_not_put_on_the_event`, `.The_event_names_levels_and_templates_are_fixed`, `.The_acr_field_is_one_of_1_2_or_other`; existing `Bff/LogScanTests` and `Bff/TokenLeakScanTests` (no token in any record) | Unit, Integration |
| BFF sign-in failure carries a reason code from the closed list | `access_denied`: `Bff/BffAuthEventsTests.An_access_denied_callback_logs_one_warning_and_ends_in_the_generic_500_with_nothing_rethrown`. `state_invalid`: `.A_callback_with_an_unreadable_state_logs_state_invalid`, `.A_callback_without_the_correlation_cookie_logs_state_invalid`. `token_exchange_failed`: `.A_token_endpoint_error_logs_token_exchange_failed_and_never_the_provider_text`. `remote_failure`: `.A_provider_error_that_is_not_access_denied_logs_remote_failure_and_never_the_description`, `.An_unreachable_token_endpoint_logs_remote_failure`. `ticket_store_unavailable`: `Bff/BffTicketStoreUnavailableEventTests.An_unreachable_ticket_store_logs_exactly_one_ticket_store_unavailable_event_and_still_fails_closed` (added at G5). Closed list and classification by exception type only: `.The_failure_reasons_are_a_closed_list_and_classification_uses_the_exception_type_only`. Both handlers call `HandleResponse()`: `.Both_failure_handlers_call_HandleResponse_and_write_the_generic_500_themselves` | Unit |
| An attacker-controlled error text cannot reach the log | `Bff/BffAuthEventsTests.An_access_denied_callback_logs_one_warning_and_ends_in_the_generic_500_with_nothing_rethrown` and `.A_provider_error_that_is_not_access_denied_logs_remote_failure_and_never_the_description` (a unique canary in `error_description`, none in any record at Debug, exactly one `auth.signin.failed`); `.The_OpenIdConnect_handler_category_is_pinned_to_None_in_the_shipped_configuration` (the framework handler's own Error log) | Unit |
| BFF sign-out (user, backchannel) | `Bff/BffAuthEventsIntegrationTests.Signing_out_logs_one_signout_event_initiated_by_the_user`, `.A_valid_backchannel_logout_that_removed_a_session_logs_one_signout_event_and_nothing_else_does` | Integration |
| The Api logs a rejected bearer token without token data | `Api/TokenRejectionEventTests.Each_invalid_token_row_logs_exactly_one_warning_with_its_closed_reason_and_stays_a_bare_401` (expired, bad signature, issuer, audience, alg none / HS256 rows; unchanged 401 shape), `.A_malformed_token_logs_the_malformed_reason`, `.A_token_of_another_type_logs_exactly_one_bad_type_event`, `.The_catalogue_and_the_expected_reasons_cover_the_same_rows_and_only_closed_reasons` | Unit |
| A request with no token is not an event | `Api/TokenRejectionEventTests.A_missing_token_and_a_non_bearer_scheme_and_a_valid_token_log_no_rejection_event` | Unit |
| No event carries a secret or a raw identifier (matrix) | Api: `Api/AuthLogCanaryTests.No_canary_reaches_any_log_record_at_Debug_under_the_real_logging_configuration`, `.Record_which_framework_categories_would_log_a_canary_without_the_configured_thresholds`, `.The_Microsoft_AspNetCore_threshold_that_hides_framework_authentication_text_stays_at_Warning`, `.The_development_settings_never_lower_an_authentication_category`; `Api/AdminMfaPolicyTests.The_refused_admin_warning_carries_the_hashed_user_id_through_the_enrichment_and_no_raw_sub`. BFF: the canary assertions in `Bff/BffAuthEventsTests` (all categories at Debug) and `Bff/BffAuthEventsIntegrationTests` (raw `sub` absent, hashed `user_id`). Keycloak store: `Identity/Production/ProductionEventStoreTests` (both tests, added at G5). Usernames and IPs in the store are not asserted on (R-1) | Unit, Integration |

### Done when (issue #121)

| Line | Proof |
| --- | --- |
| The production realm imports with no seeded users | `Identity/Production/ProductionRealmImportTests.The_production_realm_imports_with_no_seeded_users`; `Identity/ProductionRealmFileTests.The_file_holds_no_user_no_credential_and_no_dev_data` |
| `/api/admin` refuses a token without MFA | `Api/AdminMfaProductionRealmTests` (real Keycloak tokens), `Api/AdminMfaPolicyTests`, `Api/AdminEndpointsPostgresTests.An_admin_token_without_the_MFA_level_gets_403_and_writes_no_row_and_no_audit_record` |
| The authentication events are emitted and tested | Story 6 rows above: BFF `BffAuthEventsTests`, `BffAuthEventsIntegrationTests`, `BffTicketStoreUnavailableEventTests`; Api `TokenRejectionEventTests`, `AuthLogCanaryTests`, the MFA Warning in `AdminMfaPolicyTests`; Keycloak `ProductionRealmImportTests.The_keycloak_event_settings_are_pinned`, `ProductionEventStoreTests` |
| The C-02 enforcement point is decided and documented | Decided at G1 (D1, Q1) and designed at G2 (`docs/architecture/production-identity.md`); documented in `docs/runbooks/go-live-gate.md`, held by `deploy/test_go_live_gate.GoLiveGateFileTests`; the trip-wire by `deploy/test_production_identity` (see Story 5) |

### Non-functional requirements

| Id | Proof |
| --- | --- |
| NFR-43 | `Identity/ProductionRealmFileTests` (no users, no `dev-` / `.test` / credentials), `Identity/Production/ProductionRealmImportTests.The_production_realm_imports_with_no_seeded_users` and `.The_realm_settings_match_the_production_invariants` (`sslRequired` all), `deploy/test_no_home_addresses.py` (the address scan, with its exact password-list skip pinned by `SkipPathTests`) |
| NFR-44 | `Api/AdminMfaPolicyTests`, `Api/AdminMfaCouplingTests`, `Api/AdminMfaProductionRealmTests`, `Api/AdminEndpointsPostgresTests.An_admin_token_without_the_MFA_level_gets_403_and_writes_no_row_and_no_audit_record`. "Zero Keycloak calls" is structural (see the Story 3 row) |
| NFR-45 | `Api/AuthLogCanaryTests`, `Bff/BffAuthEventsTests`, `Bff/BffAuthEventsIntegrationTests`, `Identity/Production/ProductionEventStoreTests` (Keycloak store scan) |
| NFR-46 | `Identity/Production/ProductionRealmImportTests.The_keycloak_event_settings_are_pinned` (7776000); exactly one event per occurrence: the `ContainSingle` assertions in `BffAuthEventsTests`, `BffAuthEventsIntegrationTests`, `BffTicketStoreUnavailableEventTests` and `Api/TokenRejectionEventTests` |
| NFR-47 | `deploy/test_compose_guards` mount tests (Story 2, one realm file), `Identity/ProductionRealmParityTests` (P1 to P8, 0 unlisted differences) |
| NFR-48 | `deploy/test_production_identity` (trip-wire, verify, rebuild), `deploy/test_go_live_gate`, `Identity/Production/ProductionIdentityCheckTests`. The #132 and #29 pointers are manual (M2) |

### G3 requirements (MUSTs G4-121-01 to 05)

| Must | Proof |
| --- | --- |
| G4-121-01 (a) the policy: exactly one string `acr` "2" | `Api/AdminMfaPolicyTests.HasMfaLevel_is_true_only_for_exactly_one_string_acr_claim_equal_to_2` (Theory), `.The_claim_type_is_matched_exactly_not_case_insensitively`, `.An_acr_that_is_a_json_array_of_one_string_2_is_accepted_because_it_is_one_string_claim` |
| G4-121-01 (b) coupling of every `/api/admin` endpoint | `Api/AdminMfaCouplingTests.Every_api_admin_endpoint_of_the_real_host_carries_both_policies`, `.The_route_match_is_case_insensitive_and_segment_anchored`, red rows `.A_deliberately_uncoupled_admin_endpoint_makes_the_checker_fail` and `.Stripping_the_mfa_policy_from_the_real_admin_endpoints_is_reported` |
| G4-121-01 (c) integration against the production realm | `Identity/Production/ProductionAdminMfaFlowTests`, `Api/AdminMfaProductionRealmTests` |
| G4-121-01 (d) default true, fail closed, key banned in the stack, run mode only | `Api/AdminMfaPolicyTests` (options and validator tests); `deploy/test_compose_guards.test_the_api_admin_mfa_switch_is_never_set_in_the_stack`; `AppHost/RunModeAdminMfaTests.Run_mode_turns_the_admin_MFA_requirement_off_on_decisya_api_only`; `AppHost/ComposeStackPublishTests.The_publish_model_never_sets_the_admin_MFA_requirement_so_the_Api_defaults_to_required`; `tests/Decisya.ServiceDefaults.Tests/Architecture/AppHostConfigurationTests` (the literal allowed only with value "false") |
| G4-121-01 (e) spike failure stops G4 | Process rule, not a test: the spike (D7-1 to D7-6) passed and is recorded in the manifest; its test code is superseded by the production-realm tests above. `tests/Decisya.Identity.Tests/Spike/` is blanked and must be deleted before the commit (manifest) |
| G4-121-02 RealmGuard exemption | `Identity/RealmGuardTests` (case table `Identity/realm-guard-cases.json`, 15 required rows, `.The_exemption_list_is_exactly_the_expected_set`, `.Each_exemption_rule_is_the_sole_reason_at_least_one_case_does_not_offend`, `.The_121_rule_alone_exempts_exactly_the_overlay_target_row_and_the_stackguards_constant_row`, `.No_file_in_the_working_tree_offends_the_scoped_guard`); `Identity/ScanExclusionsTests`; `deploy/test_production_realm_scope` (3 classes); `deploy/test_no_home_addresses.SkipPathTests` |
| G4-121-03 (a) wrapper validation | `Identity/Production/ProductionWrapperTests` (host forbidden characters, shape, label and name limits, port 8443 only, secret regex, missing values) |
| G4-121-03 (b) wrapper refuses preset values | `Identity/Production/ProductionWrapperTests.A_preset_derived_value_is_refused_by_name_only`; `deploy/test_compose_guards.test_the_keycloak_values_are_required_and_the_derived_ones_are_never_set` |
| G4-121-03 (c) static test of the placeholders | `Identity/ProductionRealmFileTests` (placeholder tests) |
| G4-121-03 (d) identity-check booleans for the client | `Identity/Production/ProductionIdentityCheckTests.A_wildcard_in_a_redirect_uri_or_in_the_post_logout_uris_is_reported_and_reported_clean_again`, `.Right_after_the_import_the_check_reports_a_healthy_realm_with_zero_users`; `deploy/test_production_identity.DecisionTests.test_each_placeholder_flag_can_fail_and_names_the_field_only`; the unset-at-Keycloak row, `Identity/Production/ProductionUnsetPlaceholderTests` (added at G5: Keycloak refuses the import, so there is no realm for `verify` to report on) |
| G4-121-04 (a) `bad_type` | `Api/TokenRejectionEventTests.A_token_of_another_type_logs_exactly_one_bad_type_event` (ID token and refresh-typed token) |
| G4-121-04 (b) `HandleResponse()` on every branch | `Bff/BffAuthEventsTests.Both_failure_handlers_call_HandleResponse_and_write_the_generic_500_themselves`, and the no-Error-record assertion in the callback tests |
| G4-121-04 (c) canaries at Debug on all categories | `Api/AuthLogCanaryTests`; BFF `BffAuthEventsTests` (EventsFactory forces Debug) and `.The_OpenIdConnect_handler_category_is_pinned_to_None_in_the_shipped_configuration` |
| G4-121-04 (d) scoped `user_id` | `Bff/BffAuthEventsTests.OnSignedIn_logs_one_Information_event_with_the_hashed_user_and_the_tenant_and_restores_the_scope`; `Bff/BffAuthEventsIntegrationTests` (sign-out and back-channel); `Api/AdminMfaPolicyTests.The_refused_admin_warning_carries_the_hashed_user_id_through_the_enrichment_and_no_raw_sub` |
| G4-121-04 (e) Keycloak event settings pinned | `Identity/Production/ProductionRealmImportTests.The_keycloak_event_settings_are_pinned`; `deploy/test_production_identity` hardening-flag tests |
| G4-121-05 (a) verify fails on a non-synthetic user and on a master-realm user without OTP | `deploy/test_production_identity.DecisionTests.test_the_c02_trip_wire`, `.test_the_master_realm_otp_check_starts_when_the_bootstrap_secret_is_retired`, `VerifyTests.test_the_master_realm_check_waits_for_the_retirement_of_the_bootstrap_secret`, `.test_an_unreadable_or_failed_check_is_a_problem_never_a_pass` |
| G4-121-05 (b) `realm rebuild --confirm` refusals, hard-coded database, no values printed | `deploy/test_production_identity.RealmRebuildTests` (all 10), `NoSecretCanaryTests.test_verify_and_rebuild_print_no_secret_value_in_any_outcome` |
| G4-121-05 (c) identity-check SQL read-only and statically constrained | `Identity/IdentityCheckSqlTests` (6 tests); `deploy/test_production_identity.SqlStaticTests`; real database: `Identity/Production/ProductionIdentityCheckTests`, `Identity/Production/ProductionRealmImportTests.The_tables_the_identity_check_reads_have_the_columns_it_uses` |

### Manual rows

| Id | Item | Reason |
| --- | --- | --- |
| M1 | The first `stackctl up` and `verify` on a real Linux host or the NAS, with `deploy/tests/stack_smoke.py` (live realm answers on the id host, live 403 on `/admin/` and `/realms/master/` from a non-workstation client, discovery document issuer under any Host header, a Keycloak restart that keeps an edited realm, F1 re-measured with the realm imported) | Needs the target host, its network and a real TLS edge; owned by #132. The script exists and is covered statically by `deploy/test_stackctl.py` and `test_compose_guards.py`; it has not been run against a live stack in this issue |
| M2 | The Done-when pointers to `docs/runbooks/go-live-gate.md` on GitHub issues #132 and #29 (also NFR-48's "pointed to by #132 and #29") | The text is GitHub issue content that no repository test can read. G7 proposes the edits; Marco applies and checks them at merge. `deploy/test_go_live_gate` checks only that the gate file names both issues |
| M3 | The breached-password design note (Story 5, third scenario) | A documentation criterion: read `docs/architecture/production-identity.md` (chosen option, no egress, no new package, G3 reviews before the switch). No automated test can judge prose |

### Gaps and notes

- No test counts the network calls of a valid `/api/admin` request (Story 3, "no call to Keycloak"); the check reads the validated principal only, by structure. Not added: a call-counting handler would assert on the framework's own key-set fetch more than on Decisya code.
- No test submits a wrong OTP and reads the brute-force counter (Story 4); that is Keycloak behaviour, and the setting is pinned.
- The tenant-user MFA path with the switch on is not exercised (the switch is off in Phase 0 by decision Q1); it is go-live item C-02.
- The production realm is not restarted in an automated test (Story 2, last scenario); the dev realm test covers the same Keycloak import mechanism.
- Run times when run alone: `ProductionEventStoreTests` about 2.5 minutes (it uses the shared production Keycloak fixture, so the start-up is paid once per collection), `ProductionUnsetPlaceholderTests` about 1.2 minutes (its own throwaway Keycloak in start-dev mode).
- `BffTicketStoreUnavailableEventTests` points a multiplexer at a closed local port with a fail-fast backlog; it needs no Docker and takes under 2 seconds.

<!-- gate: G5 | verdict: PASS | issue: #121 -->
