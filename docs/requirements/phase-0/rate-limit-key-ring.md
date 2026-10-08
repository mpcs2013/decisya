# Phase 0: Abuse and key protection (#122, 0.17d)

Issue #122: rate limiting (C-09) and the Data Protection key ring (C-05). Manifest: `docs/ai/pipeline/122.md`. Tier: full. Depends on #120 and #121 (both merged).

Sources read: `docs/ai/pipeline/122.md`, `docs/architecture/deployable-stack.md` (networks, D3), `docs/security/threat-models/{deployable-stack,system-baseline,bff-session}.md`, `docs/requirements/phase-0/production-identity.md` (event style), `docs/runbooks/go-live-gate.md` (C-05, C-09), ADR-0016 (R4 edge, R7 backup), `src/Decisya.Bff` (`/bff` group, `/bff/backchannel-logout`, `/api/{**catch-all}` proxy, `KeyRingPath`, session `ExpireTimeSpan` 10 h), `deploy/compose/stackguards.py` (`bff-keyring` mount table), `docs/requirements/nfr.md`. #30 (backup and restore) is cited by number: `gh` is not available to this gate.

## Scope note

Actors: the **tenant user** (signed in, `tenant-user`), the **platform operator** (Marco, `platform-admin`, in no tenant), an **anonymous caller** (anyone who can reach the edge), **Keycloak** (the only legitimate caller of back-channel logout, from the `idp` network), and the **backup job** (#30).

In scope: C-09 in the BFF (a limiter per route class, a 429 shape, a log event, a metric), the edge decision, and C-05 (location, access, protection at rest, rotation, backup and restore, key loss).

Out of scope: Keycloak's own brute-force protection (already on, #121), rate limiting inside `Decisya.Api` (it sits behind the BFF and sees only the BFF), per-tenant quotas or plan-based limits, WAF rules, log shipping (C-04), the backup tooling itself (#30; this issue states what #30 must include and adds the test that the ring is in it).

**Entitlement plan.** None of these stories is gated by a subscription plan. Abuse protection and key protection are platform controls that a plan cannot switch on or off, and limits are never a paid feature. Each story names the key as "none (platform baseline)". No `entitlements.*` key is added, and the `/api/admin` limit does not change ADR-0008's no-entitlement marker.

**Informational and advice check.** No story produces financial output. The disclaimer criterion does not apply.

## Decisions the requirements make (confirmed by Marco, 2026-10-08; see Open questions)

### D1. Limits per route class

The BFF uses the ASP.NET Core rate limiter (built in, no new package). Phase 0 is one household with synthetic users, so the limits sit far above normal use and far below a script. All values are configuration (`Bff:RateLimits:<class>:PermitLimit` and `:WindowSeconds`), validated at start-up (both at least 1; a missing value falls back to the default below; a bad value fails start-up naming the key, never a secret).

| Route class | Matches | Who it protects against | Partition key | Algorithm | Window | Limit |
| --- | --- | --- | --- | --- | --- | --- |
| `login` | `GET /bff/login`, the OIDC callback, `GET /bff/logout` | login CSRF and flood of Keycloak, anonymous | client IP | sliding window, 4 segments | 60 s | 10 |
| `backchannel_logout` | `POST /bff/backchannel-logout` | flood of the logout-token validator and Redis | client IP | sliding window, 4 segments | 60 s | 300 |
| `api` | `/api/*` (the proxy) | a runaway SPA loop, a stolen cookie scripting the API | session, else client IP | sliding window, 6 segments | 60 s | 300 per session; 60 per IP when anonymous |
| `admin` | `/api/admin/*` | cross-tenant writes at machine speed | session | sliding window, 6 segments | 60 s | 30 |

Rules:
1. **Partition key.** The session is the identity of the validated session cookie's ticket, hashed with the keyed hash (never the raw cookie value, never the access token). When there is no valid session the key is the client IP as the BFF sees it after the trusted-proxy forwarded headers (`UseDecisyaForwardedHeaders`: only the edge address is trusted). A forwarded header from any other peer is ignored. If the client address is missing or unusable, the request falls into one shared `unknown` bucket for its class (fail closed to a shared bucket, never to "no limit").
2. **Chaining.** An `/api/admin/*` request is counted in both the `admin` bucket and the `api` bucket. The tighter limit decides.
3. **Order.** The limiter runs before the proxy forwards: a rejected request makes zero calls to `Decisya.Api` and zero calls to Redis for session work beyond identifying the partition. `login` and `backchannel_logout` run before any authentication work.
4. **Back-channel logout** is called by Keycloak only. Its normal rate is a handful per day. The limit protects the validator; the partition is the IP the BFF sees (the edge forwards it), which in Phase 0 is Keycloak's own address on the `idp` network. **The requirement does not assume a retry.** Keycloak is believed not to retry a back-channel logout token, so a token refused with 429 is lost and the user's BFF session stays alive until its own expiry (`ExpireTimeSpan`, 10 h). The limit is therefore set at 300 per 60 s (Marco, 2026-10-08, after G3 S-122-01), far above Keycloak's real rate, so that legitimate logout traffic is never refused and the limiter only bites on a flood from a non-Keycloak caller. **To be confirmed:** the identity-dev spike confirms Keycloak 26.7.5's behaviour on a refused (non-2xx) back-channel logout (retry or no retry). If the spike shows no retry, this rule stands; if it shows a retry, the requirement is still valid and nothing relies on it.
5. **State is bounded.** Idle partitions are evicted, so an attacker rotating addresses cannot grow memory without limit (NAS has 8 GB shared; ADR-0016).
6. **Static assets and `/health`** are not limited by the BFF (they carry no cost worth limiting and a probe must not trip the limiter).

### D2. What a 429 looks like

- Status `429`, `Content-Type: application/problem+json`, body `ProblemDetails` with `status` 429, `title` "Too many requests", `type` the same generic URI every other generic BFF problem uses (or `about:blank`), and `traceId`. **No** `detail`, no limit value, no window, no route-class name, no partition key, no session or address.
- `Retry-After` in whole seconds, rounded up, at least 1, at most the class window (60). No `X-RateLimit-*` headers.
- No cookie is set or changed, and the response carries the security headers of every other BFF response.
- The SPA: a 429 from `/api/*` shows the shell's existing generic error state, "Too many requests. Please wait a moment and try again.", and the SPA does not retry automatically before `Retry-After`; it never shows the seconds as a promise. A 429 on `/bff/login` is a browser navigation: it renders the same ProblemDetails (no new page in Phase 0; a friendlier page is a backlog item). The SPA never receives a token on this path (CLAUDE.md BFF rule).
- **Log event** `ratelimit.rejected`, Warning, fixed message template. Allowed fields: event name, `trace_id`, `span_id`, `tenant_id` (when known), hashed `user_id` (when signed in, keyed hash as in #121), `route_class` from the closed list `login`, `backchannel_logout`, `api`, `admin`, `partition_kind` from the closed list `session`, `ip`, `unknown`. **Never:** the client address, the session or ticket key, any cookie, token, query string, path beyond the class name, header value, or request body. At most **one** event per partition per window (the first rejection); later rejections in that window only increment the counter, so an attacker cannot flood the log.
- **Metric** `decisya.bff.ratelimit.rejected` (counter, Meter `Decisya.Bff`), tag `route_class` only (no address, no user).

### D3. The edge limit

- **ADR-0016 R4 settles the Phase 0 position:** the NAS runs stock, digest-pinned Caddy, "no custom build, no DNS module, no rate-limit module". The edge limiter (`caddy-ratelimit`) is listed there as a go-live VPS item. A custom Caddy build also needs an ADR-0018 amendment and Marco's approval of a new third-party component.
- **Therefore, for #122's Done-when the edge limit is not required.** "Each route class returns 429 past its limit" is met by the BFF limiter. Everything the browser can reach goes through Caddy to the BFF, and the BFF is the first place that knows the session.
- **Phase 0 edge duties (MUST, already in #120 and re-asserted by a test):** Caddy forwards the client address in a form the BFF's trusted-proxy handling accepts, and no other peer can supply it; Caddy keeps its existing request-size cap and timeouts; the edge does not strip or rewrite `Retry-After` on a BFF 429.
- **Edge limit (SHOULD in #122, MUST before the go-live VPS):** G2 weighs (a) no edge limit in Phase 0, with C-09 split in the go-live gate into C-09a (BFF limiter, closed by #122) and C-09b (edge limit, blocking public-internet exposure, implemented with `caddy-ratelimit` after an ADR-0018 amendment); (b) a custom Caddy build now. The proposed default is (a). The edge limit, when built, is coarse (a per-address ceiling well above the BFF limits, so the BFF response, not the edge's, is what a normal client sees first) and also returns 429 with `Retry-After`.
- The BFF limiter is the defence in depth under the edge limiter later, not replaced by it.

### D4. The key ring (C-05)

| Question | Decision |
| --- | --- |
| Where | The `bff-keyring` volume from #120, mounted at the path in `Bff__DataProtection__KeyRingPath`. Never Redis (bff-session T-09). `PersistKeysToFileSystem` as today; `SetApplicationName("Decisya.Bff")` unchanged. |
| Who may read | Only the BFF container user. The volume's directory is owned by that user, mode `0700`, key files `0600`. Mounted read-write in the BFF only; the one other mount allowed is a read-only mount in the `backup` service (#30), already in the stack guard table. No other service, no host path, no Caddy, no Api. The existing guard stays and gains the owner/mode check. |
| Protection at rest | `ProtectKeysWithCertificate` with an RSA 3072 (or larger) certificate held as a PFX file and its password file under `/run/secrets` (file secrets per #120 D3; `stackctl.py secrets init` generates them from a CSPRNG; never printed, never in an image, `environment:` or `.env`). The wrapping key is never on the key ring volume and never in the backup. Outside Development, start-up **fails closed** if the certificate setting is unset, the file is missing, or it is not an RSA key of at least 3072 bits, naming the key and never a value. In Development the setting stays optional and DPAPI/default behaviour is unchanged (the AppHost still never sets `KeyRingPath`). A hand-rolled `IXmlEncryptor` is not acceptable. |
| Rotation of the wrapping key | At the Phase 0 exit, on the move to the VPS, on suspected compromise, and at least yearly (same moments as the `user_id_hash_key` row of the C-20 table). Procedure: generate a new certificate; make it the protector; keep the old one as unprotect-only (`UnprotectKeysWithAnyCertificate`) until every key it protects has expired, which is the key lifetime (90 days) plus the longest session lifetime (10 h, `ExpireTimeSpan`) after the new certificate is active; then remove it. Sessions survive the rotation. |
| Rotation of the keys themselves | Automatic, 90 days (`SetDefaultKeyLifetime(90 days)`, the framework default made explicit so a change is a visible diff). |
| Backup | Included in #30's nightly set, as ADR-0016 R7.1 already says ("a copy of the Data Protection key ring into the `dumps` folder"), taken by the backup job's read-only mount, into the Hyper Backup encrypted folder. The copy holds only encrypted key files. The wrapping certificate is **not** in the backup: Marco keeps it off the NAS with the Hyper Backup password. Redis is never in the backup (R7.1), so no archive holds ticket ciphertext and the ring together. |
| Restore test | The restore drill (#30) restores the ring onto an empty volume with the wrapping certificate present, starts the BFF, and proves a payload protected before the backup still unprotects. Also proven: the restore without the certificate makes the BFF refuse to start (below), it does not mint a silent new ring. Sessions do not survive a restore (Redis is not restored), and that is expected. |
| Loss of the keys or of the wrapping certificate | **All sessions become invalid; users sign in again.** Nothing else breaks: the ring protects only the session ticket and antiforgery tokens; no stored financial data is encrypted with it. The BFF does **not** silently create a new ring over an existing undecryptable one (T-09: "a fresh key ring looks healthy"): it fails start-up with a generic message and a full-detail log. Recovery is an explicit, documented operator step (move the old ring aside, empty the volume, restart; a new ring and new sessions follow). Every old cookie is then rejected as unauthenticated (401), never a 500. |

## Change record

- Marco, 2026-10-08, after G3 S-122-01: (1) `backchannel_logout` limit 30 to 300 per 60 s per client IP (D1 table, Q1, Story 2, Story 6 defaults; no row in `docs/requirements/nfr.md` stated 30, so none changed). (2) D1 rule 4 corrected: no Keycloak retry is assumed, a refused token is lost and the session lives until its expiry; to be confirmed by the identity-dev spike on Keycloak 26.7.5. G1 verdict stays PASS.

## Open questions for Marco

None. All seven were answered by Marco on 2026-10-08 and take the proposed default. Recorded as decided:

| # | Question | Decision (Marco, 2026-10-08) |
| --- | --- | --- |
| Q1 | Limit values in D1 (10 / 300 / 300 / 30 per minute) | As in D1. All are configuration, so a wrong value is a setting change, not a code change. Changed by Marco, 2026-10-08, after G3 S-122-01: `backchannel_logout` 30 to 300; rule 4 corrected (no retry assumed; to be confirmed by the identity-dev spike on Keycloak 26.7.5). |
| Q2 | Edge limit in Phase 0 (D3) | Option (a): not built in #122; C-09 is split into C-09a (closed by #122) and C-09b (blocking before the public go-live VPS, needs an ADR-0018 amendment and `caddy-ratelimit`). No custom Caddy build in this PR. |
| Q3 | 429 on a `/bff/login` navigation | Plain ProblemDetails, no new page in Phase 0. |
| Q4 | Wrapping key mechanism | Certificate (PFX and password file in `/run/secrets`). G2 may propose an alternative only if it also reads from a file secret and uses a framework encryptor. |
| Q5 | Is the wrapping certificate backed up? | No. Marco keeps a copy off the NAS with the Hyper Backup password. Losing it costs one sign-in, not data. |
| Q6 | Undecryptable existing ring at start-up | Fail closed (refuse to start), with an explicit operator reset. |
| Q7 | Log one event per partition per window | Yes, with a metric for the rest. |

## Stories

Notation: "the stack" is the Compose project from #120; "the BFF" is `Decisya.Bff`. Lanes: Unit, Integration (Testcontainers, `Category=Integration`) and the Python static guards in `deploy/tests`. Tests lower the limits through the configuration keys of D1.

### Story 1: Login and logout start are rate limited (C-09, `login`)

*As a platform operator, I want the sign-in start to refuse a flood from one address, so that a script cannot hammer Keycloak through the BFF.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: The login class returns 429 past its limit
  Given the limit for "login" is 10 per 60 seconds and a client address A
  When address A sends 10 requests to GET /bff/login
  Then each is answered by the normal redirect to the identity provider
  When address A sends an 11th request inside the same window
  Then the response is 429 with Content-Type "application/problem+json"
  And the Retry-After header is a whole number of seconds between 1 and 60
  And no request reached Keycloak for the 11th

Scenario: Another address is not affected
  Given address A has used its 10 requests
  When address B sends a request to GET /bff/login
  Then it is answered by the normal redirect

Scenario: The window slides
  Given address A was refused with a Retry-After of N seconds
  When N seconds have passed on the fake clock
  Then address A's next request to GET /bff/login is answered normally

Scenario: A forged forwarded header from an untrusted peer does not change the partition
  Given a request from a peer that is not the trusted edge address
  When it sends "X-Forwarded-For" naming a fresh address on every request
  Then the requests are counted in the peer's own bucket
  And the 11th is answered 429

Scenario: A forwarded header from the trusted edge selects the client's bucket
  Given a request from the trusted edge with "X-Forwarded-For" naming client address C
  When 11 such requests arrive for C
  Then the 11th is answered 429
  And a request for another client address D is not refused

Scenario: A missing client address falls into a shared bucket, not into no limit
  Given requests where the client address cannot be determined
  When 11 arrive inside the window
  Then the 11th is answered 429
```

### Story 2: Back-channel logout is rate limited (C-09, `backchannel_logout`)

*As a platform operator, I want the back-channel logout endpoint to refuse a flood, so that a caller who is not Keycloak cannot make the BFF validate logout tokens and hit Redis without limit.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: The back-channel class returns 429 past its limit
  Given the limit for "backchannel_logout" is 300 per 60 seconds
  When one address posts 300 logout tokens to /bff/backchannel-logout
  Then each is handled as before (valid tokens end their session, invalid ones get the existing generic refusal)
  When the same address posts a 301st inside the window
  Then the response is 429 with Retry-After and the generic ProblemDetails
  And the logout token is not validated and Redis is not called

Scenario: Normal logout traffic is never refused
  Given Keycloak sends 5 valid back-channel logouts in a minute
  Then all 5 end their sessions and none is answered 429

Scenario: A refused logout token is not assumed to be retried
  Given the limit for "backchannel_logout" is lowered to 2 per 60 seconds in the test
  And one address has posted 2 logout tokens
  When it posts a valid logout token for session S as a 3rd request and is answered 429
  Then session S is still alive, and the test does not re-send the token
  And no documentation or test states that Keycloak will retry the refused token

Scenario: Keycloak's retry behaviour is confirmed by the spike (to be confirmed)
  Given Keycloak 26.7.5 and a back-channel logout endpoint that answers 429
  When the identity-dev spike logs a user out
  Then the spike records whether Keycloak retries the logout token
  And the result is written into the threat model finding S-122-01 closure and the runbook

Scenario: The back-channel limit is independent of the login limit
  Given one address has exhausted the "login" class
  When the same address posts to /bff/backchannel-logout
  Then it is not refused for the login limit
```

### Story 3: The API proxy is rate limited per session (C-09, `api`)

*As a tenant user, I want my own requests counted separately from everyone else's, so that a runaway script on one session cannot slow the API for another user, and so that I am never refused in normal use.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: The api class returns 429 past its limit, per session
  Given the limit for "api" is 300 per 60 seconds per session and two signed-in sessions S1 and S2
  When S1 sends 300 requests to /api/tenancy/me
  Then each is forwarded to the Api
  When S1 sends a 301st inside the window
  Then the response is 429 with Retry-After and the generic ProblemDetails
  And the request is not forwarded: the Api receives no 301st call
  And S2's next request is forwarded normally

Scenario: Two sessions behind one address are not merged
  Given S1 and S2 come from the same client address
  When S1 exhausts its limit
  Then S2 is unaffected

Scenario: The same session is one bucket across tabs and addresses
  Given S1 sends requests from two client addresses
  Then they are counted together

Scenario: An anonymous caller is limited by address and still gets 401
  Given the anonymous limit for "api" is 60 per 60 seconds per address
  When one address sends 60 unauthenticated requests to /api/anything
  Then each is answered 401 as before
  When it sends a 61st
  Then the response is 429, not 401

Scenario: The partition key is not the raw cookie
  Given a signed-in session
  When any 429 is produced and the logs and metrics are read
  Then no cookie value, ticket key or session identifier appears in them

Scenario: Normal household use never trips the limit
  Given a scripted SPA journey: load the shell, open every page, 40 API calls in one minute
  When it runs as one signed-in session
  Then 0 responses are 429

Scenario: The SPA handles a 429 without a loop
  Given the Api proxy answers 429 with Retry-After 20
  When the SPA receives it
  Then it shows "Too many requests. Please wait a moment and try again."
  And it sends no automatic retry before 20 seconds
  And no token or limit value is shown
```

### Story 4: The admin API has its own tighter limit (C-09, `admin`)

*As a platform operator, I want `/api/admin` writes limited more tightly than the rest of the API, so that a stolen admin session cannot change entitlements for many tenants at machine speed.*

Entitlement: none (platform baseline; ADR-0008 no-entitlement marker unchanged). Plan: operator only, role-gated.

```gherkin
Scenario: The admin class returns 429 past its limit
  Given the limit for "admin" is 30 per 60 seconds per session
  When an admin session sends 30 requests to /api/admin/tenants
  Then each is forwarded
  When it sends a 31st inside the window
  Then the response is 429 with Retry-After and the generic ProblemDetails
  And the Api receives no 31st call, so no audit record exists for it (NFR-39 holds)

Scenario: Admin requests also count against the api class
  Given the "api" limit is lowered to 5 per window in the test
  When an admin session sends 6 requests to /api/admin/tenants
  Then the 6th is answered 429 by the api class

Scenario: A refused admin request changes nothing
  Given an admin session that has been answered 429
  Then zero entitlement rows changed and zero audit records were written for the refused request

Scenario: The 429 is the same shape as every other class
  Given 429 responses from the "login", "backchannel_logout", "api" and "admin" classes
  Then their bodies differ only in traceId and nothing names the class
```

### Story 5: A rejection is observable without leaking identity (C-09 logging)

*As a security reviewer, I want each rejection recorded with a closed set of fields, so that I can see an attack without the log becoming a list of addresses and sessions.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: One rejection produces one event with allowed fields only
  Given a partition that exceeds its limit once
  When the BFF logs the rejection
  Then one "ratelimit.rejected" Warning is written with a fixed message template
  And its fields are only trace_id, span_id, tenant_id (when known), the hashed user_id (when signed in), route_class and partition_kind
  And route_class is one of login, backchannel_logout, api, admin
  And partition_kind is one of session, ip, unknown

Scenario Outline: Nothing sensitive reaches the log or the metric
  Given a 429 produced with a canary "<canary>" in the request
  When all log lines and metric tags are scanned
  Then the canary does not appear
  Examples:
    | canary                         |
    | a recognisable client address  |
    | a recognisable session cookie  |
    | a query string value           |
    | an X-Forwarded-For value       |
    | a bearer token                 |

Scenario: A flood does not flood the log
  Given one partition is refused 500 times inside one window
  Then exactly one "ratelimit.rejected" event is written for it
  And the counter "decisya.bff.ratelimit.rejected" with route_class reads 500

Scenario: The limiter's state is bounded
  Given requests from 20000 distinct addresses, each seen once
  When the idle eviction has run
  Then the number of tracked partitions is back below 1000
```

### Story 6: The limits are configuration and fail closed (C-09 configuration)

*As a platform operator, I want a bad limit setting to stop the start instead of silently disabling the limiter, so that abuse protection cannot be turned off by a typo.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: Defaults apply when nothing is configured
  Given no Bff:RateLimits keys
  When the BFF starts
  Then the limits are 10 (login), 300 (backchannel_logout), 300 (api; 60 anonymous) and 30 (admin) per 60 seconds as in the decisions

Scenario Outline: An invalid value fails start-up and names the key
  Given "<key>" is set to "<value>"
  When the BFF starts outside Development
  Then start-up fails naming "<key>" and never echoing a secret
  Examples:
    | key                                   | value |
    | Bff:RateLimits:login:PermitLimit      | 0     |
    | Bff:RateLimits:api:WindowSeconds      | -5    |
    | Bff:RateLimits:admin:PermitLimit      | abc   |

Scenario: The limiter is in the request pipeline in the right order
  Given the BFF's pipeline
  Then forwarded-headers handling runs before the limiter
  And the limiter runs before the reverse proxy forwards
  And every mapped /bff and /api route has a limiter policy, so a new route without one fails a test
```

### Story 7: The edge keeps the client address trustworthy and does not hide the 429 (C-09, edge)

*As a platform operator, I want the edge to pass only a client address it set itself and to leave the BFF's 429 intact, so that the BFF limiter partitions on a value an attacker cannot choose.*

Entitlement: none (platform baseline). Plan: all plans. Edge limiter: SHOULD (D3); this story's criteria are MUST.

```gherkin
Scenario: The edge overwrites a client-supplied forwarded header
  Given the running stack
  When a client sends an "X-Forwarded-For" header naming an address it invented to the app host
  Then the BFF sees the connecting client's address, not the supplied one

Scenario: A 429 passes through the edge unchanged
  Given the BFF answers 429 with Retry-After 20 and a ProblemDetails body
  When the response goes through the edge
  Then the client sees status 429, the same Retry-After and the same body

Scenario: Phase 0 adds no custom Caddy build
  Given the stack's Compose file and Caddy image reference
  Then the Caddy image is the stock digest-pinned image of ADR-0016 R4
  And the go-live gate lists the edge limit as C-09b, blocking before the public go-live VPS

Scenario: The go-live gate shows the split
  Given docs/runbooks/go-live-gate.md
  Then C-09 is closed for the BFF limiter by #122
  And C-09b (edge limit) is open and blocks the public-internet exposure
```

### Story 8: The key ring lives on its own volume, readable by the BFF only (C-05)

*As a platform operator, I want the Data Protection key ring on a volume that only the BFF can read, so that nobody on the box, and no other container, can forge a session cookie.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: The ring is persisted across restarts
  Given a BFF started with Bff:DataProtection:KeyRingPath on a volume
  When a session is created, the BFF is restarted and the same cookie is presented
  Then the session still authenticates (NFR-23 holds with the certificate enabled)

Scenario: Only the BFF mounts the volume read-write
  Given the stack's Compose definition
  When the static guards run
  Then "bff-keyring" is mounted read-write only in the BFF
  And the only other allowed mount is read-only in a service named "backup"
  And a mount in the Api, Caddy, Keycloak, Redis, Postgres or the migrator fails the guard

Scenario: The directory is private to the BFF user
  Given the running stack
  When the key ring directory and a key file are inspected
  Then the directory is owned by the BFF container user with mode 0700
  And every key file has mode 0600
  And the volume is not a host path

Scenario: The key ring path is still required outside Development
  Given Bff:DataProtection:KeyRingPath unset
  When the BFF starts in Production
  Then start-up fails naming the key (existing validator, unchanged)

Scenario: The key ring is never in Redis
  Given a running BFF
  When Redis is scanned for Data Protection key material
  Then none is found
```

### Story 9: The key ring is encrypted at rest (C-05)

*As a security reviewer, I want every key on disk encrypted with a key that is not on the same volume, so that a copy of the volume or of a backup is useless without the wrapping key.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: Key files hold no plaintext key material
  Given a BFF started with the protection certificate and a new key ring
  When the key files are read
  Then each file has an "encryptedSecret" element
  And no file has a plaintext "masterKey" element
  And no warning "No XML encryptor configured" is logged

Scenario: The wrapping key comes from a file secret, not from the volume
  Given the stack
  Then the certificate and its password are Compose secrets mounted at /run/secrets
  And neither is in the key ring volume, an image layer, "environment:", a .env file or a command line

Scenario: Outside Development a missing or weak protection key stops the start
  Given the protection certificate setting is unset, points to a missing file, or is an RSA key of 2048 bits
  When the BFF starts in Production
  Then start-up fails naming the setting and never a value or a path content

Scenario: Development is unchanged
  Given the Development environment and no certificate setting
  When the BFF starts
  Then it starts with the default per-user protection and the AppHost sets no KeyRingPath

Scenario: A ring copied without the certificate cannot be read
  Given a copy of the key files and no certificate
  When a protector tries to unprotect a payload with that copy
  Then it fails

Scenario: Rotating the wrapping certificate loses no session
  Given sessions created under certificate 1 and certificate 2 made the protector, with certificate 1 kept as unprotect-only
  When the BFF is restarted
  Then every earlier session still authenticates
  And new keys are protected by certificate 2
  And with certificate 1 removed while a key it wraps is still live, the BFF refuses to start (D11), so no user is served and none sees a 401 or a 500

Scenario: The key lifetime is explicit
  Given the BFF configuration
  Then the Data Protection default key lifetime is 90 days, set in code
```

### Story 10: The key ring is in the backup and a restore brings it back (C-05, #30)

*As a platform operator, I want a restore drill that proves the key ring comes back readable, so that I know the backup holds what it should and nothing it should not.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: The nightly set includes the encrypted key ring
  Given the backup definition delivered by #30
  When the backup runs
  Then the dumps folder contains a copy of every key file from the bff-keyring volume
  And the copy is taken through the backup service's read-only mount
  And the copy contains no plaintext key material

Scenario: Nothing that would unlock the ring or the sessions is in the same archive
  Given the backup set
  Then it contains neither the protection certificate nor its password
  And it contains no Redis data

Scenario: Restore brings the ring back
  Given a payload protected by the BFF before the backup, a backup, and an empty bff-keyring volume
  When the ring is restored onto the volume with the protection certificate present and the BFF started
  Then the BFF starts
  And the payload protected before the backup unprotects
  And the restored files have the owner and modes of Story 8

Scenario: Restore without the certificate does not mint a silent new ring
  Given the restored key files and no certificate
  When the BFF starts in Production
  Then it refuses to start with a generic message and a full-detail log line
  And no new key file is written to the volume

Scenario: The drill is recorded
  Given the runbook for the restore drill
  Then it lists the key ring restore and the certificate-missing case as steps
  And the drill result is recorded in the same place as the database restores
```

### Story 11: Key loss means everyone signs in again, nothing worse (C-05)

*As a platform operator, I want a lost key ring to cost me only a sign-in, with a written recovery step, so that a lost volume or certificate is an inconvenience and not an outage or a silent security gap.*

Entitlement: none (platform baseline). Plan: all plans.

```gherkin
Scenario: An undecryptable existing ring stops the BFF, it is not replaced silently
  Given a key ring whose keys cannot be unprotected with the configured certificate
  When the BFF starts in Production
  Then it fails start-up within 30 seconds with a generic message
  And the full detail goes to the structured log with a trace id
  And the key files on the volume are untouched

Scenario: The documented reset gives new sessions and invalidates the old ones
  Given the operator moves the old ring aside, leaves the volume empty and restarts the BFF
  When a user who had a cookie before the reset calls /api
  Then the response is 401 with the generic body, never 500
  When the user signs in again
  Then a new session works and the new key file is encrypted

Scenario: An empty volume is a normal first start
  Given an empty bff-keyring volume and a valid certificate
  When the BFF starts
  Then a new ring is created, encrypted, with the owner and modes of Story 8

Scenario: Nothing else depends on the ring
  Given the key ring was reset
  Then no database row, no Redis key other than session tickets, and no Keycloak setting is affected

Scenario: The go-live gate and the stack documentation record the decision
  Given docs/runbooks/go-live-gate.md and docs/architecture/deployable-stack.md
  Then C-05 states location, reader, protection, rotation, backup and the key-loss behaviour
  And C-05 is closed only when the restore drill of Story 10 is recorded
```

## Non-functional requirements (new rows in `docs/requirements/nfr.md`)

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-49 | Security | For each of the four route classes (`login`, `backchannel_logout`, `api`, `admin`), request number limit+1 inside the window gets 429 and requests 1 to limit do not; 100% of rejected requests make 0 calls to `Decisya.Api`, Keycloak or the session store beyond partition identification | `Decisya.Bff.Tests` per-class unit tests with a fake clock and lowered limits (#122) |
| NFR-50 | Security | 100% of 429 responses are `application/problem+json` with `status`, `title` and `traceId` only, a whole-second `Retry-After` between 1 and 60, and 0 occurrences of a limit value, class name, partition key, address or session identifier in body or headers | `Decisya.Bff.Tests` response-shape test across the four classes (#122) |
| NFR-51 | Security | The partition key never comes from a header supplied by an untrusted peer: 0 of the requests that vary `X-Forwarded-For` from an untrusted peer escape the peer's bucket; a request with no usable client address is limited in a shared bucket, never unlimited; tracked partitions fall below 1000 after eviction following 20 000 single-use addresses | `Decisya.Bff.Tests` forwarded-header and eviction tests (#122) |
| NFR-52 | Usability | 0 responses with 429 during the scripted single-user journey (shell load, every page, 40 API calls in a minute) at the default limits | Playwright case on the stack or a `Decisya.Bff.Tests` replay (#122) |
| NFR-53 | Performance efficiency | The limiter adds at most 2 ms to p95 BFF request latency at 50 concurrent requests, measured with the limiter on versus off on the same host; a rejection logs at most 1 event per partition per window | Timed `Decisya.Bff.Tests` comparison and log-capture test (#122) |
| NFR-54 | Security | 100% of Data Protection key files on the `bff-keyring` volume carry `encryptedSecret` and 0 carry plaintext `masterKey`; the volume directory is mode 0700 and key files 0600, owned by the BFF user; `bff-keyring` is mounted read-write in 1 service (the BFF) and read-only in at most 1 other (`backup`); the wrapping certificate and password are in 0 images, environment variables, volumes or backups | Key-file scan in `Decisya.Bff.Tests`; `deploy/tests` mount and permission guards; stack smoke (#122) |
| NFR-55 | Reliability | A key ring restored from the nightly copy with the wrapping certificate unprotects 100% of payloads protected before the backup; the restore is part of the drill within RPO 24 h and RTO 4 h (NFR-02) | Restore drill (#30) plus a `Decisya.Bff.Tests` copy-and-restore test (#122) |
| NFR-56 | Reliability | With an existing ring that cannot be unprotected, the BFF refuses to start within 30 s and writes 0 new key files; after the documented reset, 100% of pre-reset cookies get 401 (0 responses with status 500) and a new sign-in succeeds; rotating the wrapping certificate with the old one kept as unprotect-only loses 0 sessions | `Decisya.Bff.Tests` integration cases (`Category=Integration`) (#122) |

## Plan mapping

| Story | Entitlement feature key | Plan |
| --- | --- | --- |
| 1 `login` limit | none (platform baseline) | all plans |
| 2 `backchannel_logout` limit | none (platform baseline) | all plans |
| 3 `api` limit per session | none (platform baseline) | all plans |
| 4 `admin` limit | none (platform baseline; ADR-0008 no-entitlement marker) | operator only, role-gated |
| 5 Rejection logging | none (platform baseline) | all plans |
| 6 Limit configuration | none (platform baseline) | all plans |
| 7 Edge address and 429 pass-through | none (platform baseline) | all plans |
| 8 Key ring access | none (platform baseline) | all plans |
| 9 Encryption at rest | none (platform baseline) | all plans |
| 10 Backup and restore | none (platform baseline) | all plans |
| 11 Key loss | none (platform baseline) | all plans |

## Independent delivery

Stories 1 to 6 are the BFF limiter, one policy per story plus the shared 429 shape (Story 3 and Story 5 define the shape and the event the others reuse; build order 6, 1, 3, 2, 4, 5). Story 7 is edge verification and the go-live gate edit. Stories 8 to 11 are the key ring: 8 (guards) is independent; 9 needs the secrets from `stackctl.py secrets init`; 10 needs #30's backup (the criteria that need the `backup` service are manual or deferred until #30 lands and are recorded as such at G5); 11 follows 9. The manifest's decision is one issue, one PR, in that order.

<!-- gate: G1 | verdict: PASS | issue: #122 -->

## Traceability

G5, test-engineer, 2026-10-08. Paths: B = `tests/Decisya.Bff.Tests`, D = `deploy/tests`, W = `src/Decisya.Web`. Rate-limit tests are in `B/RateLimiting/` (unit lane: `RouteClassLimitTests`, `PartitionUnitTests`, `ForwardingAndLogTests`; Integration lane: `SessionPartitionTests`); key-ring tests are in `B/KeyRing/`. At G5 seven tests were added for the seven gaps Marco asked to close (2026-10-08): `RateLimiting/BackchannelLogoutLimitTests` (2), `HouseholdJourneyTests` (1), `BearerCanaryTests` (2 cases), `LimiterLatencyTests` (1), `KeyRing/SessionKeyRingTests` (3) and two cases in `KeyRingStartupTests` for the start-up message. `BackchannelLogoutLimitTests`, `HouseholdJourneyTests`, `LimiterLatencyTests` and `SessionKeyRingTests` are `Category=Integration` (real Redis container; the latency and journey tests carry generous margins against host noise); `BearerCanaryTests` and `KeyRingStartupTests` are `Category=Unit`. The session key-ring host sets the `Bff:DataProtection:*` settings through the environment-variable path (`EagerConfigurationGuard`), because `AddBffDataProtection` reads them eagerly. Tests that need a POSIX host skip on Windows and run in CI.

Changes at G5: (1) S-122-09 applied: the last clause of Story 9's rotation scenario now matches D11 (retiring certificate 1 while a key it wraps is live makes the BFF refuse to start; no 401 is seen). (2) Story 1, "The window slides": the criterion says "fake clock". Spike R4 found that the framework limiter has no `TimeProvider` hook, so the test uses a short real window and the real replenishment heartbeat (`A_refused_partition_recovers_after_its_window_through_the_real_heartbeat_and_state_is_tracked`). Deviation recorded, not a gap. The once-per-window log slot does use the NodaTime `IClock` (`FakeClock`).

### Done when

| Line | Proof |
| --- | --- |
| Each route class returns 429 past its limit | `RouteClassLimitTests`: `Login_class_returns_429_past_its_limit_and_another_address_is_not_affected`, `Backchannel_logout_class_returns_429_past_its_limit_and_is_independent_of_login`, `Api_class_returns_429_not_401_past_the_anonymous_limit`, `Admin_class_returns_429_past_its_own_limit`; `SessionPartitionTests` for sessions |
| Key ring persisted, encrypted at rest, BFF-only, in the backup plan | Persisted and encrypted: `KeyRingStartupTests` (first start, restart, rotation). BFF-only: `D/test_compose_guards.py` (`test_each_key_ring_rule_can_fail`, `test_the_bff_alone_gets_the_four_certificate_files`), `D/test_keyring.py` (`ScriptTextTests`, `ArgvTests`). Backup plan: `D/test_go_live_gate.py` (C-05 statements, `test_each_122_statement_can_fail`). The backup itself: manual/deferred (#30), see Story 10 |

### Story 1: login

| Scenario | Test |
| --- | --- |
| 429 past limit | `RouteClassLimitTests.Login_class_returns_429_past_its_limit_and_another_address_is_not_affected`; Retry-After shape: `Every_class_returns_the_same_generic_problem_that_names_nothing`; `PartitionUnitTests.Retry_after_is_the_computed_window_over_segments_clamped` |
| Another address is not affected | same test as above |
| The window slides | `RouteClassLimitTests.A_refused_partition_recovers_after_its_window_through_the_real_heartbeat_and_state_is_tracked` (short real window, not a fake clock: spike R4) |
| Forged header from an untrusted peer | `ForwardingAndLogTests.A_forged_forwarded_header_from_an_untrusted_peer_stays_in_the_peers_own_bucket` |
| Forwarded header from the trusted edge | `ForwardingAndLogTests.The_trusted_edge_selects_the_clients_bucket_and_the_log_says_ip` |
| Missing address, shared bucket | `ForwardingAndLogTests.No_address_at_all_is_one_shared_bucket_not_no_limit`; `PartitionUnitTests.A_missing_or_unspecified_address_is_the_shared_unknown_bucket` |

Also: `RouteClassLimitTests.The_OIDC_callback_paths_are_the_login_class_whatever_their_case`, `SessionPartitionTests.A_refused_oidc_callback_with_a_session_cookie_does_no_session_fixation_work`.

### Story 2: back-channel logout

| Scenario | Test |
| --- | --- |
| 429 past limit, validator not reached | `RouteClassLimitTests.Backchannel_logout_class_returns_429_past_its_limit_and_is_independent_of_login` (lowered limit; token not validated is shown by the generic 429 on a token that would otherwise be refused differently). The 300 default: `PartitionUnitTests.Defaults_apply_when_nothing_is_configured` |
| Normal logout traffic is never refused | `BackchannelLogoutLimitTests.Five_valid_logouts_a_minute_at_the_default_limit_are_never_refused`; the 300 default: `PartitionUnitTests.Defaults_apply_when_nothing_is_configured` |
| A refused token is not assumed to be retried | `BackchannelLogoutLimitTests.A_refused_logout_token_leaves_the_session_alive_and_is_not_sent_again`. Docs claim no retry: spike K1 (manifest) |
| Keycloak retry behaviour confirmed by the spike | manual: spike K1, Keycloak 26.7.5 sent one logout, got 429, no retry within 100 s (manifest, G4 evidence). Throwaway spike, not repeatable |
| Independent of the login limit | same test as the first row (login 1, backchannel 2) |

### Story 3: api per session

| Scenario | Test |
| --- | --- |
| 429 per session, Api receives no extra call | `SessionPartitionTests.The_api_class_counts_per_session_and_a_refused_request_never_reaches_the_api` |
| Two sessions behind one address | same test (one peer, S1 refused, S2 served) |
| Same session across addresses is one bucket | same test (second address, S1 refused) |
| Anonymous limited by address, still 401 | `RouteClassLimitTests.Api_class_returns_429_not_401_past_the_anonymous_limit`; `Me_endpoint_belongs_to_the_api_class` |
| Partition key is not the raw cookie | `SessionPartitionTests` (log scanned for cookie and name); `ForwardingAndLogTests.A_flood_writes_one_event_with_closed_fields_and_the_counter_counts_every_refusal`; `PartitionUnitTests.The_partition_code_reads_no_request_header` |
| Normal household use never trips the limit | `HouseholdJourneyTests.A_household_journey_at_the_default_limits_gets_no_429` (0 x 429); also `Defaults_apply_when_nothing_is_configured`, `RouteClassLimitTests.Static_assets_and_the_shell_are_not_limited` |
| The SPA handles a 429 without a loop | `W/src/rate-limit.test.ts` (9 tests, e.g. `returns RATE_LIMITED, not the anonymous identity, and calls once without retry`, `is the fixed text with no digits`); the Playwright case below is manual |

### Story 4: admin

| Scenario | Test |
| --- | --- |
| 429 past limit, Api receives no extra call | `SessionPartitionTests.The_admin_class_has_its_own_session_limit_and_a_refused_admin_request_makes_no_api_call`; `RouteClassLimitTests.Admin_class_returns_429_past_its_own_limit` |
| Admin also counts against api | `RouteClassLimitTests.Admin_requests_also_count_against_the_api_class` |
| A refused admin request changes nothing | `SessionPartitionTests` (zero Api calls means no entitlement write and no audit record; the Api is a loopback double, so database rows are not asserted) |
| Same shape for every class | `RouteClassLimitTests.Every_class_returns_the_same_generic_problem_that_names_nothing` |

Admin path forms: `PartitionUnitTests.Admin_path_forms_are_the_admin_class`, `Other_api_paths_stay_in_the_api_class`, `An_unmapped_bff_or_api_path_is_limited_as_api_never_unlimited`.

### Story 5: observability

| Scenario | Test |
| --- | --- |
| One event, allowed fields only | `ForwardingAndLogTests.A_flood_writes_one_event_with_closed_fields_and_the_counter_counts_every_refusal`; `SessionPartitionTests` (event 1820, tenant, hashed user); `PartitionUnitTests.A_refusal_is_flagged_for_logging_once_per_window` |
| Canary outline | Partly: query string, X-Forwarded-For value and client address: `ForwardingAndLogTests` (canary tests in `A_forged_forwarded_header...` and the closed-fields test). Session cookie: `SessionPartitionTests`. Bearer token: `BearerCanaryTests.A_bearer_token_on_a_limited_request_appears_in_no_log_record_body_or_header` (api and login paths) |
| A flood does not flood the log | `ForwardingAndLogTests.A_flood_writes_one_event_with_closed_fields_and_the_counter_counts_every_refusal` |
| The state is bounded | `PartitionUnitTests.Idle_partitions_are_evicted_by_the_real_heartbeat_after_20000_single_use_addresses`; `Disposing_synchronously_or_asynchronously_releases_the_tracked_count_once` |

### Story 6: configuration

| Scenario | Test |
| --- | --- |
| Defaults | `PartitionUnitTests.Defaults_apply_when_nothing_is_configured` |
| Invalid value fails and names the key | `PartitionUnitTests.An_invalid_or_unknown_setting_fails_validation_naming_the_key_and_never_the_value`, `An_invalid_limit_stops_the_host_start_in_every_environment` |
| Pipeline order, every route has a policy | `ForwardingAndLogTests.Every_bff_and_api_endpoint_carries_a_route_class`; `RouteClassLimitTests.The_OIDC_callback_paths_are_the_login_class_whatever_their_case`; `BffBoundaryTests.Only_types_in_Decisya_Bff_RateLimiting_depend_on_the_rate_limiter_namespaces`. The relative order of forwarded headers and limiter is proved by behaviour (`ForwardingAndLogTests` trusted-edge test) |

### Story 7: edge

| Scenario | Test |
| --- | --- |
| Edge overwrites a forged header | `D/test_caddyfile.py`: `test_no_header_up_on_a_forwarding_header`, `test_forwarding_headers_are_never_set_copied_or_deleted`; live check: manual, `D/stack_smoke.py` (11 forged-XFF logins, 429, one event 1820 `(login, ip)`) on Linux/NAS (#132) |
| A 429 passes through the edge unchanged | `D/test_caddyfile.py`: `test_no_handle_response_no_intercept_no_retry_after`, `test_a_429_and_its_retry_after_pass_through_the_app_host`; live: manual (#132) |
| No custom Caddy build | `D/test_compose_guards.py` image-pin rules (`test_each_keycloak_production_and_image_rule_can_fail`, `test_assembled_stack_passes_every_guard_with_fixture_values`); `D/test_go_live_gate.py::test_the_table_closes_c09a_and_keeps_c09b_open_and_blocking` |
| The go-live gate shows the split | `D/test_go_live_gate.py`: `test_the_table_closes_c09a_and_keeps_c09b_open_and_blocking`, `test_the_old_c09_item_is_gone`, `test_a_reopened_c09a_or_a_closed_c09b_is_flagged`, `test_each_122_statement_can_fail` |

### Story 8: key ring access

| Scenario | Test |
| --- | --- |
| Persisted across restarts | `KeyRingStartupTests.A_restart_with_the_same_certificate_still_unprotects` (payload level); with a real session cookie: `SessionKeyRingTests.A_session_cookie_still_authenticates_after_a_host_restart_with_the_same_wrapped_ring` |
| Only the BFF mounts it read-write | `D/test_compose_guards.py`: `test_each_mount_rule_can_fail`, `test_each_key_ring_rule_can_fail`, `test_the_bff_alone_gets_the_four_certificate_files` |
| Directory private to the BFF user (0700/0600, not a host path) | `D/test_keyring.py`: `ScriptTextTests.test_prepare_sets_owner_and_modes_without_following_links`, `ArgvTests.test_no_tool_has_a_bind_mount_...`; `D/test_compose_guards.py::test_a_named_volume_has_no_driver_no_driver_opts_and_no_name_override`; `KeyRingStartupTests.A_directory_open_to_group_or_other_fails_on_unix` (POSIX only, skips on Windows); live inspection: manual (#132) |
| Key ring path required outside Development | existing validator tests (unchanged); `KeyRingStartupTests.A_missing_directory_fails_closed` |
| The ring is never in Redis | `BffBoundaryTests.The_key_ring_is_never_stored_in_Redis_or_a_database` (structure); runtime Redis scan: manual (#132) |

### Story 9: encryption at rest

| Scenario | Test |
| --- | --- |
| No plaintext key material | `KeyRingStartupTests.An_empty_directory_is_a_normal_first_start_with_one_certificate_wrapped_key`, `A_key_written_through_NullXmlEncryptor_is_plaintext_even_though_it_has_an_encryptedSecret`, `A_key_with_a_foreign_decryptor_or_a_stray_masterKey_or_an_expired_plaintext_key_refuses`, `A_plaintext_ring_from_the_previous_stack_refuses_to_start`; `KeyRingValidationAndGeneratorTests.The_detector_needs_both_the_certificate_decryptor_and_an_EncryptedData_element`. The "No XML encryptor" log line is not asserted (the start-up check refuses a ring that lacks the certificate encryptor) |
| Wrapping key from a file secret | `D/test_keyring.py::GuardTests` (`test_the_four_certificate_secrets_belong_to_the_bff_alone`, `test_the_committed_overlay_names_the_four_files_for_the_bff`, `test_the_certificate_names_are_never_environment_names`); `D/test_compose_guards.py` secret rules |
| Missing or weak key stops the start | `KeyRingValidationAndGeneratorTests.A_missing_certificate_fails_outside_Development_and_passes_in_Development`, `A_weak_certificate_fails_in_every_environment_and_the_message_leaks_nothing`, `Garbage_fails_naming_the_key_only`, `A_wrong_password_fails_without_echoing_either_value`; `KeyRingStartupTests.The_check_runs_outside_Development_even_without_a_certificate` |
| Development unchanged | `KeyRingStartupTests.Development_without_a_certificate_runs_no_check_and_keeps_the_unwrapped_local_ring`; AppHost sets no KeyRingPath: existing AppHost tests |
| A copy without the certificate cannot be read | `KeyRingStartupTests.A_copy_of_the_ring_without_the_certificate_cannot_be_read` |
| Rotating the certificate loses no session | `KeyRingStartupTests.Rotation_keeps_every_earlier_payload_readable_and_reports_the_previous_only_expiry`. Last clause (S-122-09, D11): `KeyRingStartupTests.Retiring_the_previous_certificate_while_a_key_it_wraps_is_live_fails_closed`. Sessions: `SessionKeyRingTests.A_session_cookie_survives_certificate_rotation_and_new_keys_are_wrapped_by_the_new_certificate` |
| Key lifetime is explicit | `KeyRingStartupTests.The_default_key_lifetime_is_90_days_set_in_code` |

### Story 10: backup and restore (#30)

| Scenario | Test |
| --- | --- |
| Nightly set includes the encrypted ring | manual/deferred: the `backup` service is delivered by #30. Statement of what #30 must hold: `D/test_go_live_gate.py` C-05 statements; mount rule for a read-only `backup` mount: `D/test_compose_guards.py::test_each_mount_rule_can_fail` |
| No certificate, password or Redis data in the set | manual/deferred (#30). Go-live and runbook wording tested in `D/test_go_live_gate.py::test_each_122_statement_can_fail` |
| Restore brings the ring back | manual/deferred (#30 drill). Copy-and-restore at unit level (NFR-55): `KeyRingStartupTests.A_restart_with_the_same_certificate_still_unprotects`, `A_copy_of_the_ring_without_the_certificate_cannot_be_read`; modes after restore: `D/test_keyring.py::ResetOnAFixtureVolumeTests` and `KeyringCommandTests.test_prepare_runs_the_exact_argv_and_prints_counts_only` (POSIX fixtures skip on Windows) |
| Restore without the certificate refuses to start, writes nothing | `KeyRingStartupTests.An_existing_ring_without_the_certificate_refuses_to_start_and_writes_no_key` |
| The drill is recorded | manual/deferred (#30): runbook step and recorded result; runbook text for the key ring in `docs/runbooks/deployable-stack.md` |

### Story 11: key loss

| Scenario | Test |
| --- | --- |
| Undecryptable ring stops the BFF, files untouched | `KeyRingStartupTests.An_existing_ring_without_the_certificate_refuses_to_start_and_writes_no_key`, `Retiring_the_previous_certificate_while_a_key_it_wraps_is_live_fails_closed`; generic message and trace id: `KeyRingStartupTests.A_start_up_failure_shows_only_the_generic_message_and_the_full_detail_log_line_carries_the_trace_id` and `The_failure_line_carries_a_trace_id_from_the_check_own_activity_when_the_caller_has_none`. Note: the trace id in the second case comes from a change identity-dev made in parallel to `KeyRingStartupCheck`; the first test asserts a trace id is present when an `Activity` exists, so it passes both before and after that change. The 30 s bound is by construction (the check is a local file read) |
| Documented reset: old cookies get 401, new sign-in works | Reset mechanics: `D/test_keyring.py` (`KeyringCommandTests.test_reset_stops_the_bff_deletes_then_prepares_and_does_not_start_anything`, `ResetOnAFixtureVolumeTests`). The 401-not-500 and new-sign-in outcome: `SessionKeyRingTests.After_a_key_ring_reset_a_pre_reset_cookie_gets_401_never_500_and_a_new_sign_in_works` (real key files; the old Redis ticket stays in Redis but is unreadable on the new ring and degrades to null); live: also in the stack smoke (#132) |
| An empty volume is a normal first start | `KeyRingStartupTests.An_empty_directory_is_a_normal_first_start_with_one_certificate_wrapped_key`; `D/test_keyring.py::KeyringCommandTests.test_prepare_on_a_first_start_lets_compose_create_the_volume` |
| Nothing else depends on the ring | `BffBoundaryTests.The_key_ring_is_never_stored_in_Redis_or_a_database`, `Only_types_in_Decisya_Bff_KeyRing_depend_on_the_key_management_namespaces`; no database or Keycloak setting involved (by design) |
| Gate and documentation record the decision | `D/test_go_live_gate.py::test_each_122_statement_can_fail`, `test_the_committed_file_passes` |

### NFR rows

| NFR | Proof |
| --- | --- |
| NFR-49 | `RouteClassLimitTests` (four classes); `SessionPartitionTests` (zero Api calls for refused requests). Zero Keycloak/Redis calls beyond partition identification: `SessionPartitionTests.A_refused_oidc_callback_with_a_session_cookie_does_no_session_fixation_work` |
| NFR-50 | `RouteClassLimitTests.Every_class_returns_the_same_generic_problem_that_names_nothing`; `PartitionUnitTests.Retry_after_is_the_computed_window_over_segments_clamped` (Retry-After is `ceil(window / segments)`, a deviation from the G1 "rounded-up window"; manifest Part 2) |
| NFR-51 | `ForwardingAndLogTests` (forged header, no address); `PartitionUnitTests.An_address_equal_to_a_trusted_proxy_means_forwarding_did_not_apply_and_is_unknown`, `Idle_partitions_are_evicted_by_the_real_heartbeat_after_20000_single_use_addresses` |
| NFR-52 | `HouseholdJourneyTests.A_household_journey_at_the_default_limits_gets_no_429`; `W/src/rate-limit.test.ts` |
| NFR-53 | `LimiterLatencyTests.The_limiter_adds_little_p95_latency_at_50_concurrent_requests`. Deviation: "limiter off" removes only the limiter's evaluation (`GlobalLimiter` is null); the partition middleware and the `UseRateLimiter` middleware stay in both arms, and the measured path is anonymous `/api` with distinct peers. So it bounds the cost of evaluating the limiter, not of the whole middleware. The log part (one event per partition per window) is proved by `ForwardingAndLogTests.A_flood_writes_one_event_with_closed_fields_and_the_counter_counts_every_refusal` |
| NFR-54 | Key-file scan: `KeyRingStartupTests` first-start and detector tests; mounts and secrets: `D/test_compose_guards.py`, `D/test_keyring.py::GuardTests`; modes and owner on a real volume: manual (#132); POSIX file-mode tests skip on Windows, run in CI |
| NFR-55 | Unit level: see Story 10 restore rows. The drill itself: manual/deferred (#30) |
| NFR-56 | Refuse to start and write nothing: `KeyRingStartupTests.An_existing_ring_without_the_certificate_refuses_to_start_and_writes_no_key`; rotation loses nothing: `Rotation_keeps_every_earlier_payload_readable_and_reports_the_previous_only_expiry`. 401-not-500 after reset and the new sign-in: `SessionKeyRingTests.After_a_key_ring_reset_...` (Story 11). Tests are in the unit lane here, not `Category=Integration` as the NFR row says |

### G4-122 MUSTs

| MUST | Proof |
| --- | --- |
| G4-122-01 (address, replenishment/eviction, OIDC path case) | `PartitionUnitTests` (`An_address_equal_to_a_trusted_proxy...`, `A_missing_or_unspecified_address...`, `Idle_partitions_are_evicted_by_the_real_heartbeat...`, `Disposing_synchronously_or_asynchronously...`, `The_OIDC_handler_paths_are_login_whatever_their_case`, `Admin_path_forms_are_the_admin_class`); `RouteClassLimitTests.A_refused_partition_recovers_after_its_window_through_the_real_heartbeat_and_state_is_tracked`; smoke check `partition_kind=ip`: manual (#132) |
| G4-122-02 (strict plaintext definition) | `KeyRingStartupTests` NullXmlEncryptor, foreign decryptor, stray masterKey, expired plaintext key, previous-stack ring; `KeyRingValidationAndGeneratorTests.The_detector_*` |
| G4-122-03 (privileged tools, generator, volume bans) | `D/test_keyring.py` (`ArgvTests`, `ScriptTextTests`, `GuardTests.test_the_generator_flag_is_banned_in_command_entrypoint_and_healthcheck`); `KeyRingValidationAndGeneratorTests.The_generator_*`; `D/test_compose_guards.py::test_each_key_ring_rule_can_fail`. POSIX shell fixtures skip on Windows, run in CI |
| G4-122-04 (no plaintext key after reset) | `D/test_keyring.py::ResetOnAFixtureVolumeTests` (`test_reset_deletes_every_failing_key_file_and_keeps_the_rest_unchanged`, `test_reset_all_keys_deletes_every_key_file_and_nothing_else`, `test_verify_is_clean_after_a_reset`); go-live and runbook wording: `D/test_go_live_gate.py::test_each_122_statement_can_fail` |

### Manual or deferred

| Item | Reason |
| --- | --- |
| Stack smoke (`D/stack_smoke.py`): forged XFF through Caddy, 429 and Retry-After pass-through, `ratelimit.rejected (login, ip)`, real owner/modes, Redis scan | Needs a Linux host or the NAS; tracked in #132 |
| Story 10 criteria (backup copy, no certificate in the archive, restore drill, drill record) | Need #30's backup service |
| Playwright `shell.spec.ts` "a 429 from /bff/me shows the too-many-requests notice..." (with axe) | Needs the full stack; CI does not run Playwright until #123. Marco runs it locally |
| POSIX-only key-ring script fixtures and file-mode tests (14 skipped in `deploy/tests` on Windows, `A_directory_open_to_group_or_other_fails_on_unix`) | Skip on Windows, run in CI |
| Keycloak no-retry confirmation (Story 2) | Throwaway spike K1 on Keycloak 26.7.5; result in the manifest |

### Gaps (all closed at G5 by the seven added tests)

1. Story 2: "Normal logout traffic is never refused" and "A refused logout token is not assumed to be retried". Closed by `BackchannelLogoutLimitTests` (2 tests).
2. Story 3 and NFR-52: "Normal household use never trips the limit" (scripted journey, 0 responses 429). Closed by `HouseholdJourneyTests`.
3. Story 5: the bearer-token canary row. Closed by `BearerCanaryTests`.
4. NFR-53: timed p95 comparison, limiter on versus off. Closed by `LimiterLatencyTests`.
5. Story 8 and Story 9 session-level claims (same cookie after restart, sessions survive rotation). Closed by `SessionKeyRingTests` (restart, rotation).
6. Story 11 and NFR-56: after the documented reset, a pre-reset cookie gets 401 (never 500) and a new sign-in works. Closed by `SessionKeyRingTests.After_a_key_ring_reset_a_pre_reset_cookie_gets_401_never_500_and_a_new_sign_in_works`. Note: after the test host was fixed to read the reset ring, the old cookie got 401, so there is no product finding. The Redis ticket survives in Redis, but `RetrieveAsync` returns null (graceful degrade), because the ticket body is itself Data Protection-protected.
7. Story 11: the generic start-up message and the trace id in the full-detail log line. Closed by `KeyRingStartupTests`, the two trace-id tests (including the no-caller-Activity trace-id test).

Run evidence (green):

- `SessionKeyRingTests` 3/3.
- Bff unit lane 284/284.
- Bff Integration lane 99/99.
- `KeyRingStartupTests` 16/16.
- `LimiterLatencyTests` passed three runs in a row.
- `BackchannelLogoutLimitTests` 2/2, `HouseholdJourneyTests` 1/1, `BearerCanaryTests` 2/2.

The PASS-WITH-NOTES verdict rests on the manual and deferred rows above: the stack smoke (#132), the backup (#30), the Playwright test (#123), the POSIX-only fixtures (they run in CI), and Keycloak spike K1.

<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #122 -->

