# Phase 0 – BFF: cookie auth, OIDC, Redis ticket store, antiforgery, logout

## Issue 0.06 (#18) — BFF session pattern

### Scope note (role framing)

Every story below is written **"As a tenant user"**: unlike #17 (identity
*infrastructure* with no tenant-observable behaviour beyond a reachable login
page), #18 is the first issue where a real person's browser completes a login,
holds a session, calls an endpoint and signs out. The mechanism behind each
story (Redis, Data Protection, antiforgery) is infrastructure, but every one of
them has a directly tenant-observable consequence, so the tenant-user framing
holds throughout instead of switching to "platform operator".

**Entitlement plan:** N/A for every story in this issue. Signing in, holding a
session, reading one's own identity and signing out are core platform
infrastructure available identically on every subscription plan; nothing here
is gated by an entitlement feature key. `IEntitlementService` (ADR-0008) and
any `module.feature` key belong to the entitlements module, which does not
exist yet in phase 0.

**Scope, tightly bound to issue #18 (per the manifest and ADR-0003):**

- Login: OIDC authorization-code flow with PKCE (S256) against the
  `decisya-bff` confidential client (#17), driven by ASP.NET Core's cookie and
  OpenID Connect authentication handlers in a new `Decisya.Bff` host.
- The session cookie and its security flags (`HttpOnly`, `Secure`,
  `SameSite=Strict`); the OIDC correlation/nonce cookies (`SameSite=Lax`,
  per ADR-0003 — `Strict` would break the callback).
- A Redis-backed `ITicketStore` (ADR-0007's Redis-protocol store) holding the
  authentication ticket (including the tokens) server-side, protected with
  ASP.NET Core Data Protection, so the cookie itself never carries a token.
- Antiforgery (double-submit cookie/header pattern) enforced on every
  state-changing BFF endpoint — in #18's own surface, that is the sign-out
  endpoint; the mechanism is the one later `/api` endpoints (#19+) reuse.
- A user-info endpoint for the SPA (`GET /bff/me`, reusing the path ADR-0008
  already names for the later capability manifest) that returns the caller's
  identity claims without ever handling a token client-side.
- Logout: RP-initiated sign-out (BFF clears the cookie and the Redis ticket,
  then redirects through Keycloak's end-session endpoint) **and**
  back-channel logout (Decision 1 below: included in #18, not deferred).

**Out of scope, with the owning issue:**

- YARP forwarding of `/api/*` to `Decisya.Api` with the user's access token
  attached server-side — #19.
- Token refresh and the per-session lock against concurrent refreshes — #19.
  Until #19 ships, a session may need a fresh full login sooner than
  Keycloak's SSO idle timeout (1800 s) would otherwise allow, because the
  access token in the ticket (≤ 300 s, NFR-15) is never refreshed; #18 does
  not paper over this with an early refresh mechanism of its own.
- Serving the SPA's compiled static assets. No SPA project exists yet in this
  phase; #18's own acceptance criteria are proven against an `HttpClient`-driven
  test harness (the style #17 already established for Keycloak), not a real
  browser. ADR-0003's "SPA and BFF share one origin" is unaffected — whichever
  issue adds the SPA project serves it from the same `Decisya.Bff` host.
- `IEntitlementService` and the capability-manifest fields on `/bff/me` —
  the entitlements module, not yet scheduled. #18's `/bff/me` returns only
  identity claims (Decision 2 below); the entitlements module extends the
  same response additively later, so the SPA never has to migrate to a new
  path.
- JWT validation in `Decisya.Api` (`iss`, `aud`, `exp`, alg allow-list) — #20.

---

### Story 1 — A tenant user logs in and receives a correctly-flagged session cookie; the browser never sees a token (the Done-when)

As a tenant user, I want to sign in through Keycloak's hosted login form and
have the Decisya BFF give my browser a secure session cookie, so that I can
use Decisya without my browser ever holding an access token, an ID token or a
refresh token that a compromised page script could steal.

#### Acceptance criteria

```gherkin
Feature: OIDC login sets a secure session cookie and the browser never receives a token

  Scenario: A tenant user completes login and receives a correctly-flagged session cookie
    Given the Decisya BFF is running at https://localhost:7200
    And "dev-alice" is a seeded tenant user in the decisya realm
    When a browser starts the BFF's login challenge and completes Keycloak's
      hosted login form as dev-alice with her seeded password
    Then the BFF establishes a session and redirects the browser back to itself
    And the response that establishes the session sets a cookie with the
      HttpOnly, Secure and SameSite=Strict attributes
    And no access token, ID token or refresh token value appears in any
      response header, cookie or body at any point in the flow

  Scenario: The OIDC correlation and nonce cookies use SameSite=Lax
    Given a browser starts the BFF's login challenge
    When the BFF redirects the browser to Keycloak's authorization endpoint
    Then the correlation and nonce cookies the BFF sets carry SameSite=Lax
    And they carry HttpOnly and Secure

  Scenario: The BFF uses authorization code flow with PKCE S256
    Given a browser starts the BFF's login challenge
    When the resulting authorization request reaches Keycloak
    Then it uses response_type=code
    And it includes a code_challenge with code_challenge_method=S256
    And the PKCE code verifier is generated and kept server-side; the browser
      never receives it

  Scenario: A tampered callback is rejected
    Given a browser holds an authorization-code callback URL from a login
      challenge it started
    When that callback is replayed with a modified "state" value
    Then the BFF rejects the callback and establishes no session
```

---

### Story 2 — A tenant user's session survives a BFF restart because the ticket lives in Redis, not the cookie

As a tenant user, I want my signed-in session to remain valid across a BFF
service restart, so that a deployment or process restart doesn't force me to
sign in again just because the process that was holding my session happened
to bounce.

#### Acceptance criteria

```gherkin
Feature: Session tickets live server-side in Redis, protected with Data Protection

  Scenario: The session cookie carries no token; the ticket lives in Redis
    Given a tenant user has completed login and holds a session cookie
    When the raw value of that session cookie is inspected
    Then it contains no access token, ID token or refresh token value
    And the authentication ticket for that session is found in the
      Redis-backed ticket store, not encoded in the cookie

  Scenario: The stored ticket is protected, not plaintext
    Given a valid session ticket exists in the Redis-backed ticket store
    When the raw bytes stored in Redis for that ticket are inspected
    Then the access token and ID token values from that session do not appear
      in them in plaintext

  Scenario: A session survives a BFF process restart
    Given a tenant user has completed login and holds a session cookie
    When the BFF host process is restarted while Redis keeps running
    And the same session cookie is then presented to the BFF again
    Then the request is authenticated as the same tenant user
```

---

### Story 3 — A tenant user is signed out safely, not left broken or exposed, when Redis is unavailable

As a tenant user, when the session store is temporarily unavailable, I want to
be told plainly that I'm signed out rather than see a broken page, a stack
trace, or a session that silently keeps working on stale data, so that I know
what happened and that no unsafe shortcut was taken with my session.

#### Acceptance criteria

```gherkin
Feature: A Redis outage degrades sessions safely, without leaking detail or crashing

  Scenario: Login fails closed when Redis is unreachable
    Given the Redis-backed ticket store is unreachable
    When a tenant user completes Keycloak's hosted login form
    Then the BFF returns a generic error response to the browser, not a
      session
    And the response carries no stack trace or internal exception detail
    And the full error detail, with a trace id, is written only to the
      structured log

  Scenario: An existing session is treated as unauthenticated when Redis becomes unreachable
    Given a tenant user already holds a session cookie from before an outage
    And the Redis-backed ticket store then becomes unreachable
    When that cookie is presented to a protected BFF endpoint
    Then the BFF treats the request as unauthenticated
    And it does not serve the caller's claims from any local cache instead

  Scenario: Session validation fails fast, not slow
    Given the Redis-backed ticket store is unreachable
    When a request carrying a session cookie reaches the BFF
    Then the BFF responds within 2 seconds
    And it does not hang waiting on the Redis connection
```

---

### Story 4 — State-changing BFF requests are rejected unless they carry a valid antiforgery token

As a tenant user, I want the BFF to reject any state-changing request that
didn't genuinely originate from the Decisya application, so that a malicious
website can't use my browser's ambient session cookie to act as me (CSRF).

#### Acceptance criteria

```gherkin
Feature: State-changing BFF endpoints require a valid antiforgery token

  Scenario: A state-changing request without an antiforgery token is rejected
    Given a tenant user holds a valid session cookie
    When a POST request to a state-changing BFF endpoint is sent with the
      session cookie but with no antiforgery header
    Then the BFF rejects it with 403
    And no state change happens

  Scenario: A state-changing request with a mismatched antiforgery token is rejected
    Given a tenant user holds a valid session cookie and a valid antiforgery cookie
    When a POST request is sent with the session cookie and an antiforgery
      header value that does not match the antiforgery cookie
    Then the BFF rejects it with 403

  Scenario: A state-changing request with a valid antiforgery token pair succeeds
    Given a tenant user holds a valid session cookie and has fetched the
      matching antiforgery cookie and header value from the BFF
    When a POST request to a state-changing BFF endpoint is sent with both
    Then the BFF processes the request normally

  Scenario: A safe, read-only request needs no antiforgery token
    Given a tenant user holds a valid session cookie
    When a GET request to the user-info endpoint is sent with no antiforgery
      header
    Then the BFF processes the request normally
```

---

### Story 5 — A tenant user's browser learns who they are without ever handling a token

As a tenant user, I want the Decisya SPA to know who I am and which tenant I
belong to without the browser ever handling a security token itself, so that
my identity stays available to the app while the actual credential stays out
of reach of any script running on the page.

#### Acceptance criteria

```gherkin
Feature: A token-free user-info endpoint for the SPA

  Scenario: An authenticated user gets her identity claims
    Given "dev-alice" has completed login and holds a valid session cookie
    When she calls the BFF's user-info endpoint (GET /bff/me) with that cookie
    Then the response is 200
    And it reports that she is authenticated, together with her subject
      identifier, email, tenant id and roles
    And it contains no access token, ID token or refresh token value anywhere
      in the body or the response headers

  Scenario: An anonymous caller gets a non-error "not signed in" response
    Given no session cookie is presented
    When the BFF's user-info endpoint is called
    Then the response is 200
    And it reports that the caller is not authenticated
    And it includes no identity claim and no token value

  Scenario: A platform-admin user's response carries no tenant id
    Given "dev-admin" has completed login and holds a valid session cookie
    When he calls the BFF's user-info endpoint
    Then the response reports his platform-admin role
    And it includes no tenant id field holding a fabricated or empty value
```

---

### Story 6 — A tenant user signs out and the session ends everywhere

As a tenant user, I want signing out of Decisya to end my session in the
browser, at the BFF and at Keycloak all at once, so that no one who gets hold
of my computer afterwards can reuse a leftover session to keep acting as me.

#### Acceptance criteria

```gherkin
Feature: RP-initiated sign-out ends the session at the browser, the BFF and Keycloak

  Scenario: A tenant user signs out
    Given "dev-alice" holds a valid session cookie and a matching antiforgery
      token pair
    When she calls the BFF's sign-out endpoint with the session cookie and the
      antiforgery token
    Then the BFF clears her session cookie
    And the corresponding ticket is deleted from the Redis-backed ticket store
    And the browser is redirected towards Keycloak's end-session endpoint with
      client_id and the registered post-logout redirect URI, and no
      id_token_hint parameter
    And the redirect's Location header (and every other browser-visible part
      of the response) carries no id_token_hint and no access token, ID token
      or refresh token value of any kind

  Scenario: Keycloak completes the logout redirect
    Given the sign-out redirect from the previous scenario
    When Keycloak processes the end-session request
    Then it redirects the browser back to the BFF's registered post-logout
      callback
    And no server error is returned

  Scenario: A cookie from before sign-out no longer works
    Given "dev-alice" has signed out
    When the previously valid session cookie is presented to a protected BFF
      endpoint afterwards
    Then the BFF treats the request as unauthenticated

  Scenario: Sign-out itself requires a valid antiforgery token
    Given "dev-alice" holds a valid session cookie
    When she calls the BFF's sign-out endpoint with the session cookie but
      with no valid antiforgery header
    Then the BFF rejects it with 403
    And her session remains valid
```

---

### Story 7 — A session ended at Keycloak is invalidated at the BFF, even without the browser doing anything

As a tenant user, I want my Decisya session to end automatically when my
session is ended at the identity provider — for example, an administrator
revoking it, or me signing out of another application sharing the same
Keycloak session — even if my browser never sends another request, so that a
revoked session can't keep quietly working through Decisya.

#### Acceptance criteria

```gherkin
Feature: Back-channel logout invalidates the local ticket without browser interaction

  Scenario: Keycloak's back-channel logout deletes the local ticket
    Given "dev-bob" has completed login through the BFF and holds a valid
      session
    When Keycloak sends a back-channel logout request for dev-bob's session
      to the BFF's registered back-channel-logout endpoint, carrying a
      validly signed logout token
    Then the BFF deletes the corresponding ticket from the Redis-backed
      ticket store
    And a request that later presents dev-bob's session cookie is treated as
      unauthenticated

  Scenario: An invalid logout token is rejected
    Given a back-channel logout request arrives at the BFF's endpoint
    When its logout token has an invalid signature, the wrong issuer, or the
      wrong audience
    Then the BFF rejects the request
    And no ticket is deleted

  Scenario: A replayed logout token has no further effect
    Given a valid back-channel logout request has already been processed for
      a session
    When the same logout token is sent to the BFF again
    Then the BFF returns a success response, per the OpenID Connect
      Back-Channel Logout specification's idempotency expectation
    And it performs no further ticket deletion, because the ticket is already
      gone
```

---

## Open questions for Marco — answered

Both carried a recommended answer; that recommendation is adopted below so
G2/G3/G4 have a concrete answer to build against. Flag either one at any later
gate if it should be revisited.

1. **Is back-channel logout in #18, or deferred to #19? — Answered: in #18.**
   - **Answer:** included, per Story 7 above. ADR-0003's own "Enforced by"
     line names "back-channel logout deletes the ticket" as one of the tests
     the pattern requires, and #17's architecture note already earmarks
     adding the `backchannel.logout.url` client attribute to
     `deploy/keycloak/decisya-realm.json` to #18, not to a later issue. #19's
     scope (YARP forwarding, token refresh) has no natural owner for logout,
     so deferring it would leave it homeless rather than smaller.
   - Rejected alternative: defer to a later issue — would leave ADR-0003's own
     enforcement list unmet by the issue that ADR-0003 is written for, with no
     issue yet named to pick it up.

2. **Does GET /bff/me return 401 or 200 with `isAuthenticated: false` for an
   anonymous caller? — Answered: 200 with `isAuthenticated: false`.**
   - **Answer:** Story 5's second scenario. ADR-0008 frames this endpoint as
     the SPA's own way to know its state ("UI hides, API denies"); a 401 on
     the SPA's first, routine check of "am I signed in?" would make every
     unauthenticated page load look like a request failure rather than a
     normal state, and would need special-casing in the SPA's HTTP client to
     avoid a console error on every anonymous visit. 401 is still correct,
     and used, for genuinely protected endpoints (Story 6's sign-out; #19's
     `/api/*`).
   - Rejected alternative: 401 for every unauthenticated call, including this
     one — technically simpler, worse for the SPA's own state-detection use
     case that ADR-0008 exists to serve.

## Decisions (product owner, 2026-09-27)

1. **`GET /bff/me` is the user-info endpoint**, reusing the path ADR-0008
   already names for the later capability manifest. #18 returns only identity
   claims (`isAuthenticated`, subject, email, `tenantId` when present, roles);
   the entitlements module adds capability fields to the same response later,
   additively, so the SPA integrates against a stable path from #18 onward
   instead of migrating later.
2. **Back-channel logout ships in #18** (open question 1). `Decisya.Bff`
   exposes a back-channel-logout endpoint and `deploy/keycloak/decisya-realm.json`
   gains the client's `backchannel.logout.url` attribute; both are in scope
   for #18's PR.
3. **Session-ticket lifetime tracks the OIDC handler's own defaults** at login
   time (Story 1/2); #18 does not add its own sliding-expiration or
   token-refresh logic ahead of #19, per the "Out of scope" note above.
4. **Entitlement plan: N/A** for every story — session infrastructure is not
   subscription-gated.
5. **G1 amendment (Marco, G2 D3, 2026-09-27): the end-session redirect in
   Story 6 scenario 1 carries no `id_token_hint`.** Recorded at G2
   (`docs/architecture/bff-session.md`, decision D3): an `id_token_hint` would
   put the ID token in a browser-visible `Location` header, breaking the
   issue's Done-when and NFR-21 (zero token values in any browser-visible
   response). The redirect instead carries only `client_id` and
   `post_logout_redirect_uri`. The accepted consequence is that Keycloak 26
   then shows its own logout-confirmation page (one extra click); the BFF's
   ticket and cookie are already cleared by that point, so this does not
   change Story 6's other scenarios.

<!-- gate: G1 | verdict: PASS | issue: #18 -->

## Traceability

Test names below are `ClassName.MethodName` in the project named in the Lane
column; namespaces match the project (`Decisya.Bff.Tests`, `Decisya.Identity.Tests`,
`Decisya.Api.Tests.Architecture`, `Decisya.ServiceDefaults.Tests.Architecture`).
Coverage is one of **Direct** (the test drives exactly the scenario's given/when/then),
**Indirect** (the test proves the same code contract through a related but not
identical precondition), or **Gap → backlog #83** (no automated test yet; the
column names the test that would close it). Three scenarios that had no test and
would have been a few lines were written now (marked **NEW**) rather than left as
gaps: `OidcChallengeShapeTests`, `BffMeEndpointTests` (3 tests), `TicketProtectionTests`,
`SessionRestartTests`, and `LogoutTests`.

### Story 1 — OIDC login sets a secure session cookie; the browser never receives a token

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| A tenant user completes login and receives a correctly-flagged session cookie (Done-when) | `BffLoginFlowTests.Every_set_cookie_in_the_flow_has_the_required_attributes` (redirect + cookie flags); `TokenLeakScanTests.No_token_value_appears_in_any_response_across_the_flow` (no token anywhere) | Decisya.Bff.Tests, Integration | Direct |
| The OIDC correlation and nonce cookies use SameSite=Lax | `BffLoginFlowTests.Every_set_cookie_in_the_flow_has_the_required_attributes` (same test; asserts the correlation/nonce `Set-Cookie` headers) | Decisya.Bff.Tests, Integration | Direct |
| The BFF uses authorization code flow with PKCE S256 | `OidcChallengeShapeTests.The_OIDC_handler_is_configured_for_authorization_code_flow_with_PKCE` (**NEW**: asserts `ResponseType=code`, `ResponseMode=query`, `UsePkce=true` — the ASP.NET Core OIDC handler implements only S256, so `UsePkce=true` *is* `code_challenge_method=S256`); the "verifier kept server-side" half is proven Indirectly by `BffLoginFlowTests`' correlation-cookie `HttpOnly`/`Secure` assertions (the verifier travels only inside that protected cookie, never as a query parameter or in a response body) | Decisya.Bff.Tests, Unit (new test) / Integration (indirect half) | Direct + Indirect |
| A tampered callback is rejected (modified `state`) | — | — | **Gap → backlog #83**. The test: extend `LoginFlowHarness` with a step that mutates the `state` query value on a genuine Keycloak callback URL before presenting it to `/signin-oidc`, then assert no `__Host-decisya-session` cookie is set and `/bff/me` still reports `isAuthenticated:false`. Not added now: needs a new harness branch and confirming the BFF's exact `RemoteFailure` response shape, more than a few lines. |

### Story 2 — Session tickets live server-side in Redis, protected with Data Protection

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| The session cookie carries no token; the ticket lives in Redis | `TokenLeakScanTests.No_token_value_appears_in_any_response_across_the_flow` (retrieves the ticket via `RedisTicketStore.RetrieveAsync`, scans every response) | Decisya.Bff.Tests, Integration | Direct |
| The stored ticket is protected, not plaintext | `TicketProtectionTests.The_stored_ticket_is_protected_not_plaintext` (**NEW**: reads the raw bytes Redis holds for the ticket key directly, bypassing the protector, and asserts the access/ID token values are absent) | Decisya.Bff.Tests, Integration | Direct |
| A session survives a BFF process restart | `SessionRestartTests.A_session_survives_a_BFF_process_restart` (**NEW**: disposes the first `BffWebApplicationFactory`, builds a second one on the same on-disk key-ring directory and the same Redis container, presents the old cookie) | Decisya.Bff.Tests, Integration | Direct |
| (supporting) Redis is wired into the AppHost as a third persistent, fixed-name container | `AppHostConfigurationTests.AppHost_cs_marks_postgres_keycloak_and_redis_persistent_with_fixed_names_and_a_test_time_override` | Decisya.ServiceDefaults.Tests.Architecture, Unit | Direct (infrastructure prerequisite, not a Story scenario itself) |

### Story 3 — A Redis outage degrades sessions safely, without leaking detail or crashing

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| Login fails closed when Redis is unreachable | — | — | **Gap → backlog #83**. |
| An existing session is treated as unauthenticated when Redis becomes unreachable | `RedisFailureTests.A_corrupted_ticket_entry_reports_unauthenticated_not_a_server_error` | Decisya.Bff.Tests, Integration | Indirect — exercises the ticket store's *unprotect-failure* fail-closed branch (`IsUnprotectFailure`), not the *connection-unreachable* branch (`IsTransientStoreFailure`); same fail-closed contract, different trigger. |
| Session validation fails fast, not slow (< 2 s) | — | — | **Gap → backlog #83**. |

**Backlog #83 test for all three rows above:** a dedicated, non-shared Redis
endpoint (e.g. a closed TCP port in `ConnectionStrings:redis`, not the shared
`RedisFixture` container — stopping that assembly-wide fixture mid-suite would
break every later Integration test since parallelism is off) that deterministically
throws `RedisException`/`TimeoutException`. One test drives a login callback
against it and asserts a generic error, no stack trace in the body, and a
trace id in the log; a second calls `/bff/me` with a valid cookie against it
and asserts a response within 2 s. `RedisFixture.StopAsync` already exists for
this (doc comment: "Story 3's outage scenario") but is never called by any
test today — this is the gap.

### Story 4 — State-changing BFF endpoints require a valid antiforgery token

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| A state-changing request without an antiforgery token is rejected | `AntiforgeryTests.A_state_changing_request_with_no_antiforgery_header_is_rejected` | Decisya.Bff.Tests, Integration | Direct |
| A state-changing request with a mismatched antiforgery token is rejected | `AntiforgeryTests.A_state_changing_request_with_a_mismatched_header_is_rejected`; also `AntiforgeryTests.Another_users_antiforgery_pair_presented_with_this_session_is_rejected` and `AntiforgeryTests.A_pair_issued_while_anonymous_is_rejected_once_presented_after_login` (extra cases beyond the Gherkin wording, same scenario family) | Decisya.Bff.Tests, Integration | Direct |
| A state-changing request with a valid antiforgery token pair succeeds | `AntiforgeryTests.A_valid_antiforgery_pair_is_accepted` | Decisya.Bff.Tests, Integration | Direct |
| A safe, read-only request needs no antiforgery token | `AntiforgeryTests.A_safe_GET_request_needs_no_antiforgery_header` | Decisya.Bff.Tests, Integration | Direct |
| (supporting) every non-GET `/bff` endpoint carries the antiforgery filter except the one opted-out endpoint | `AntiforgeryFilterMetadataTests.Every_non_GET_bff_endpoint_requires_antiforgery_except_the_one_opted_out_backchannel_logout_endpoint` | Decisya.Bff.Tests, Unit | Direct (structural, not a Story scenario itself) |

### Story 5 — A token-free user-info endpoint for the SPA

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| An authenticated user gets her identity claims | `BffMeEndpointTests.An_authenticated_user_gets_her_identity_claims` (**NEW**) | Decisya.Bff.Tests, Integration | Direct |
| An anonymous caller gets a non-error "not signed in" response | `BffMeEndpointTests.An_anonymous_caller_gets_a_non_error_not_signed_in_response` (**NEW**) | Decisya.Bff.Tests, Integration | Direct |
| A platform-admin user's response carries no tenant id | `BffMeEndpointTests.A_platform_admins_response_carries_no_tenant_id` (**NEW**) | Decisya.Bff.Tests, Integration | Direct |
| (supporting) the realm's client-level mapper puts realm roles in the ID token, which `/bff/me` reads | `RealmConfigurationTests.The_realm_roles_id_token_mapper_puts_roles_in_the_ID_token_only` | Decisya.Identity.Tests, Integration | Direct (realm prerequisite, not the BFF endpoint itself) |

Isolation-test skill: not applicable. `/bff/me` reads claims off the caller's
own ID token; #18 introduces no persisted aggregate and no cross-tenant query,
so there is no read/update-by-id pair to write per the skill's template. The
realm-side guarantee that two seeded tenant users carry distinct `tenant_id`
values is `RealmConfigurationTests.At_least_two_enabled_tenant_users_have_distinct_tenant_ids_and_one_platform_admin_has_none`
(already existed, issue #17).

### Story 6 — RP-initiated sign-out ends the session at the browser, the BFF and Keycloak

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| A tenant user signs out (cookie cleared, ticket deleted, redirect with `client_id` + `post_logout_redirect_uri`, no `id_token_hint`, no token value anywhere) | `BffLoginFlowTests.Every_set_cookie_in_the_flow_has_the_required_attributes` (cleared cookie, redirect shape, no `id_token_hint`); `TokenLeakScanTests.No_token_value_appears_in_any_response_across_the_flow` (logout response scanned for token values); `LogoutTests.Signing_out_deletes_the_ticket_from_Redis_and_the_old_cookie_no_longer_works` (**NEW**: ticket-deletion clause) | Decisya.Bff.Tests, Integration | Direct |
| Keycloak completes the logout redirect | — | — | **Manual.** Per G2 decision D3, the end-session redirect carries no `id_token_hint`, so Keycloak 26 shows its own logout-confirmation page (one extra click) before redirecting back — the accepted consequence recorded in the G1 amendment and `docs/architecture/bff-session.md`. Automating the click-through needs a new helper (parse and submit Keycloak's confirmation form, the same shape `KeycloakFormHelper` already does for the login form) — a **Gap → backlog #83** for full automation; verified today by Marco's manual browser check (G4 evidence, "log in ... and log out"). |
| A cookie from before sign-out no longer works | `LogoutTests.Signing_out_deletes_the_ticket_from_Redis_and_the_old_cookie_no_longer_works` (**NEW**) | Decisya.Bff.Tests, Integration | Direct |
| Sign-out itself requires a valid antiforgery token | `AntiforgeryTests.A_state_changing_request_with_no_antiforgery_header_is_rejected` (Story 4's scenario 1 is literally this scenario against `/bff/logout`, the only state-changing endpoint #18 ships) | Decisya.Bff.Tests, Integration | Direct |

### Story 7 — Back-channel logout invalidates the local ticket without browser interaction

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| Keycloak's back-channel logout deletes the local ticket; a later cookie presentation is unauthenticated | `BackchannelLogoutTests.A_valid_logout_token_deletes_every_ticket_under_its_sid_and_replay_deletes_nothing_further` (first half: deletes the ticket, confirmed via `RedisTicketStore.RetrieveAsync` — the exact check `/bff/me`'s own authentication depends on) | Decisya.Bff.Tests, Integration | Direct |
| An invalid logout token is rejected (bad signature, wrong issuer, wrong audience, `alg=none`, HMAC alg, missing/extra claims, expired, not-yet-valid) | `BackchannelLogoutTests.Invalid_logout_token_is_rejected_and_deletes_nothing` (`Theory`, 10 cases) | Decisya.Bff.Tests, Integration | Direct — this is also CLAUDE.md's negative-auth-test set (expired token, wrong audience, `alg=none`) for this issue's one auth change beyond antiforgery. |
| A replayed logout token has no further effect | `BackchannelLogoutTests.A_valid_logout_token_deletes_every_ticket_under_its_sid_and_replay_deletes_nothing_further` (second half: replay still 200, deletes nothing further) | Decisya.Bff.Tests, Integration | Direct |
| (supporting) the realm's `decisya-bff` client carries `backchannel.logout.url` and `frontchannelLogout: false` | `RealmConfigurationTests.The_decisya_bff_client_has_a_backchannel_logout_url_and_no_frontchannel_logout` | Decisya.Identity.Tests, Integration | Direct (realm prerequisite) |
| (supporting) a live Keycloak actually reaching the BFF's `/bff/backchannel-logout` over `host.docker.internal:7200` with a trusted dev certificate | — | — | **Manual** (S-6, not taken; G4 evidence and G1 decisions record this as Marco's optional host check: end a session in the Keycloak admin console and confirm `/bff/me` flips to `isAuthenticated:false`). `BackchannelLogoutTests` itself never depends on this path — it posts a self-built, validly-signed logout token straight to the endpoint (G2's stop-and-record rule). |

### NFR-20 to NFR-23

| NFR | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| NFR-20 (cookie flags, 100% of responses that set them) | `BffLoginFlowTests.Every_set_cookie_in_the_flow_has_the_required_attributes` | Decisya.Bff.Tests, Integration | Direct |
| NFR-21 (zero token values in any browser-visible response) | `TokenLeakScanTests.No_token_value_appears_in_any_response_across_the_flow` | Decisya.Bff.Tests, Integration | Direct |
| NFR-22 (fail closed within 2 s on a Redis outage) | `RedisFailureTests.A_corrupted_ticket_entry_reports_unauthenticated_not_a_server_error` | Decisya.Bff.Tests, Integration | Indirect (see Story 3 above — the corrupted-entry branch only; the connection-unreachable branch and the 2 s timing assertion are the same **Gap → backlog #83**) |
| NFR-23 (Data Protection key ring survives a restart) | `SessionRestartTests.A_session_survives_a_BFF_process_restart` (**NEW**) | Decisya.Bff.Tests, Integration | Direct |

### Other #18 evidence not tied to a single scenario

`ApiBoundaryTests.No_type_depends_on_Decisya_Bff` (Decisya.Api.Tests.Architecture,
Unit) is the reverse-boundary test test-engineer added at G4 ("Api, ServiceDefaults
and SharedKernel don't depend on the BFF") — an architecture invariant from G2/G3,
not a Story scenario; recorded here for completeness, Direct.

### Test run (2026-09-27, Docker running)

`dotnet test --project tests/Decisya.Bff.Tests`: **57 passed**, 0 failed, 0 skipped
(30 unit, 27 integration; up from 29 unit / 21 integration at G4 — six tests
added at this gate: `OidcChallengeShapeTests` ×1, `BffMeEndpointTests` ×3,
`TicketProtectionTests` ×1, `SessionRestartTests` ×1, `LogoutTests` ×1).
`dotnet build -warnaserror`: 0 warnings, 0 errors, whole solution.

### Verdict

Every Story 1–7 scenario and every NFR-20 to NFR-23 row above carries either a
passing test or an explicit reason (Indirect, Manual, or Gap → backlog #83).
Four residual gaps are recorded above, each with the test that would close it,
per the issue's proportionality rule (small enough gaps were written now; the
four left are bigger than "a few lines" and go to backlog #83): the tampered-callback
scenario (Story 1), the two literal Redis-unreachable scenarios plus NFR-22's
untested connection-failure branch (Story 3), and full automation of Keycloak's
logout-confirmation click-through (Story 6 scenario 2, currently a manual check).

<!-- gate: G5 | verdict: PASS | issue: #18 -->
