# Phase 0 – Keycloak realm export, `decisya-bff` client, seeded users

## Issue 0.05 (#17) — Keycloak realm export + bff client + seeded users

### Scope note (role framing)

Most of this issue is identity **infrastructure**, not a tenant-facing feature: the
realm export, the `decisya-bff` client registration, the seeded dev users and the
AppHost's Keycloak resource have no story a tenant would recognise as a product
capability. Those stories use **"As a Decisya platform operator"** (Marco today), the
person who runs `dotnet run --project src/Decisya.AppHost`, edits
`deploy/keycloak/decisya-realm.json`, and needs the realm to be reproducible across a
clone, a CI runner and, eventually, a self-hosted deployment.

One story is genuinely observable by an end user, even though completing a login is
out of scope (#18): a browser can reach Keycloak's own hosted login page for the
`decisya-bff` client. That story uses **"As a tenant user"**, because it is the first
thing a real user's browser does on the login journey, and the issue's literal
Done-when ("login page reachable") is written from that vantage point.

**Entitlement plan:** N/A for every story in this issue. Identity infrastructure is
shipped identically regardless of subscription plan; nothing here is gated by an
entitlement feature key. `IEntitlementService` (ADR-0008) and any `module.feature` key
belong to the entitlements module, not to Keycloak's realm configuration.

**Scope, tightly bound to issue #17 (per the manifest and ADR-0002/0001):**

- The realm export under `deploy/keycloak/`: the `decisya` realm, the confidential
  `decisya-bff` client (PKCE S256, a client secret from environment/user-secrets,
  access-token lifespan ≤ 5 min, RS256/ES256 signing), realm roles, and the `tenant_id`
  claim mapping (this issue decides the trivial case — see Decision 2 below).
- Seeded development users, clearly dev-only, with passwords never committed in
  source.
- The AppHost's Keycloak resource, importing the realm export automatically when its
  container starts, backed by Keycloak's own Postgres database and role (ADR-0002,
  ADR-0007).
- A realm-configuration test (`Testcontainers.Keycloak`, `Category=Integration`)
  asserting the settings above.
- The Done-when: the realm imports on container start, and Keycloak's own login page
  for `decisya-bff` is reachable in a browser.

**Out of scope, with the owning issue (unchanged from the manifest):**

