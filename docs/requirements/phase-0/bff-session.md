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
