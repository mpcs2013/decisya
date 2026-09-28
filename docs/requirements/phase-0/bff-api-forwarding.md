# Phase 0 – BFF: `/api` forwarding with server-side token injection + refresh

## Issue 0.07 (#19) — YARP `/api` forwarding, token injection, transparent refresh

### Scope note (role framing)

Every story below is written **"As a tenant user"**, the same framing #18 adopted
(`docs/requirements/phase-0/bff-session.md`): forwarding a call, refreshing a
token, and ending a Keycloak session at logout are each directly observable by
the person sitting at the browser (a call that used to work keeps working
across an expiry; a call after a revoked session gets a clean sign-in prompt
instead of a silent failure), even though the mechanism (YARP, the refresh
grant, the per-session lock) is infrastructure.

**Entitlement plan:** N/A for every story in this issue, for the same reason
as #18: forwarding an authenticated call to the platform's own API, refreshing
the session that carries it, and ending that session cleanly are core platform
infrastructure identical on every subscription plan. Nothing here is gated by
an entitlement feature key (`IEntitlementService`, ADR-0008, does not exist
yet in phase 0).

**Scope, tightly bound to issue #19 (per the manifest and ADR-0003), carrying
in three items flagged at #18's G3 (`docs/security/threat-models/bff-session.md`,
"#19 boundary"):**

- YARP forwards `/api/*` to `Decisya.Api`, attaching the caller's Keycloak
  access token server-side on every forwarded request. The browser's session
  cookie is never forwarded to `Decisya.Api`, and no access, ID or refresh
  token value ever reaches the browser.
- Transparent refresh of an access token that has expired, or is close enough
  to expiring that forwarding it would risk the call failing in flight,
  using the refresh token already held in the session's ticket (#18 stores it;
  #18 does not yet read it). Exactly one refresh happens per session even when
  several requests race against the same expiry (the per-session lock ADR-0003
  names as a consequence of hand-rolling the BFF).
