# Phase 0 – API: JWT bearer validation

## Issue 0.08 (#20) — Decisya.Api validates Keycloak access tokens

### Scope note (role framing)

Every story below is written **"As a tenant user"**, the same framing #18 and
#19 adopted. Some scenarios describe a forged or tampered token rather than
anything a genuine tenant user's browser would ever send — but the person the
control protects is still the tenant user: it is her tenant isolation and her
own identity that a forged token would otherwise let someone borrow. This
mirrors how #19 framed B-1 (a rejected refresh) and B-3 (CSRF): the mechanism
is adversarial, the beneficiary is the tenant user whose data the control
keeps safe. The one exception is Story 4 (unhandled errors), framed as **"As
the platform operator"**, because a stack trace leaking to *any* caller,
genuine or not, is an operational/security property of the API itself, not
something one tenant user experiences differently from another.

**Entitlement plan:** N/A for every story in this issue, for the same reason
as #18 and #19. Authenticating a call to the platform's own API is core
platform infrastructure, identical on every subscription plan. Nothing here
is gated by an entitlement feature key (`IEntitlementService`, ADR-0008, does
not exist yet in phase 0).

**Scope, tightly bound to issue #20 (per the manifest, ADR-0002, ADR-0003 and
the five items #19's G3 carried in):**

- `Decisya.Api` authenticates every request with a Keycloak access token
  (JWT bearer): `iss` against the decisya realm's issuer, `aud` containing
  `decisya-api`, `exp` with a small clock-skew tolerance, the signature
  against Keycloak's JWKS, and an RS256/ES256 algorithm allow-list.
- A token that fails any of those checks — `alg=none`, HS256 (including
  HS256 computed with the RS256 signing key's public component as the HMAC
  secret — the classic algorithm-confusion attack), expired, wrong `aud`,
  wrong `iss`, missing, or malformed — is rejected with `401` and **no
  detail** in the response body or in the `WWW-Authenticate` header: no
  claim value, no validation-failure reason, no exception message.
- The authenticated user and tenant are exposed **only** from validated
  token claims (`sub`, `tenant_id`) — never from a client-supplied header
  such as `X-Tenant-Id` or `X-User-Id`, closing the boundary #19's G3 flagged
  as T-19 and left for this issue.
- `UseExceptionHandler` with a generic `ProblemDetails` body, in every
  environment (Development included) — the second layer behind #19's
  G4-19-02 mitigation, and CLAUDE.md's "Errors: generic message to the
  client, full detail to the structured log with trace id."
- `Decisya.Api` trusts `X-Forwarded-*` headers only from the BFF's own
  address (`ForwardedHeaders` with `KnownProxies`/`KnownNetworks` limited to
  it) — not from an arbitrary immediate peer.
- **A minimal authenticated endpoint the tests need.** `Decisya.Api` has no
  business endpoint yet (its own `Program.cs` comment names this issue as
  the one that changes that). Every scenario below needs something
  authenticated to call. This issue adds `GET /api/whoami` on `Decisya.Api`
  itself (the BFF forwards `/api/x` unchanged, so the route is the same on
  both sides of the proxy — D1): it returns the caller's `sub` (as
  `userId`) and `tenant_id` (as `tenantId`, absent when the token carries
  none) straight from the validated principal's claims, no database, no
  module. See Open question 1 for whether it stays.

**Out of scope, with the owning issue:**

- **Business endpoints, and authorization policies beyond "authenticated".**
  `/api/whoami` requires only a valid, authenticated principal — no role check,
  no policy. Enforcing `tenant-user` vs `platform-admin` distinctions, or any
  business rule, belongs to the module that first needs it.
- **Parsing `tenant_id` into the `TenantId` value type, `ITenantScoped` and
  the global query filter.** This issue exposes the claim as the raw string
  Keycloak issues. #22 owns turning it into a `TenantId` and wiring the
  filter, per `docs/architecture/keycloak-realm.md`'s "Interface for later
  issues" section.
