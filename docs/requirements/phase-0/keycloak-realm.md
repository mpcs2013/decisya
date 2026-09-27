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

## Traceability

Test-engineer reviewed two untracked, unfinished files left by a paused earlier G5 run
(`tests/Decisya.Identity.Tests/KeycloakDbInitScriptTests.cs`,
`KeycloakRealmFixtureSecretResolutionTests.cs`), kept both as-is (each proves a real,
already-required criterion — G4-17-15 and G4-17-02 respectively — entirely without
Docker, and their assertions were checked line-by-line against
`deploy/postgres/init/10-keycloak-db.sh` and `KeycloakRealmFixture.InitializeAsync`), and
added one new test (`ContainerImageParityTests.The_Keycloak_fixture_uses_no_bind_mount_and_never_mentions_the_Docker_socket`)
for the one criterion (G4-17-18's second bullet) that had none and is testable without
Docker. No `src/` or `deploy/` file was changed. Docker Desktop was stopped throughout, so
no `Category=Integration` or `Category=AppHost` test was (re-)run; every Integration/AppHost
row below cites the class/method that carries the assertion and, where the manifest
records it, the G4 run that last executed it.

`dotnet build -warnaserror`: 0 warnings. `dotnet test --no-build --filter-not-trait
"Category=Integration" --filter-not-trait "Category=AppHost"`: **482/482 passed, 0
failed** (up from the G4 manifest's 469, by the 12 tests the two kept files carry plus the
1 new test).

All test classes are in `Decisya.Identity.Tests` unless noted. "Lane" is the trait the
test carries for `--filter-trait`/`--filter-not-trait "Category=…"`: **Unit** (no explicit
trait — this project's own convention for every Docker-free static/behavioural-rule test,
see `RealmExportFileTests`, `RealmSecretRulesTests`, `ContainerImageParityTests`), **Integration**
(`[Trait("Category","Integration")]`, Testcontainers.Keycloak, needs Docker), **AppHost**
(`[Trait("Category","AppHost")]`, the real Aspire AppHost, host-only), or **manual** (a
live check recorded in the manifest's G4 evidence, or a G6 documentation/CI-diff check —
neither is a `dotnet test` case).

### Stories 1-7 (Gherkin acceptance criteria)

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | The realm import happens without manual steps | `RealmReimportTests.The_first_start_logs_the_realm_import_and_a_restart_keeps_the_realm_intact` (first-start log assertion; the test harness itself runs no `kcadm`/Admin Console step) | Integration |
| 1 | A restart re-applies the export without failing | `RealmReimportTests.The_first_start_logs_the_realm_import_and_a_restart_keeps_the_realm_intact` (restart + survives-with-same-entities assertions) | Integration |
| 1 | No credential value in the export is a literal secret | `RealmExportFileTests.The_realm_file_contains_exactly_the_two_allowed_placeholders`, `.The_decisya_bff_client_secret_is_exactly_the_client_secret_placeholder`, `.Every_seeded_users_credential_value_is_exactly_the_dev_password_placeholder`, `.No_key_material_or_hashed_credential_field_appears_anywhere`; the gitleaks-scan half is manual — evidence: manifest G4 evidence, "gitleaks (working tree and history) clean", re-confirmed by the orchestrator's final run | Unit + manual |
| 2 | The client is confidential and requires PKCE S256 | `RealmConfigurationTests.The_decisya_bff_client_matches_every_named_setting` (`publicClient` false, `clientAuthenticatorType` `client-secret`, `pkce.code.challenge.method` `S256`) | Integration |
| 2 | Access tokens are short-lived | `RealmConfigurationTests.The_realm_settings_match_the_platforms_security_invariants` (`accessTokenLifespan` ≤ 300), `.The_decisya_bff_client_matches_every_named_setting` (client override ≤ 300 when present); `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (`exp - iat` ≤ 300 on the issued token) | Integration |
| 2 | Only an allowed signature algorithm is used | `RealmConfigurationTests.The_realm_settings_match_the_platforms_security_invariants` (`defaultSignatureAlgorithm`), `.The_decisya_bff_client_matches_every_named_setting` (both `*.signed.response.alg` attributes, and every client attribute checked against the HS256/none deny-list) | Integration |
| 2 | The client secret is not a literal value in the export | `RealmExportFileTests.The_decisya_bff_client_secret_is_exactly_the_client_secret_placeholder`, `.The_client_secret_field_appears_only_on_the_decisya_bff_client` | Unit |
| 3 | A user with a tenant_id attribute gets the matching claim | `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (asserts the claim equals dev-alice's seeded GUID in both the access and the id token) | Integration |
| 3 | A user without a tenant_id attribute gets no fabricated claim | `BffLoginFlowTests.A_platform_admin_login_carries_no_tenant_id_claim_in_either_token` | Integration |
| 3 | The claim reaches both the ID token and the access token | `RealmConfigurationTests.The_tenant_id_and_audience_protocol_mappers_are_present_and_enabled` (`id.token.claim`/`access.token.claim` both `"true"` on the mapper config); reconfirmed behaviourally by the Story 3 rows above | Integration |
| 4 | Two ordinary users exist in two different tenants | `RealmExportFileTests.The_two_seeded_tenant_users_have_distinct_well_formed_tenant_ids` (static); `RealmConfigurationTests.At_least_two_enabled_tenant_users_have_distinct_tenant_ids_and_one_platform_admin_has_none` (live, also asserts `enabled`) | Unit + Integration |
| 4 | One platform-admin user exists with no tenant | `RealmExportFileTests.The_platform_admin_user_carries_no_tenant_id_attribute` (static); `RealmConfigurationTests.At_least_two_enabled_tenant_users_have_distinct_tenant_ids_and_one_platform_admin_has_none` (live: exactly one enabled `platform-admin`, no `attributes`) | Unit + Integration |
| 4 | Seeded users are unmistakably dev-only | `RealmExportFileTests.Every_seeded_user_has_a_dev_username_and_a_dot_test_email` | Unit |
| 4 | No seeded user's password is a literal value in the export | `RealmExportFileTests.Every_seeded_users_credential_value_is_exactly_the_dev_password_placeholder` | Unit |
| 5 | The AppHost wires Keycloak to a dedicated Postgres database | `AppHostConfigurationTests.AppHost_cs_passes_secrets_only_through_parameters` (`KC_DB`/`KC_DB_USERNAME` literals, secret parameters) in `Decisya.ServiceDefaults.Tests`; `KeycloakDbInitScriptTests` (role/database creation, `REVOKE ALL ON DATABASE keycloak FROM PUBLIC`); live role/ownership evidence is manual — see G4-17-16 below. "No module's DbContext or connection string references that database" has no test: no module schema/DbContext exists yet in this phase (ADR-0005's module Postgres resource is a later issue), so there is nothing to assert against; noted as a gap with a reason, not silently skipped | Unit + manual |
| 5 | Keycloak data survives an AppHost restart | `RealmReimportTests.The_first_start_logs_the_realm_import_and_a_restart_keeps_the_realm_intact` (Testcontainers-level restart survival); `AppHostConfigurationTests.AppHost_cs_marks_postgres_and_keycloak_persistent_with_fixed_names_and_a_test_time_override` (guards the persistent-container wiring that makes the real AppHost's restart survival possible); the real-AppHost-and-volume version of this scenario is manual — evidence: manifest "Dev-volume incident and container lifetime" and Marco's re-verified dev-alice login after the reset | Integration + Unit + manual |
| 6 | The realm's authorization endpoint serves a login form | `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (200, `kc-form-login`, `/realms/decisya/`, not `/realms/master/`) — proven against a real Keycloak (the same pinned image) via Testcontainers, not the AppHost resource itself | Integration |
| 6 | A seeded user can complete the Keycloak-hosted login form | `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (redirect with `code=`, no server error) | Integration |
| 6 | The AppHost's Keycloak resource is healthy before the login page is expected to work (NFR-16) | manual — evidence: manifest's "Done-when, done live by Marco" (all resources Healthy, login page 200, dev-alice redirected with a code) and G4-17-13. `KeycloakResourceTests.Keycloak_and_Postgres_reach_healthy_and_the_login_page_is_reachable` encodes the same assertions (including the 60 s bound) but is `[Fact(Skip)]` for **#70** (Aspire's test host fails Keycloak's auto-HTTPS management-port health check, 4/4 runs) — cited as the tracked gap, not as passing evidence. GETTING-STARTED §3 documents the URL and the seeded user (G6 doc check) | manual (+ #70) |
| 7 | The test asserts every setting named in ADR-0002's Enforced-by line | `RealmConfigurationTests` (whole class): `.The_realm_settings_match_the_platforms_security_invariants`, `.The_decisya_bff_client_matches_every_named_setting`, `.The_tenant_id_and_audience_protocol_mappers_are_present_and_enabled`, `.The_CONFIGURE_TOTP_required_action_is_enabled` | Integration |
| 7 | The test runs in CI without extra plumbing | `RealmConfigurationTests` carries `[Trait("Category","Integration")]`; `IntegrationCategoryTraitGuardTests.Every_class_that_takes_the_Keycloak_realm_fixture_carries_the_Integration_category_trait` statically guards that it (and every fixture-consuming class) keeps that trait; `ci.yml`'s "Integration tests (Testcontainers)" step running it in CI is a G6 diff check, not a `dotnet test` case — evidence: manifest orchestrator run "29 of 29" | Unit + manual |
| 7 | The test never asserts against a literal secret | `RealmConfigurationTests.The_stored_client_secret_equals_the_environment_value_the_placeholder_resolved_to` (`_fixture.ClientSecret`, env-resolved, `FixedTimeEquals`); `BffLoginFlowTests`' login tests use `_fixture.DevUserPassword`, never a literal | Integration |

### Non-functional requirements

| Id | Requirement | Test(s) | Lane |
| --- | --- | --- | --- |
| NFR-15 | Access-token lifespan for `decisya-bff` ≤ 300 s | `RealmConfigurationTests.The_realm_settings_match_the_platforms_security_invariants`, `.The_decisya_bff_client_matches_every_named_setting`; `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (`exp - iat` ≤ 300 on the actually-issued token) | Integration |
| NFR-16 | Keycloak reports healthy within 60 s of `dotnet run` | manual — evidence: manifest's "Done-when, done live by Marco" (all resources Healthy after a fresh import). `KeycloakResourceTests.Keycloak_and_Postgres_reach_healthy_and_the_login_page_is_reachable` carries the 60 s assertion in code but is skipped for **#70**; not cited as passing evidence | manual (+ #70) |

### G4-17-01 to 21 (threat-model requirements)

| Id | Requirement | Test(s) / evidence | Lane |
| --- | --- | --- | --- |
| G4-17-01 | Static realm-file checks (placeholders, components, no IdP/groups, roles, secret placement) | `RealmExportFileTests.The_realm_file_contains_exactly_the_two_allowed_placeholders`, `.Components_hold_only_the_declarative_user_profile_provider`, `.No_identity_provider_and_no_group_is_seeded`, `.No_seeded_user_carries_a_client_role_and_every_realm_role_is_one_of_the_two_platform_roles`, `.The_client_secret_field_appears_only_on_the_decisya_bff_client` | Unit |
| G4-17-02 | Fail-open controls: AppHost guard, fixture's three branches, secret-equality, dev-alice login | `RealmSecretRulesTests.EnsureDevUserPassword_throws_for_an_invalid_value_without_ever_naming_it`, `.EnsureDevUserPassword_throws_for_a_missing_value`, `.EnsureDevUserPassword_does_not_throw_for_a_valid_value` (AppHost guard); `KeycloakRealmFixtureSecretResolutionTests.On_the_host_with_both_variables_unset_a_valid_per_run_fallback_is_generated`, `.With_the_client_secret_variable_unset_in_CI_initialisation_fails_naming_that_variable`, `.With_only_the_dev_password_variable_unset_in_CI_initialisation_fails_naming_that_variable`, `.A_set_but_charset_invalid_value_fails_even_on_the_host_with_CI_unset` (the fixture's three branches — new); `RealmConfigurationTests.The_stored_client_secret_equals_the_environment_value_the_placeholder_resolved_to` and `BffLoginFlowTests`' login tests (dev-alice login, secret equality). The one-off characterisation of the pinned version's unresolved-placeholder behaviour is manual, never a committed test — evidence: manifest finding 1 (`${env.X}` not substituted, confirmed live) | Unit + Integration + manual |
| G4-17-03 | No secret in output | `RealmSecretRulesTests.EnsureDevUserPassword_throws_for_an_invalid_value_without_ever_naming_it` (message/`ToString()` canary check); `RealmConfigurationTests.The_stored_client_secret_equals_the_environment_value_the_placeholder_resolved_to` (`FixedTimeEquals`, boolean-only assertion); `AppHostResourceTests`/`KeycloakResourceTests` assert dictionary **keys** only, never values (L-1). No `upload-artifact` step for TRX is a G6 diff check, not a test | Unit + Integration + manual |
| G4-17-04 | gitleaks stays green; no path allow-list for `deploy/` | manual — `.gitleaks.toml` has no path allow-list touching `deploy/`; evidence: manifest "gitleaks (working tree and history) clean". G6 check per the threat model's own text | manual |
| G4-17-05 | decisya-bff configuration assertions (`accessCodeLifespan`, scopes, device/CIBA grants, request-object alg, redirect/post-logout URIs, `webOrigins`) | `RealmConfigurationTests.The_decisya_bff_client_matches_every_named_setting` | Integration |
| G4-17-06 | Issued-token conformance (`azp`, `aud`, no `offline_access`, refresh `typ`, `alg`, `kid` in JWKS, `exp-iat`) | `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (incl. `AssertTokenIsConformantAsync`) | Integration |
| G4-17-07 | Negative authorization-flow cases | `BffLoginFlowTests.An_authorization_request_with_an_unregistered_redirect_uri_gets_no_login_form` (theory), `.An_authorization_request_for_an_implicit_response_type_gets_no_login_form` (theory), `.An_authorization_request_with_plain_PKCE_or_no_challenge_gets_no_login_form`, `.Password_and_client_credentials_grants_are_rejected_on_decisya_bff`, `.A_token_request_with_the_wrong_client_secret_is_rejected`, `.A_code_exchange_without_the_PKCE_verifier_fails` | Integration |
| G4-17-08 | `admin-cli` in the `decisya` realm | `BffLoginFlowTests.A_password_grant_against_admin_cli_in_the_decisya_realm_fails_or_yields_a_harmless_token`; manual confirmation — evidence: manifest "G4-17-08: `admin-cli` password grant refused (`LOGIN_ERROR … reason="Client not allowed for direct access grants"`)" | Integration + manual |
| G4-17-09 | Self-edit of `tenant_id` is refused | `TenantSelfEditTests.Dev_alice_cannot_change_her_own_tenant_id_through_the_account_console` (managed-attribute overwrite attempt and the unmanaged-attribute-name smuggling attempt) | Integration |
| G4-17-10 | No IdP admin rights | `RealmConfigurationTests.No_seeded_user_is_composite_or_carries_any_realm_management_client_role`, `.No_identity_provider_is_configured`; `registrationAllowed` assertion inside `.The_realm_settings_match_the_platforms_security_invariants` | Integration |
| G4-17-11 | Tenant-value shape (pattern rejects all-zero GUID etc.; two seed values distinct) | `RealmConfigurationTests.The_tenant_id_pattern_accepts_the_seed_values_and_rejects_the_all_zero_GUID`; `RealmExportFileTests.The_two_seeded_tenant_users_have_distinct_well_formed_tenant_ids` | Integration + Unit |
| G4-17-12 | Single import path | `RealmExportFileTests.The_realm_file_name_is_referenced_only_from_the_AppHost_tests_and_docs`; evidence: manifest "the single-import-path static test passes" | Unit |
| G4-17-13 | Loopback-only listeners | manual — evidence: manifest's `docker ps`/`netstat` output (keycloak/postgres bound to `127.0.0.1`, proxy on `127.0.0.1:8080`, nothing on `0.0.0.0` or the LAN) | manual |
| G4-17-14 | Admin credentials never a literal/default | `AppHostConfigurationTests.AppHost_cs_passes_secrets_only_through_parameters` (the literal-key allow-list is exactly `{KC_DB, KC_DB_USERNAME}`, which also blocks `KC_LOG_LEVEL`/`KC_HOSTNAME_STRICT`/`KC_BOOTSTRAP_ADMIN_*`/`KC_HTTP_*` literals). The Testcontainers admin password being freshly randomized (never the library default) is exercised by every test that authenticates as admin (`RealmConfigurationTests`, `RealmReimportTests`, `TenantSelfEditTests`, `PlaceholderSubstitutionRegressionTests` all call `KeycloakRealmFixture.GetAdminAccessTokenAsync` with the per-run generated password) but has no dedicated regression test proving a hardcoded default would fail; noted as a minor gap, not added because it would only re-assert code (`GenerateHex(24)`) already exercised by every one of those tests | Unit + Integration (partial) |
| G4-17-15 | Postgres init script (fail-fast guard, no trace/echo, quoted heredoc, `\getenv`-only password path, log/role/revoke lines, LF-only) | `KeycloakDbInitScriptTests` (new — all 6 tests): `.The_script_starts_with_set_dash_eu_right_after_its_shebang_and_comments`, `.The_script_fails_fast_on_an_unset_password_before_psql_ever_runs`, `.No_line_traces_or_echoes_the_password_and_set_dash_x_never_appears`, `.The_SQL_heredoc_is_single_quoted_so_the_shell_never_expands_it`, `.The_password_reaches_SQL_only_through_the_getenv_bound_kc_password_variable`, `.The_heredoc_disables_statement_logging_sets_the_role_attributes_and_revokes_public_access`, `.The_file_uses_LF_line_endings_only` | Unit |
| G4-17-16 | Role/database privilege evidence (`\du`, `\l`, no PUBLIC connect) | manual — evidence: manifest "G4-17-16 (least privilege) … role `keycloak\|f\|f\|f` … database `keycloak\|keycloak` (owned by the keycloak role)" | manual |
| G4-17-17 | Image pinning (tag shape, registry, digest format, Postgres constants unchanged) | `ContainerImageParityTests.The_Keycloak_tag_is_an_exact_26_x_y_patch_and_the_registry_is_quay_io`, `.Every_pinned_digest_is_64_lowercase_hex_characters`, `.The_Postgres_constants_are_unchanged_from_the_existing_sandbox_line`; the `docker buildx imagetools inspect` digest confirmation is manual — evidence: manifest "confirmed two ways against the quay.io registry" | Unit + manual |
| G4-17-18 | Sandbox image-list line, no bind mount/socket, package justification | `ContainerImageParityTests.The_sandbox_image_list_has_a_keycloak_alias_matching_ContainerImages`, `.The_sandbox_image_list_has_a_postgres_alias_matching_ContainerImages` (parity with `ContainerImages`; a laxer C# regex than `sandbox.py`'s `IMAGE_FROM_RE`, so exact-grammar conformance is confirmed by running `sandbox.py`'s own parser, manual/G4), `.The_Keycloak_fixture_uses_no_bind_mount_and_never_mentions_the_Docker_socket` (new — closes the one gap this gate found). The PR-body package justification is a manual/G6 check | Unit + manual |
| G4-17-19 | CI Integration step (masking, no `$GITHUB_ENV`, no `secrets.*`, no `--ignore-exit-code 8`) | manual/G6 — diff-only check of `ci.yml`; confirmed by reading the current step (openssl-generated values, `::add-mask::` before use, exported only inside the step, no `$GITHUB_ENV`/`$GITHUB_OUTPUT`/`secrets.*`, no `--ignore-exit-code 8`); evidence: manifest orchestrator run "29 of 29" | manual |
| G4-17-20 | Reimport (Story 1's restart scenario) and reset documentation | `RealmReimportTests.The_first_start_logs_the_realm_import_and_a_restart_keeps_the_realm_intact`; GETTING-STARTED §3 documents the three reset-scenario facts (G6 doc check) | Integration + manual |
| G4-17-21 | Docs hygiene (generate-not-paste password guidance, admin console URL, no pasted secret/token) | manual/G6 — `docs/GETTING-STARTED.md` §3 gives `https://localhost:8080/admin/` and states loopback/dev-only, and instructs generating the dev password rather than showing a value | manual |

### Gaps and follow-ups

- **Story 5, "no module's DbContext references Keycloak's database"** — no test; no module schema/DbContext exists yet in this phase to assert against. Revisit when the first module Postgres resource (ADR-0005) ships.
- **Story 6 / NFR-16, AppHost-level health and login reachability** — proven live by Marco (manifest Done-when), not by an automated, currently-passing test; `KeycloakResourceTests` encodes the same assertions but is skipped for **#70**. Re-run this row's test once #70 lands.
- **G4-17-14, admin-password-is-never-a-default** — exercised incidentally by every admin-authenticated Integration test, but not guarded by a dedicated regression test. Left as a minor, explicitly-noted gap rather than adding a test that would only re-verify `KeycloakRealmFixture.GenerateHex(24)`'s own call site.
- **G4-17-18, exact `IMAGE_FROM_RE` grammar conformance** — `ContainerImageParityTests` checks parity with `ContainerImages`, not byte-for-byte conformance with `sandbox.py`'s Python regex; that exact-grammar check is `sandbox.py`'s own parser, run manually/at G4, not a `dotnet test` case.
- No test was added for anything requiring Docker; worth adding once #70 unblocks the AppHost test host: a non-skipped `KeycloakResourceTests`, and (if ever needed) a dedicated "admin password is randomized, not a library default" Integration regression test.

<!-- gate: G5 | verdict: PASS | issue: #17 -->