- **B-1** (carried in from #18's G3, T-13): a refresh that Keycloak rejects
  ends the BFF session at once — the ticket is deleted — so the *next* call
  gets 401, never a call forwarded with a stale or invalid token.
- **B-2** (carried in from #18's G3, T-12): signing out ends the session at
  Keycloak server-side, using the refresh token the BFF already holds, closing
  the residual left by #18's D3 (no `id_token_hint` in the end-session
  redirect). The end-session redirect stays as a fallback.
- **B-3** (carried in from #18's G3): the same `AntiforgeryFilter` #18 applies
  to `/bff` covers state-changing `/api` calls too.
- An anonymous call to `/api` gets 401, never a redirect to Keycloak's login
  page — an API surface must fail an unauthenticated caller predictably, not
  hand it an HTML login page.

**Out of scope, with the owning issue:**

- **JWT validation inside `Decisya.Api`** (`iss`, `aud`, `exp`, the RS256/ES256
  algorithm allow-list) — #20. **What #19 assumes about it:** today,
  `src/Decisya.Api/Program.cs` is the #15 skeleton — `AddServiceDefaults()` and
  the health endpoints only, no authentication middleware, no business
  endpoint (its own comment names #20 as the issue that adds both). #19
  therefore cannot prove anything about how `Decisya.Api` *reacts* to the
  token it receives, only about what the BFF *attaches* to the forwarded
  request: the header, its value, and what is absent from it (a cookie, a
  client-supplied override). Every scenario below is written so a test can
  prove it by inspecting the request YARP produces and the response the BFF
  returns to the browser, without `Decisya.Api` validating, or even parsing,
  the token. Architecture (G2) picks the concrete mechanism a test uses to
  observe the forwarded request (for example, a minimal echo/probe endpoint,
  or a test double standing in for `Decisya.Api`); #19 does not require
  `Decisya.Api` to gain a real business endpoint.
- **The SPA** — no SPA project exists yet in this phase; #19's acceptance
  criteria are proven the same way #17/#18's were, against an
  `HttpClient`-driven test harness, not a real browser.
- **Rate limits** — a later issue.
- **CORS** — unchanged from #18: the SPA and the BFF share one origin
  (ADR-0003); `/api/*` is no exception.

---

### Story 1 — A tenant user's `/api` call is forwarded with her access token attached server-side; her cookie never leaves the BFF (the Done-when's supporting story)

As a tenant user, I want my `/api` calls proxied to Decisya.Api with my access
token attached automatically by the BFF, so that I can use backend APIs
without my browser ever handling a token, and without my session cookie being
exposed to a service other than the BFF itself.

#### Acceptance criteria

```gherkin
Feature: /api/* forwarding attaches the access token server-side and never forwards the session cookie

  Scenario: An authenticated tenant user's /api call reaches Decisya.Api with her access token attached
    Given "dev-alice" has completed login and holds a valid session cookie
    When she sends a GET request to an /api endpoint with that cookie and no Authorization header
    Then the BFF forwards the request to Decisya.Api
    And the forwarded request carries an Authorization header of the form "Bearer <token>"
    And that token is the access token from dev-alice's session
    And the forwarded request carries no Cookie header

  Scenario: The token never reaches the browser
    Given "dev-alice" has completed login and holds a valid session cookie
    When she sends a request to an /api endpoint with that cookie
    Then the response returned to her browser contains no access token, ID token or refresh token value, in its body or in any header

  Scenario: A client-supplied Authorization header is not trusted
    Given "dev-alice" holds a valid session cookie
    When she sends a request to an /api endpoint with that cookie and an Authorization header value of her own choosing
    Then the request forwarded to Decisya.Api carries the BFF's own attached access token, not the value she supplied
```

---

### Story 2 — An expired or soon-to-expire access token is refreshed transparently before the call is forwarded (the Done-when)

As a tenant user, I want an access token that has expired, or is close to
expiring, to be refreshed automatically before my `/api` call is forwarded, so
that my request succeeds without me noticing and without having to sign in
again.

#### Acceptance criteria

```gherkin
Feature: An expiring or expired access token is refreshed transparently before forwarding

  Scenario: An expired access token is refreshed before the call is forwarded (Done-when)
    Given "dev-alice" holds a valid session whose access token has already expired and whose refresh token is still valid at Keycloak
    When she sends a request to an /api endpoint with her session cookie
    Then the BFF exchanges the refresh token for a new access token before forwarding the request
    And the forwarded request carries the new, unexpired access token
    And the call succeeds
    And dev-alice's session ticket in the Redis-backed ticket store now holds the new access and refresh tokens in place of the old ones

  Scenario: A token nearing expiry is refreshed proactively, without waiting for it to expire
    Given "dev-alice" holds a valid session whose access token has less than the refresh lead time (NFR-24) remaining before expiry
    When she sends a request to an /api endpoint with her session cookie
    Then the BFF refreshes the token before forwarding the request

  Scenario: A token with plenty of remaining lifetime is forwarded unchanged
    Given "dev-alice" holds a valid session whose access token has more than the refresh lead time (NFR-24) remaining before expiry
    When she sends a request to an /api endpoint with her session cookie
    Then the BFF forwards the request using the existing access token
    And no call is made to Keycloak's token endpoint
```

---

### Story 3 — Concurrent calls against an expired token share exactly one refresh

As a tenant user, I want only one token refresh to happen even when several of
my requests race against the same expired token at once, so that Keycloak's
refresh-token rotation doesn't invalidate my session through a race between
simultaneous refresh attempts.

#### Acceptance criteria

```gherkin
Feature: Concurrent requests against an expired or expiring token trigger exactly one refresh per session

  Scenario: Ten concurrent /api requests against an expired token share one refresh
    Given "dev-alice" holds a valid session whose access token has expired
    When 10 requests to /api endpoints are sent concurrently, all carrying her session cookie
    Then exactly one request reaches Keycloak's token endpoint to refresh the token
    And all 10 requests are forwarded to Decisya.Api carrying the same new access token
    And all 10 requests succeed
```

---

### Story 4 — A refresh Keycloak rejects ends the session at once, not on the next call (B-1)

As a tenant user, I want a refresh attempt that Keycloak rejects to end my BFF
session immediately, so that the very next request I make gets a clean
sign-in prompt instead of the BFF forwarding a stale or invalid token, or
silently retrying forever.

#### Acceptance criteria

```gherkin
Feature: A failed refresh ends the BFF session at once (B-1)

  Scenario: Keycloak rejects the refresh
    Given "dev-alice" holds a session whose access token has expired and whose refresh token Keycloak will reject (invalid_grant: revoked, expired, or the Keycloak session itself has ended)
    When she sends a request to an /api endpoint with her session cookie
    Then the BFF deletes her session's ticket from the Redis-backed ticket store
    And the response to that request is 401
    And no request is forwarded to Decisya.Api

  Scenario: The next call after a failed refresh gets 401, not a stale token
    Given dev-alice's refresh has just failed as in the previous scenario
    When she sends another request to an /api endpoint with the same, now-deleted session cookie
    Then the response is 401
    And no request is forwarded to Decisya.Api
```

---

### Story 5 — Signing out ends the session at Keycloak server-side, using the refresh token the BFF already holds (B-2)

As a tenant user, I want signing out of Decisya to end my session at Keycloak
itself, using the refresh token the BFF already holds for me, so that a
refresh token captured earlier cannot be used afterward to mint new access
tokens once I've deliberately logged out — closing the gap #18 left open
(its end-session redirect alone cannot guarantee this, per D3).

#### Acceptance criteria

```gherkin
Feature: Sign-out ends the session at Keycloak server-side (B-2)

  Scenario: Logout revokes the session at Keycloak before clearing the local session
    Given "dev-alice" holds a valid session cookie and a matching antiforgery token pair
    When she calls the BFF's sign-out endpoint
    Then the BFF calls Keycloak, using her session's refresh token, to end the Keycloak session server-side
    And it then clears her session cookie and deletes her ticket from the Redis-backed ticket store
    And the previously valid refresh token can no longer be exchanged for a new access token at Keycloak
    And the browser is still redirected towards Keycloak's end-session endpoint, as in #18's Story 6, as a fallback

  Scenario: Logout still succeeds locally even when Keycloak's revocation call itself fails
    Given "dev-alice" holds a valid session cookie and a matching antiforgery token pair
    And Keycloak's revocation endpoint is unreachable
    When she calls the BFF's sign-out endpoint
    Then the BFF still clears her session cookie and still deletes her ticket from the Redis-backed ticket store
    And the failure to reach Keycloak is written to the structured log with a trace id, not surfaced to the browser as an error
```

---

### Story 6 — State-changing `/api` calls require the same antiforgery protection as `/bff` (B-3)

As a tenant user, I want a state-changing `/api` call to be rejected unless it
carries a valid antiforgery token, exactly as `/bff`'s own state-changing
endpoints already are, so that a malicious website can't use my browser's
ambient session cookie to make Decisya.Api act on my data (CSRF).

#### Acceptance criteria

```gherkin
Feature: State-changing /api calls require a valid antiforgery token (B-3)

  Scenario: A state-changing /api request without an antiforgery token is rejected
    Given "dev-alice" holds a valid session cookie
    When a POST request to a state-changing /api endpoint is sent with the session cookie but with no antiforgery header
    Then the BFF rejects it with 403
    And no request is forwarded to Decisya.Api

  Scenario: A state-changing /api request with a mismatched antiforgery token is rejected
    Given "dev-alice" holds a valid session cookie and a valid antiforgery cookie
    When a POST request to a state-changing /api endpoint is sent with the session cookie and an antiforgery header value that does not match the antiforgery cookie
    Then the BFF rejects it with 403
    And no request is forwarded to Decisya.Api

  Scenario: A state-changing /api request with a valid antiforgery token pair is forwarded
    Given "dev-alice" holds a valid session cookie and has fetched the matching antiforgery cookie and header value from the BFF
    When a POST request to a state-changing /api endpoint is sent with both
    Then the BFF forwards the request to Decisya.Api

  Scenario: A safe, read-only /api request needs no antiforgery token
    Given "dev-alice" holds a valid session cookie
    When a GET request to an /api endpoint is sent with no antiforgery header
    Then the BFF forwards the request to Decisya.Api
```

---

### Story 7 — An anonymous `/api` call is rejected outright, never redirected to a login page

As a tenant user — and, equivalently, any anonymous caller, since an API
consumer is never a browser rendering a login page — I want an unauthenticated
`/api` call to get a plain 401, so that an API client fails predictably
instead of receiving a Keycloak login page where it expected a JSON error.

#### Acceptance criteria

```gherkin
Feature: Anonymous /api calls are rejected with 401, never redirected to login

  Scenario: An anonymous call to a read-only /api endpoint gets 401, not a redirect
    Given no session cookie is presented
    When a GET request is sent to an /api endpoint
    Then the response is 401
    And the response is not a 302 (or any other) redirect
    And no request is forwarded to Decisya.Api

  Scenario: An anonymous call to a state-changing /api endpoint also gets 401, not 403
    Given no session cookie is presented
    When a POST request is sent to a state-changing /api endpoint with no antiforgery header
    Then the response is 401
    And no request is forwarded to Decisya.Api
```

---

## Open questions for Marco — answered

Both carry a recommended answer, adopted below so G2 has a concrete value and
rule to build against. Flag either one at any later gate if it should be
revisited.

1. **What is the refresh lead time — the remaining-lifetime threshold below
   which the BFF refreshes proactively instead of waiting for outright
   expiry? — Answered: 30 seconds.**
   - **Answer:** 30 s, 10% of the ≤ 300 s (5 min) maximum access-token
     lifespan NFR-15 already fixes for the `decisya-bff` client. Recorded as
     NFR-24 below.
   - Rationale: too short a lead time (or none at all — refresh only after
     outright expiry) risks a token expiring in the round trip between the
     BFF's freshness check and `Decisya.Api` actually processing the
     forwarded request, especially once #20 adds real validation there. Too
     long a lead time (say, half the token's life) would refresh far more
     often than needed, adding load on Keycloak's token endpoint for no
     benefit.
   - Rejected alternative: refresh strictly on expiry (lead time zero) — the
     simplest rule, but reintroduces exactly the in-flight-expiry race the
     lead time exists to avoid, and #20's forthcoming `exp` check would then
     surface it as an intermittent 401 that has nothing to do with the
     caller's own session.

2. **When Keycloak's server-side revocation call at logout (Story 5, B-2)
   itself fails — Keycloak unreachable, or it rejects the already-expired
   refresh token — does the BFF still complete the local logout (clear the
   cookie, delete the ticket), or does it report an error to the browser
   instead? — Answered: the local logout still completes.**
   - **Answer:** Story 5's second scenario. The BFF always clears the cookie
     and deletes the ticket; a revocation failure is logged with a trace id,
     never surfaced to the browser as an error.
   - Rationale: the user's own ability to end her own local session must not
     depend on Keycloak's availability — #18's Story 3 already establishes
     that a Redis or IdP-side problem degrades safely rather than leaving the
     tenant user stuck. The residual (a Keycloak session outliving a logout
     during an outage) is bounded by Keycloak's own SSO idle timeout (1800 s),
     the same bound #18's G3 already accepted for T-12.
   - Rejected alternative: block the local logout on a successful revocation
     — turns a Keycloak-side or network hiccup into a tenant user unable to
     sign out of her own browser, a worse outcome than the bounded residual
     above.

## NFRs added

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-24 | Reliability | The BFF begins refreshing a session's access token once its remaining lifetime is ≤ 30 s (10% of the ≤ 300 s maximum lifespan, NFR-15), so a request forwarded to Decisya.Api is never rejected for carrying a token that expired in flight | `Decisya.Bff.Tests` integration test (Testcontainers Keycloak + Redis, `Category=Integration`) asserting a refresh is triggered before expiry and not before the lead-time threshold (0.07) |
| NFR-25 | Reliability | Under ≥ 10 concurrent `/api` requests against a session whose access token needs refresh, exactly one refresh call reaches Keycloak's token endpoint per session, and 100% of the concurrent requests complete successfully using the resulting token (zero `invalid_grant` failures caused by a refresh racing itself under Keycloak's refresh-token rotation) | `Decisya.Bff.Tests` integration test issuing concurrent requests against a session pinned near/at expiry (`Category=Integration`) (0.07) |

These two rows should be appended to `docs/requirements/nfr.md`.

## Traceability

Test names below are `ClassName.MethodName` in the project named in the Lane
column; namespaces match the project (`Decisya.Bff.Tests`,
`Decisya.ServiceDefaults.Tests.Architecture`). Coverage is one of **Direct**
(the test drives exactly the scenario's given/when/then), **Indirect** (the
test proves the same code contract through a related but not identical
precondition or a static/infrastructure check), or **Manual** (no automated
test, with a reason). Two gaps G4 left for this gate — the AppHost's
`.WithReference(api)` wiring assertion and the automated log scan (G3 MUST
G4-19-05) — are closed here (marked **NEW**), each a few lines added to an
existing fixture/factory rather than a new harness, per the issue's
proportionality rule. No scenario below needed a gap to backlog #83: G4 left
`Decisya.Bff.Tests` Integration at 50/50 green, and every Gherkin scenario
already had a passing Direct test.

### Story 1 — `/api/*` forwarding attaches the access token server-side and never forwards the session cookie

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| An authenticated tenant user's `/api` call reaches Decisya.Api with her access token attached (the Done-when's supporting story) | `ApiForwardingTests.Forwarded_request_carries_only_the_bffs_bearer` (the forwarded `Authorization: Bearer <token>` equals the session's own access token; no `Cookie` header is forwarded) | Decisya.Bff.Tests, Integration | Direct — against `ApiDouble`, the test double standing in for `Decisya.Api` per this doc's own scope note (`Decisya.Api` has no business endpoint until #20) |
| The token never reaches the browser | `TokenLeakScanTests.No_token_value_appears_in_any_api_response_across_forward_refresh_401_403_or_503` (the forwarded/refreshed/403/401 hops); `TokenLeakScanTests.No_token_value_appears_in_any_response_across_the_flow` (`/bff/me`, `/bff/logout`) | Decisya.Bff.Tests, Integration | Direct |
| A client-supplied Authorization header is not trusted | `ApiForwardingTests.Forwarded_request_carries_only_the_bffs_bearer` (same test: sends `Authorization: Bearer attacker-supplied-value`, asserts the forwarded header is the BFF's own token, not that value) | Decisya.Bff.Tests, Integration | Direct |
| (supporting) `decisya-bff` resolves `https://decisya-api` through the AppHost's service discovery | `AppHostConfigurationTests.AppHost_cs_passes_secrets_only_through_parameters` (**NEW** assertion: `decisya-bff`'s `.WithReference(api)`, per G2's AppHost section) | Decisya.ServiceDefaults.Tests.Architecture, Unit | Indirect — a static check of the production wiring `ApiDouble` stands in for above; #19 does not stand up a real two-project Aspire run (out of scope: no SPA yet, #20 adds real JWT validation in `Decisya.Api`) |

### Story 2 — An expiring or expired access token is refreshed transparently before forwarding (the Done-when)

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| An expired access token is refreshed before the call is forwarded (Done-when) | `TokenRefreshTests.Expired_access_token_is_refreshed_before_the_call_is_forwarded` | Decisya.Bff.Tests, Integration | Direct |
| A token nearing expiry is refreshed proactively, without waiting for it to expire | `TokenRefreshTests.A_token_nearing_expiry_is_refreshed_proactively_without_waiting_for_outright_expiry` (29 s remaining, below NFR-24's 30 s lead time) | Decisya.Bff.Tests, Integration | Direct |
| A token with plenty of remaining lifetime is forwarded unchanged | `TokenRefreshTests.A_token_with_plenty_of_remaining_lifetime_is_forwarded_unchanged_with_no_refresh_call` (31 s remaining, above the lead time; asserts zero calls to Keycloak's token endpoint) | Decisya.Bff.Tests, Integration | Direct |

### Story 3 — Concurrent calls against an expired token share exactly one refresh

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| Ten concurrent `/api` requests against an expired token share one refresh | `TokenRefreshTests.Ten_concurrent_requests_against_an_expired_token_share_exactly_one_refresh` | Decisya.Bff.Tests, Integration | Direct |

### Story 4 — A refresh Keycloak rejects ends the session at once, not on the next call (B-1)

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| Keycloak rejects the refresh | `TokenRefreshTests.A_refresh_Keycloak_rejects_ends_the_session_at_once_and_the_next_call_gets_401` (first half: 401, ticket deleted from Redis, nothing forwarded) | Decisya.Bff.Tests, Integration | Direct |
| The next call after a failed refresh gets 401, not a stale token | same test (second half: a second call with the now-deleted cookie also gets 401, nothing forwarded) | Decisya.Bff.Tests, Integration | Direct |

### Story 5 — Signing out ends the session at Keycloak server-side, using the refresh token the BFF already holds (B-2)

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| Logout revokes the session at Keycloak before clearing the local session | `LogoutTests.Logout_ends_the_session_at_Keycloak_so_the_pre_logout_refresh_token_no_longer_works` (the end-session call is made; the pre-logout refresh token is then rejected with `invalid_grant` at Keycloak, proving it was the session revoked); the fallback redirect shape (`client_id`, no `id_token_hint`) is #18's own `BffLoginFlowTests.Every_set_cookie_in_the_flow_has_the_required_attributes`, unchanged by #19 | Decisya.Bff.Tests, Integration | Direct |
| Logout still succeeds locally even when Keycloak's revocation call itself fails | `LogoutTests.Logout_still_completes_locally_even_when_Keycloaks_revocation_call_fails` (cookie cleared, ticket deleted, 302 to the browser, nothing surfaced as an error); "written to the structured log with a trace id" is proven generically by `DecisyaJsonConsoleFormatterTests` (every JSON log line carries `trace_id` inside an active trace — `BffLog.KeycloakLogoutFailed` renders through the same formatter), and `LogScanTests` (**NEW**, below) proves the message itself carries no sensitive value | Decisya.Bff.Tests, Integration; Decisya.ServiceDefaults.Tests.Logging, Unit | Direct + Indirect |

### Story 6 — State-changing `/api` calls require the same antiforgery protection as `/bff` (B-3)

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| A state-changing `/api` request without an antiforgery token is rejected | `ApiAntiforgeryTests.A_state_changing_api_request_without_an_antiforgery_header_is_rejected` | Decisya.Bff.Tests, Integration | Direct |
| A state-changing `/api` request with a mismatched antiforgery token is rejected | `ApiAntiforgeryTests.A_state_changing_api_request_with_a_mismatched_antiforgery_header_is_rejected` | Decisya.Bff.Tests, Integration | Direct |
| A state-changing `/api` request with a valid antiforgery token pair is forwarded | `ApiAntiforgeryTests.A_state_changing_api_request_with_a_valid_antiforgery_pair_is_forwarded` | Decisya.Bff.Tests, Integration | Direct |
| A safe, read-only `/api` request needs no antiforgery token | `ApiForwardingTests.A_GET_request_needs_no_antiforgery_header_and_is_forwarded` | Decisya.Bff.Tests, Integration | Direct |

### Story 7 — An anonymous `/api` call is rejected outright, never redirected to a login page

| Scenario | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| An anonymous call to a read-only `/api` endpoint gets 401, not a redirect | `ApiAntiforgeryTests.An_anonymous_GET_to_api_gets_401_never_a_redirect` (also asserts the status is never 302) | Decisya.Bff.Tests, Integration | Direct |
| An anonymous call to a state-changing `/api` endpoint also gets 401, not 403 | `ApiAntiforgeryTests.An_anonymous_state_changing_api_request_gets_401_not_403` | Decisya.Bff.Tests, Integration | Direct |

### NFRs added (NFR-24, NFR-25)

| NFR | Test(s) | Lane | Coverage |
| --- | --- | --- | --- |
| NFR-24 (refresh begins once remaining lifetime is ≤ 30 s, never before) | `TokenRefreshTests.A_token_nearing_expiry_is_refreshed_proactively_without_waiting_for_outright_expiry` (29 s: refreshes); `TokenRefreshTests.A_token_with_plenty_of_remaining_lifetime_is_forwarded_unchanged_with_no_refresh_call` (31 s: does not) | Decisya.Bff.Tests, Integration | Direct |
| NFR-25 (exactly one refresh call under ≥ 10 concurrent requests; 100% succeed) | `TokenRefreshTests.Ten_concurrent_requests_against_an_expired_token_share_exactly_one_refresh` | Decisya.Bff.Tests, Integration | Direct |

### Other #19 evidence not tied to a single Gherkin scenario

G2/G3 hardening beyond the stories above, all already green at G4:

- `ApiForwardingTests.The_forwarded_path_always_starts_with_api_and_the_client_supplied_Host_is_not_used_to_steer_the_destination` — the client-supplied `Host` header cannot steer the forwarding destination.
- `ApiAntiforgeryTests.A_verb_outside_the_routes_allow_list_is_not_forwarded` — the route only matches GET/HEAD/POST/PUT/PATCH/DELETE (405 for anything else, e.g. `PROPFIND`).
- `ApiResponseTests.Upstream_set_cookie_is_dropped_and_the_session_still_authenticates_afterwards` (T-04) and `ApiResponseTests.Upstream_5xx_body_is_replaced_with_the_generic_ProblemDetails` (T-05, `Theory` ×3 content types) — an upstream `Decisya.Api` cannot plant a cookie on the BFF's own origin, and a leaking 5xx body is replaced before it reaches the browser; both extend #18's NFR-21 ("zero token values in any browser-visible response") to `/api`.
- `BffOptionsTests.A_Development_only_relaxation_fails_startup_in_Production` (`Theory`, includes the two new `Bff:Api:Address` cases: `http://decisya-api`, `decisya-api`) — the https-only destination check (T-02) fails startup outside Development.
- `LogScanTests.No_log_record_across_login_forward_refresh_refresh_failure_or_logout_carries_a_token_the_client_secret_or_the_session_key` (**NEW**) — G3 MUST G4-19-05's automated log scan, the item G4 left for this gate. Drives login, a forwarded `/api` call, a forced refresh (`FakeClock` past `expires_at`), a refresh Keycloak rejects (`invalid_grant`, ending the session per B-1), and a full sign-out including the server-side end-session call (B-2, `CountingBackchannelHandler.EndSessionCallCount`), all through a new in-memory `CapturingLoggerProvider` wired via `BffWebApplicationFactory`'s new `loggerProvider` parameter, which `PostConfigure`s `LoggerFilterOptions` to force every category to `Debug` (replacing appsettings.json's `Microsoft.AspNetCore`/`Yarp: Warning`, so the scan sees what a more verbose production configuration would too). Every record's message, structured state values and exception text are scanned for the access, ID and refresh tokens (both flows' tickets, before and after refresh), the client secret, and both sessions' Redis keys, plus each token's JWT payload segment. 51/51 Integration tests pass, including this one — no leak found; `Decisya.Bff.Session.BffLog`'s own message templates (never taking a token, cookie, session key or claim value, per its own header comment and CLAUDE.md) hold up under an automated check, not just inspection.

### Test run (2026-09-28, Docker running)

- `dotnet build -warnaserror`: 0 warnings, 0 errors, whole solution.
- Unit lane (`dotnet test --filter-not-trait "Category=Integration" --filter-not-trait "Category=AppHost"`): **632 passed**, 0 failed, 0 skipped — up from 631/632 at G4 (the one failure, `AppHostConfigurationTests.AppHost_cs_passes_secrets_only_through_parameters`'s stale literal, is fixed; no new unit test was added, one assertion was added to that existing test).
- `dotnet test --project tests/Decisya.Bff.Tests --filter-trait "Category=Integration"`: **51 passed**, 0 failed, 0 skipped — up from 50 at G4 (`LogScanTests` ×1, new).

### Verdict

Every Story 1–7 Gherkin scenario and both NFR-24/NFR-25 rows above carry a
passing Direct test; the one scenario with additional Indirect/infrastructure
support (Story 5 scenario 2's "trace id" clause, Story 1's AppHost wiring) is
recorded as such, not a gap. The two items G4 left open for this gate — the
stale AppHost literal assertion and G3 MUST G4-19-05's automated log scan —
are both closed here, each within the issue's "a few lines" proportionality
bar rather than deferred to backlog #83. No new gap is opened.

<!-- gate: G1 | verdict: PASS | issue: #19 -->

<!-- gate: G5 | verdict: PASS | issue: #19 -->