- **Network-level restriction of `Decisya.Api` to the BFF only** (a
  deployment/firewall concern, not application code) — 0.16/0.17, per
  ADR-0002 and ADR-0003's later-issue notes. This issue's `ForwardedHeaders`
  story is about which headers the app *trusts*, not about which peers can
  reach it on the wire.
- **The SPA.** No SPA project exists yet; every scenario below is proven
  against `Decisya.Api` directly (an authenticated bearer call), the same
  style #17/#18/#19 used before a real browser existed.
- **Rate limits and CORS.** Unchanged from #18/#19: not this issue's concern,
  and `Decisya.Api` has no browser-facing CORS surface (only the BFF does).

---

### Story 1 — A tenant user's genuine, valid access token authenticates her and exposes her identity and tenant (the Done-when's supporting story)

As a tenant user, I want `Decisya.Api` to accept my genuine, unexpired
Keycloak access token and tell me who it thinks I am, so that I know my calls
reach a backend that actually checks the token rather than trusting it
blindly, and so that later endpoints have a validated identity and tenant to
build on.

#### Acceptance criteria

```gherkin
Feature: A genuine, valid Keycloak access token authenticates the caller and exposes her identity and tenant

  Scenario: A tenant user's genuine, valid access token is accepted
    Given "dev-alice" holds a genuine access token issued by the decisya realm, for audience "decisya-api", signed with an algorithm on the RS256/ES256 allow-list, not expired
    When a GET request is sent to /api/whoami with "Authorization: Bearer <that token>"
    Then the response is 200
    And the response body's userId equals the token's "sub" claim
    And the response body's tenantId equals the token's "tenant_id" claim

  Scenario: A platform admin's token carries no tenant claim
    Given "dev-admin" holds a genuine, valid access token issued by the decisya realm for audience "decisya-api", whose claims carry no "tenant_id"
    When a GET request is sent to /api/whoami with "Authorization: Bearer <that token>"
    Then the response is 200
    And the response body's userId equals the token's "sub" claim
    And the response body's tenantId is absent — not an empty string, and not the all-zero GUID
```

---

### Story 2 — A forged, invalid or missing bearer token is rejected with 401 and no detail (the Done-when)

As a tenant user, I want `Decisya.Api` to reject any bearer token that isn't
genuinely mine, unexpired and issued for this API — regardless of how it was
tampered with — and to give the caller nothing useful in the rejection, so
that no one can borrow my identity or my tenant by forging, replaying or
malforming a token, and so that a rejection response teaches an attacker
nothing about why it failed.

#### Acceptance criteria

```gherkin
Feature: A bearer token that fails signature, algorithm, audience, issuer, expiry or presence checks is rejected with 401 and no detail

  Scenario Outline: An invalid or forged bearer token never authenticates
    Given a request carries the Authorization value described by "<case>"
    When a GET request is sent to /api/whoami with that Authorization header
    Then the response is 401
    And the response body contains no claim value, validation-failure reason or exception detail
    And the "WWW-Authenticate" response header, if present, contains no claim value or validation-failure detail
    And no downstream code observes an authenticated principal for the request

    Examples:
      | case                                                                                                                    |
      | a token with header alg=none and no signature, otherwise a genuine, unexpired claim set for audience decisya-api        |
      | a token signed HS256 with a random 256-bit secret, otherwise a genuine, unexpired claim set for audience decisya-api    |
      | a token signed HS256 using the decisya realm's RS256 public key bytes as the HMAC secret, otherwise a genuine, unexpired claim set for audience decisya-api (the algorithm-confusion attack) |
      | a genuine, correctly-signed RS256 token whose aud claim is "some-other-api", not "decisya-api"                          |
      | a genuine, correctly-signed RS256 token whose iss claim does not match the decisya realm's configured issuer            |
      | a genuine, correctly-signed RS256 token whose exp claim is more than 5 minutes in the past                              |
      | no Authorization header at all                                                                                         |
      | an Authorization header whose scheme is not "Bearer"                                                                   |
      | an Authorization header "Bearer " followed by an empty or malformed (non-JWT) value                                    |
```

---