- The BFF's OIDC code — cookie/OIDC handlers, the Redis ticket store, YARP (#18).
- JWT validation in `Decisya.Api` (#20).
- The final `tenant_id` **resolution** mechanism app-side, and Keycloak Organizations
  if Marco later prefers it over a user attribute (#22); this issue only decides how
  the claim is **populated in Keycloak**, per Decision 2.
- Production deployment, TLS termination, and the restore drill itself (#29 / 0.18).
- The shared, module-facing Postgres AppHost resource (module schemas, migrator role,
  ADR-0005) — only Keycloak's own database is in scope here.

---

### Story 1 — The `decisya` realm imports automatically when the container starts (the Done-when)

As a Decisya platform operator, I want the AppHost's Keycloak resource to import the
`decisya` realm export automatically every time its container starts, so that every
clone, every CI run and every future deployment gets an identical, version-controlled
identity configuration without a manual Admin Console step.

#### Acceptance criteria

```gherkin
Feature: The decisya realm imports automatically at container start

  Scenario: The realm import happens without manual steps
    Given the AppHost's Keycloak resource is configured to import
      deploy/keycloak/decisya-realm.json on startup
    When the AppHost starts the Keycloak resource
    Then the Keycloak container's own log reports that the decisya realm was imported
    And no operator runs a kcadm command or an Admin Console step to make it appear

  Scenario: A restart re-applies the export without failing
    Given the decisya realm was already imported by a previous container start
    When the Keycloak container is restarted with the same realm export file
    Then the container starts successfully
    And the realm's clients, roles and seeded users match the export exactly
      (no duplicate-entity error, no manual cleanup step)

  Scenario: No credential value in the export is a literal secret
    Given deploy/keycloak/decisya-realm.json
    When the file's client-secret and user-credential fields are inspected
    Then every one of them is a placeholder resolved from an environment variable at
      import time, never a literal password or secret string
    And a gitleaks scan of the file reports no finding
```

---

### Story 2 — The `decisya-bff` client matches the platform's security invariants

As a Decisya platform operator, I want the realm export to register the confidential
`decisya-bff` client with PKCE (S256), a short-lived access token, and an allowed
signature algorithm, so that #18's BFF integration authenticates against a client that
already satisfies `CLAUDE.md`'s security principles and ADR-0002's decision, instead of
each later issue re-deriving these settings.

#### Acceptance criteria

```gherkin
Feature: decisya-bff client configuration matches CLAUDE.md and ADR-0002

  Scenario: The client is confidential and requires PKCE S256
    Given the imported decisya realm
    When the decisya-bff client's configuration is inspected
    Then the client is confidential (a client secret is required)
    And it is configured to require PKCE with the S256 code-challenge method only
      (plain is not accepted)

  Scenario: Access tokens are short-lived
    Given the imported decisya realm
    When the decisya-bff client's and the realm's token settings are inspected
    Then the effective access-token lifespan for that client is at most 300 seconds

  Scenario: Only an allowed signature algorithm is used
    Given the imported decisya realm
    When the realm's default signature algorithm and the decisya-bff client's token
      signing configuration are inspected
    Then the algorithm is RS256 or ES256
    And neither HS256 nor "none" is enabled anywhere in that client's token settings

  Scenario: The client secret is not a literal value in the export
    Given deploy/keycloak/decisya-realm.json
    When the decisya-bff client's secret field is inspected
    Then its value is a placeholder resolved from an environment variable
      (for example DECISYA_BFF_CLIENT_SECRET) supplied by user-secrets locally and by
      the environment in CI, never a literal string in the file
```

---

### Story 3 — The `tenant_id` claim reaches the token from a user attribute

As a Decisya platform operator, I want every token Keycloak issues for `decisya-bff`
to carry a `tenant_id` claim populated from a Keycloak user attribute, so that #20's
JWT validation and #22's tenant resolution have a concrete, testable claim to read
now, instead of every later issue blocking on a decision this issue can make trivially
(per the manifest: "the tenant claim source decision — #22, unless it's trivially the
user attribute").

This is Decision 2 below: the mechanism is decided now as a user attribute, without
waiting for #22. #22 keeps the freedom to move to Keycloak Organizations later
(ADR-0002 still names Organizations as an option) if a user ever needs to belong to
more than one tenant; that later change would replace this claim's *source*, not its
*shape* — `tenant_id` stays a single string claim, so #20 and #22's consumers do not
need to change.

#### Acceptance criteria

```gherkin
Feature: tenant_id claim populated from a user attribute

  Scenario: A user with a tenant_id attribute gets the matching claim
    Given a Keycloak user in the decisya realm has a tenant_id user attribute set to
      a well-formed GUID string
    When that user authenticates against decisya-bff and a token is issued
    Then the access token's claims include tenant_id
    And the id token's claims include tenant_id
    And its value equals the user's tenant_id attribute exactly

  Scenario: A user without a tenant_id attribute gets no fabricated claim
    Given a Keycloak user in the decisya realm has no tenant_id user attribute
    When that user authenticates against decisya-bff and a token is issued
    Then the token carries no tenant_id claim
    And no empty-string or default-GUID tenant_id claim is emitted in its place

  Scenario: The claim reaches both the ID token and the access token
    Given a Keycloak user with a tenant_id attribute
    When a token is issued for decisya-bff
    Then the tenant_id protocol mapper is configured to add the claim to both the ID
      token and the access token (not only one of them)
```

---

### Story 4 — Seeded development users exercise two tenants and a platform-admin role

As a Decisya platform operator, I want the realm export to seed a small, clearly
dev-only set of users — at least two ordinary tenant users in two different tenants,
and one platform-admin user with no tenant — with passwords supplied outside the
export, so that #18, #20 and later phase-0 issues have ready-made accounts to test
against without anyone creating them by hand in the Admin Console, and so that a
future two-tenant isolation test (#22) already has two distinct `tenant_id` values to
use.

#### Acceptance criteria

```gherkin
Feature: Seeded development users cover two tenants and a platform-admin role

  Scenario: Two ordinary users exist in two different tenants
    Given the imported decisya realm
    When its seeded users are inspected
    Then at least two enabled users exist, each carrying a tenant_id user attribute
      that is a well-formed, non-empty GUID string
    And the two users' tenant_id values are different from each other

  Scenario: One platform-admin user exists with no tenant
    Given the imported decisya realm
    When its seeded users are inspected
    Then exactly one enabled user carries the platform-admin realm role
    And that user carries no tenant_id user attribute
      (reserved for [AllowCrossTenant]-attributed, audited admin handlers, ADR-0001)

  Scenario: Seeded users are unmistakably dev-only
    Given the imported decisya realm's seeded users
    When their usernames and email addresses are inspected
    Then every one uses a reserved, non-routable domain (for example a .test domain,
      RFC 2606) or an explicit "dev" marker in the username
    And none resembles a real customer's email address

  Scenario: No seeded user's password is a literal value in the export
    Given deploy/keycloak/decisya-realm.json
    When every seeded user's credential block is inspected
    Then each password value is a placeholder resolved from an environment variable
      at import time, never a literal string
```

---

### Story 5 — Keycloak's data lives in its own Postgres database and role

As a Decisya platform operator, I want the AppHost's Keycloak resource to store its
data in a dedicated Postgres database with its own role, not Keycloak's default
in-memory/H2 development store, so that the realm and its seeded users survive an
AppHost restart within a work session, and so that ADR-0002's decision ("Keycloak
stores its data in its own Postgres database and role") and ADR-0007's backup/restore
scope are true from the first issue that ships Keycloak, not retrofitted later.

#### Acceptance criteria

```gherkin
Feature: Keycloak persists to its own Postgres database and role

  Scenario: The AppHost wires Keycloak to a dedicated Postgres database
    Given AppHost.cs adds a Postgres resource (postgres:18-alpine, per ADR-0007) and a
      Keycloak resource
    When the Keycloak resource is configured
    Then it connects to a database and role dedicated to Keycloak, not shared with
      any future module schema
    And no module's DbContext or connection string references that database

  Scenario: Keycloak data survives an AppHost restart
    Given the AppHost has started once and the decisya realm was imported
    And a seeded user's Keycloak-generated internal state (for example a failed-login
      counter) has changed since import
    When the AppHost is stopped and started again against the same Postgres data
      volume
    Then Keycloak starts successfully against the same database
    And the realm and its seeded users are present without a fresh import being
      required to make them appear
```

---

### Story 6 — Keycloak's own login page is reachable (the Done-when)

As a tenant user, I want to be able to open Keycloak's hosted login page for the
`decisya-bff` client in my browser and see the `decisya` realm's login form, so that
once #18 wires the BFF's OIDC redirect, I land on a working identity provider instead
of a connection error or a misconfigured client — this is the issue's literal
Done-when, proven without needing #18's BFF code to exist yet.

#### Acceptance criteria

```gherkin
Feature: Keycloak's login page for decisya-bff is reachable

  Scenario: The realm's authorization endpoint serves a login form
    Given the AppHost is running and the Keycloak resource reports healthy
    When a browser requests the decisya realm's OIDC authorization endpoint for the
      decisya-bff client, with a registered redirect_uri and PKCE parameters attached
    Then Keycloak responds with 200 and an HTML login form
    And the page identifies the decisya realm, not the master realm

  Scenario: A seeded user can complete the Keycloak-hosted login form
    Given the login form from the previous scenario
    And one seeded development user's username and password (Decision 1 below)
    When those credentials are submitted to the form
    Then Keycloak redirects the browser to the registered redirect_uri with an
      authorization code in the query string
    And no server error is returned

  Scenario: The AppHost's Keycloak resource is healthy before the login page is
    expected to work
    Given the AppHost has just started the Keycloak resource
    When the resource's health check is queried
    Then it reports healthy within a bounded time (NFR-16)
    And Getting-started documentation for this issue tells the operator which URL to
      open and which seeded user to log in with
```

---

### Story 7 — A realm-configuration test catches drift before merge

As a Decisya platform operator, I want an automated realm-configuration test using
`Testcontainers.Keycloak` that asserts every setting Stories 2-4 describe, so that a
future edit to `deploy/keycloak/decisya-realm.json` that weakens PKCE, lengthens the
access-token lifespan, downgrades the signing algorithm, or drops a seeded user's
tenant separation fails CI before merge, exactly as ADR-0002's "Enforced by" line
requires.

#### Acceptance criteria

```gherkin
Feature: A realm-configuration test asserts the platform's Keycloak invariants

  Scenario: The test asserts every setting named in ADR-0002's Enforced-by line
    Given a Testcontainers.Keycloak instance importing deploy/keycloak/decisya-realm.json
    When the realm-configuration test runs
    Then it asserts: PKCE S256 required on decisya-bff; access-token lifespan
      ≤ 300 s; RS256/ES256 signing; the tenant_id and audience protocol mappers are
      present and enabled; brute-force detection is enabled; an OTP credential type
      is permitted; a non-trivial password policy is configured
    And the test fails if any one of those assertions does not hold

  Scenario: The test runs in CI without extra plumbing
    Given the test carries the Category=Integration trait
    When ci.yml's existing "Integration tests (Testcontainers)" step runs
    Then the test executes there, using the Docker daemon already available to that
      CI job
    And it requires no new CI job, secret, or Docker socket configuration beyond what
      Testcontainers.Keycloak itself needs

  Scenario: The test never asserts against a literal secret
    Given the test needs a client secret or a seeded user's password to complete a
      login-shaped assertion
    When the test supplies that value
    Then it reads the same environment-variable placeholder the realm export
      resolves at import time, and that value is never a literal committed in the
      test's own source
```

---

## Open questions for Marco — answered

Each question below carried the recommended answer first; Marco confirmed the
recommended answer for all four on 2026-09-26 (see "Decisions" below). None of these
ever blocked writing Stories 1-7 — the stories were worded to hold under any answer —
but ADR-0002's own "Enforced by" line and this issue's Done-when depend on the
concrete values below, and G2/G4 now have an answer for each.

1. **How are seeded dev-user passwords supplied? — Answered: one shared env-var
   placeholder.**
   - **Answer:** the same mechanism ADR-0002 already accepted for the `decisya-bff`
     client secret (D7): an environment variable resolved at Keycloak import time
     (Keycloak's own `${env.VAR}` substitution in the realm JSON), sourced from
     user-secrets locally and from the CI job's environment for the Testcontainers
     test. One shared variable, `DECISYA_DEV_USER_PASSWORD`, is enough for every
     seeded user — they are not production credentials, and a single dev-only value
     keeps `GETTING-STARTED.md` simple. Document the local default value there, never
     in `deploy/keycloak/`.
   - Rejected alternative: a distinct variable per seeded user (more realistic, more
     to wire for no real security benefit at this stage).

2. **Is the `tenant_id` claim mechanism decided now as a plain user attribute (Story
   3), or does it wait for #22 as ADR-0002's Decision outcome literally says? —
   Answered: decided now.**
   - **Answer:** decide it now, as Story 3 does — a user attribute plus a Protocol
     Mapper is a few lines of realm JSON, and `TenantId.Parse` (per its own doc
     comment) already accepts "any well-formed, non-empty GUID" specifically because
     "the exact shape Keycloak's tenant_id claim uses is a later issue's decision" —
     this issue supplies that shape without pre-empting #22's harder question
     (Organizations vs. attribute, and how the API resolves the claim into a
     `TenantId`). #22 may still move to Organizations later.
   - Rejected alternative: leave the mapper out of this issue's export entirely and
     defer the whole claim to #22, which would have left Story 3 (and #20's JWT
     validation) with nothing to read until #22 ships.

3. **Which realm roles exist? — Answered: `tenant-user` and `platform-admin`.**
   - **Answer:** two roles only, for this phase: `tenant-user` (every seeded ordinary
     user) and `platform-admin` (the one seeded cross-tenant user, backing
     `[AllowCrossTenant]`-attributed handlers per ADR-0001; it has no tenant). Real
     per-feature authorization (entitlements, budget ownership, etc.) is a later
     module's concern, not a realm role.
   - Rejected alternative: a finer role set now (for example `tenant-owner` vs.
     `tenant-member`).

4. **Does the realm-configuration test run in CI, or on the host only? — Answered:
   CI (and the host).**
   - **Answer:** `Category=Integration`, running in CI and on the host. `ci.yml`
     already has an "Integration tests (Testcontainers)" step with Docker available
     and a `Category=Integration` filter (`--ignore-exit-code 8`, a placeholder for
     "no Integration test exists yet"); this issue's test is the first to make that
     step do real work, and no new CI plumbing is needed.
   - Rejected alternative: host-only.

## Non-functional requirements

See `docs/requirements/nfr.md` — this issue adds NFR-15 and NFR-16.

## Decisions (Marco, 2026-09-26)

Marco answered all four open questions above, each with the recommended answer:

1. **Dev-user passwords** come from one shared env-var placeholder,
   `DECISYA_DEV_USER_PASSWORD` (Keycloak's `${env.VAR}` substitution at import time),
   supplied from the local secret store on the host and from the job environment in
   CI. Never a literal value in `deploy/keycloak/decisya-realm.json`.
2. **`tenant_id`** is decided now, as a Keycloak user attribute plus a protocol mapper
   (Story 3), added to both the ID token and the access token. #22 may still move to
   Organizations later; that would replace the claim's *source*, not its *shape*.
3. **Realm roles** are `tenant-user` and `platform-admin`. `platform-admin` has no
   `tenant_id` attribute and backs `[AllowCrossTenant]`-attributed, audited admin
   handlers (ADR-0001).
4. **The realm-configuration test** carries `Category=Integration` and runs in CI
   (via `ci.yml`'s existing "Integration tests (Testcontainers)" step) and on the
   host.

<!-- gate: G1 | verdict: PASS | issue: #17 -->