### Story 3 — The authenticated user and tenant come only from validated token claims, never from a client-supplied header (the #19 boundary)

As a tenant user, I want `Decisya.Api` to determine who I am and which tenant
I belong to only from my access token's own claims, so that a header a
caller adds — whether by accident, by a misbehaving proxy, or by an attacker
— can never make the API treat me as, or act on behalf of, a different user
or a different tenant.

#### Acceptance criteria

```gherkin
Feature: The authenticated user and tenant are derived only from validated token claims, never from client-supplied headers

  Scenario: A client-supplied tenant header is ignored
    Given "dev-alice" holds a genuine, valid access token whose tenant_id claim is "7c9e6679-7425-40de-944b-e07fc1f90ae7"
    When a GET request is sent to /api/whoami with "Authorization: Bearer <that token>" and a header "X-Tenant-Id: 2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"
    Then the response is 200
    And the response body's tenantId is "7c9e6679-7425-40de-944b-e07fc1f90ae7", the token's own claim value, not the header's

  Scenario: A client-supplied user header is ignored
    Given "dev-alice" holds a genuine, valid access token
    When a GET request is sent to /api/whoami with "Authorization: Bearer <that token>" and a header "X-User-Id: someone-elses-id"
    Then the response is 200
    And the response body's userId equals the token's own "sub" claim, not the header's value
```

---

### Story 4 — An unhandled exception never returns detail to the caller, in any environment

As the platform operator, I want any unhandled exception in `Decisya.Api` to
return a generic `ProblemDetails` body with the exception's full detail
written only to the structured log, in every environment including
Development, so that a bug can never leak a stack trace, an exception
message or a claim value to whoever happens to be calling — genuine tenant
user or not.

#### Acceptance criteria

```gherkin
Feature: An unhandled exception returns a generic ProblemDetails response in every environment

  Scenario Outline: An unhandled exception never reaches the client as detail
    Given Decisya.Api is running with environment "<environment>"
    And an authenticated request is made to an endpoint that throws an unhandled exception
    When the response is received
    Then the response is a ProblemDetails body carrying a generic message, the correct status code, and no exception message, stack trace or claim value
    And the exception's full detail, together with a trace id, is written to the structured log

    Examples:
      | environment |
      | Development |
      | Production  |
```

---

### Story 5 — `Decisya.Api` trusts `X-Forwarded-*` headers only from the BFF's own address

As a tenant user, I want `Decisya.Api` to only honour `X-Forwarded-*` headers
when they come from the BFF itself, so that a header injected by anyone else
in the request path — a compromised intermediary, or a caller reaching the
API directly — can't misrepresent the original scheme or client address that
downstream logging or security decisions rely on.

#### Acceptance criteria

```gherkin
Feature: Decisya.Api trusts X-Forwarded-* headers only from the BFF's own network

  Scenario: A request from outside the configured BFF address has its forwarded headers ignored
    Given Decisya.Api's ForwardedHeaders middleware is configured with the BFF's address as the only known proxy
    And a request arrives from a peer address outside that configuration, carrying "X-Forwarded-For: 6.6.6.6" and "X-Forwarded-Proto: http"
    When the request is processed
    Then Decisya.Api's view of the connection's remote address and scheme is the actual peer's, not the header's values

  # Deferred to 0.16 (Marco, G2 D4, 2026-09-28): #20 trusts no forwarded headers at all —
  # it uses neither the client IP nor the scheme, and the BFF is loopback on the host.
  # This scenario proves conditional trust-by-address once Decisya.Api actually needs
  # a forwarded client IP or scheme.
  @deferred-0.16
  Scenario: A request from the configured BFF address has its forwarded headers applied
    Given Decisya.Api's ForwardedHeaders middleware is configured with the BFF's address as the only known proxy
    And a request arrives from that configured address, carrying "X-Forwarded-Proto: https"
    When the request is processed
    Then Decisya.Api's view of the connection's scheme is "https", taken from the header
```

---

## Open questions for Marco — answered

Both carry a recommended answer, adopted above/below so G2 has a concrete
value to build against. Flag either one at any later gate if it should be
revisited.

1. **Does `/api/whoami` stay as a permanent endpoint, or is it removed once a
   real business endpoint exists? — Answered: it stays.**
   - **Answer:** `GET /api/whoami` stays as a permanent, minimal, unauthenticated-
     by-nothing-but-a-valid-token diagnostic endpoint. It never grows business
     logic, a database call, or an entitlement gate.
   - Rationale: it costs nothing to keep, it is already the target every test
     in this issue proves the JWT pipeline against, and it doubles as a cheap
     manual smoke check ("is the API actually validating tokens right now")
     the same way `/bff/me` already does for the BFF. Removing it would strand
     `Decisya.Api.Tests`' auth-pipeline tests with no target until a business
     module ships, and would force them to be rewritten against whatever
     endpoint arrives first instead.
   - Rejected alternative: delete it once #21 or a later module adds a real
     endpoint, and re-point the tests there. This would make this issue's own
     tests dependent on a future issue's endpoint shape, and lose a cheap,
     stable smoke-test target for no benefit.

2. **What clock-skew tolerance does `exp` (and `nbf`) validation use? — Answered: 60 seconds.**
   - **Answer:** 60 s, replacing the JWT-bearer handler's 300 s default.
   - Rationale: the handler's default `ClockSkew` (300 s) stacked on top of
     the ≤ 300 s maximum access-token lifespan (NFR-15) would let a token
     that expired up to 5 minutes ago still validate — effectively doubling
     the token's real-world lifetime and directly undermining the Done-when
     ("expired ... rejected"). 60 s comfortably covers realistic clock drift
     between the API host and Keycloak without materially weakening the
     ≤ 300 s lifespan CLAUDE.md and ADR-0002/0003 already fix.
   - Rejected alternative: leave the library default (300 s) — technically
     simple, but contradicts the issue's own Done-when in the boundary case,
     and is exactly the kind of silent, unmeasured gap a later security
     review would have to catch instead.

## NFRs added

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-26 | Security | Every request carrying a token with a wrong audience, a wrong issuer, an expired `exp`, `alg=none`, or an HS256 signature (including HS256 computed with the RS256 signing key's public component as the HMAC secret) is rejected by `Decisya.Api` with 401 and zero claim value, validation-failure reason or exception detail in the response body or `WWW-Authenticate` header, across 100% of such requests | `Decisya.Api.Tests` integration test (Testcontainers Keycloak, `Category=Integration`) (0.08) |
| NFR-27 | Reliability | `Decisya.Api`'s `exp`/`nbf` validation tolerates a clock-skew of ≤ 60 s (down from the JWT-bearer handler's 300 s default, which stacked on the ≤ 300 s maximum access-token lifespan, NFR-15, would accept a token up to 5 minutes past its stated expiry) | `Decisya.Api.Tests` integration test asserting a token 59 s past `exp` is accepted and one 61 s past `exp` is rejected (`Category=Integration`) (0.08) |

These two rows are appended to `docs/requirements/nfr.md` in this same PR.

## Decisions

- **D1 (source: Marco, G2 `docs/architecture/api-jwt-validation.md`, 2026-09-28).**
  The minimal endpoint is `GET /api/whoami` on `Decisya.Api` itself — the BFF
  forwards `/api/x` unchanged, so the route is the same on both sides of the
  proxy, not `/whoami` internally. Every scenario above uses `/api/whoami`.
- **D4 (source: Marco, G2 `docs/architecture/api-jwt-validation.md`, 2026-09-28).**
  `Decisya.Api` trusts no forwarded headers in #20: it uses neither the
  client IP nor the request scheme from `X-Forwarded-*`, and the BFF is
  loopback on the host. Story 5 scenario 1 (headers from outside the BFF are
  ignored) stays, proven in this issue. Scenario 2 (headers trusted only
  from the BFF's configured address) is deferred to 0.16, marked
  `@deferred-0.16` above, for when `Decisya.Api` first needs a forwarded
  client IP or scheme.

<!-- gate: G1 | verdict: PASS | issue: #20 -->
