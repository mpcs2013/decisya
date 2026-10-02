# Phase 0 – Modules.Admin API: platform-admin endpoints for the Entitlements commands

## Issue 0.13 (#25) — Modules.Admin API

Done when (issue): a non-admin gets 403; every call is audited.

Inputs: `docs/ai/pipeline/25.md` (Marco's decisions of 2026-10-02: full tier; scope below), the carry-forwards C-2 and S-5 (#23) and C-4 (#24), `docs/requirements/phase-0/entitlements-module.md`, `audit-module.md`, `api-jwt-validation.md`, `tenancy-module.md`, `bff-api-forwarding.md`, `keycloak-realm.md`, ADR-0012, ADR-0013, `deploy/keycloak/decisya-realm.json`, and the current `CallerContextMiddleware`, `ICurrentCaller` and Entitlements handlers. `gh issue view 25 --comments` was not run in this session, so the carry-forwards come from the manifest. Anything the issue body might add beyond that is an open question, not an assumption.

### Scope note (role framing)

Stories are written "As a platform admin" (the operator who calls the endpoints), "As a tenant user" (the tenant acted on, or a caller who must be refused) and "As the platform operator" (properties no single caller experiences). The SPA reaches `/api` only through the BFF, with the session cookie and an antiforgery header on state-changing verbs, and the BFF attaches the access token server-side (#19, #20). The criteria below are written at the `Decisya.Api` level (a bearer call) and, for the end-to-end ones, through the BFF.

**Entitlement plan.** None of these endpoints is gated by a feature key. They are platform operations that a subscription plan cannot switch on or off, and an `entitlements.*` key would be circular (Entitlements is the gating primitive). Each endpoint carries ADR-0008's explicit no-entitlement marker, and the endpoint-metadata check, if it exists by then, must accept it. Access is role-gated instead (Story 1).

### Scope, tight (#84)

In scope:
- A platform-admin realm role exposed to the API as a validated claim of the access token, and an `IsPlatformAdmin` fact on `ICurrentCaller` (C-2). The Entitlements handler precondition becomes `None && IsPlatformAdmin`.
- Three admin endpoints over the three existing Entitlements commands (start trial, grant override, revoke override), one audit record per success (the #24 record, unchanged).
- A target-tenant existence check before any write (S-5).
- The dev-admin user can call the endpoints in a local run.

Out of scope, with owner:
- The audit reader and the `decisya_audit` SELECT-only role (C-4's reader half): #83.
- Other C-4 items are not touched here (fault matrix per audited type applies to any new audited type, and #25 adds none).
- Any listing, search or read endpoint for trials, overrides or tenants (no admin reader exists, so a response never echoes stored rows).
- Admin UI in the SPA, and the `/bff/me` capability manifest (#26).
- Billing, plan changes, auto-start of trials.
- Auditing refused calls (Q4).
- Rate limiting of admin endpoints (not a phase-0 concern anywhere in the API yet).

### Design notes (adopted as recommendations; see Open questions)

- **Role source.** The platform-admin fact comes only from the validated access token that the API itself verified (#20). It is never taken from a header, from the BFF's ID token, from a query string or from the request body. The realm already defines the role `platform-admin` and seeds `dev-admin` with it and with no `tenant_id` (#17). One gap exists today: the realm's role mapper (`realm-roles-id-token`) puts `roles` into the ID token only (`access.token.claim: false`), and the API validates the access token. The access token must therefore carry the role. The claim name and mapper are G2's choice (G2 must also keep `RealmConfigurationTests` and `RealmExportFileTests` in step). Observable requirement: dev-admin's access token carries the role, and dev-alice's and dev-bob's do not.
- **Role matching is exact and fail-closed.** Only the exact value `platform-admin` counts. A missing claim, a differently cased value, the value inside another claim, or an ambiguous claim set (for example a duplicated roles claim shape the parser cannot read as one list) gives `IsPlatformAdmin = false`. `ICurrentCaller.IsPlatformAdmin` is false by default, so a test double or a caller never set is not an admin.
- **Admin means `None` and role.** The caller has the role and no `tenant_id` claim (Q2). A caller with the role and a `tenant_id` is refused. The endpoint policy and the handler precondition (`None && IsPlatformAdmin`, defence in depth, ADR-0012 point 2) both apply.
- **Check order.** (1) 401 for no or invalid token (#20); (2) the pre-routing caller-context 403 for an invalid identity or tenant claim (#21); (3) admin policy 403; (4) route and body validation, 400; (5) handler: `Forbidden` precondition, actor check, command validation (existing order from #24), target tenant exists (404), write plus audit in one transaction. Invalid input for an unknown tenant therefore gives 400, not 404.
- **Responses.** All three success responses are `204 No Content`, with no body. The commands return a `Result` with no payload, and there is no admin reader (deferred), so a richer body would need a read model this issue does not add. Every response, success or error, carries `Cache-Control: no-store`.
- **Error body.** The same generic `ProblemDetails` shape as the other #21 responses: `status`, `title`, `traceId`, plus a stable `code` extension for 400, 404 and 409 only (the existing `entitlements.*` codes and `entitlements.tenant_not_found`). A 403 carries no `code`, so a caller cannot tell why it was refused. No body ever echoes the reason text, the claim values, a row or a database message.
- **Error mapping** (from the existing `EntitlementsErrors`):

| Result code | HTTP | Note |
| --- | --- | --- |
| `entitlements.forbidden` | 403 | handler precondition tripped (defence in depth) |
| `entitlements.actor_unknown` | 403 | unreachable over HTTP, since the middleware refuses a caller with no `sub`; still mapped |
| `entitlements.tenant_invalid` | 400 | the all-zero GUID or an unparseable path value |
| `entitlements.feature_unknown` | 400 | PUT: key not in the catalog; DELETE: malformed key only |
| `entitlements.reason_invalid` | 400 | empty after trim, or longer than 500 characters |
| `entitlements.expiry_not_in_future` | 400 | `expiresAt` at or before the clock |
| `entitlements.trial_already_used` | 409 | a trial already exists for the tenant, active or expired |
| `entitlements.tenant_not_found` (new) | 404 | no such tenant (Story 5, Q3) |
| anything else, including a database error | 500 | generic body; detail only in the log with the trace id |

- **No AI dependency** (invariant 2). Nothing here touches `IChatClient`.
- **No financial advice** (invariant 6). The endpoints grant product access, not guidance. No disclaimer criterion applies.

---

## Open questions for Marco (all four answered by Marco, 2026-10-02: recommendations accepted as written)

Status: Q1 answered (Marco, 2026-10-02). Q2 answered (Marco, 2026-10-02). Q3 answered (Marco, 2026-10-02). Q4 answered (Marco, 2026-10-02). No open question remains, and the stories below are written to these answers.

1. **URL shape.** Answered (Marco, 2026-10-02): recommendation accepted, REST under `/api/admin`, 204 on success.
   - Recommended: resource-style, under one group `/api/admin/tenants/{tenantId}`:
     - `POST /api/admin/tenants/{tenantId}/trial` (start the trial; no body);
     - `PUT /api/admin/tenants/{tenantId}/overrides/{featureKey}` (grant or replace; body `{ "reason": string, "expiresAt": string|null }`);
     - `DELETE /api/admin/tenants/{tenantId}/overrides/{featureKey}` (revoke).
   - Why: the tenant and the feature are resources, so the PUT is naturally idempotent (the command already replaces), DELETE is naturally idempotent (revoking nothing succeeds), and the single `/api/admin` prefix gives one policy group and one authorization test. The BFF already forwards every verb on `/api/*` with antiforgery on non-GET.
   - Rejected: action-style (`POST .../grant-override`), which hides the idempotence and gives three verbs-as-nouns routes; and a flat `/api/admin/overrides` with the tenant in the body, which makes the target tenant easy to omit from logs and route-level authorization.
2. **May an admin also carry a `tenant_id` claim?** Answered (Marco, 2026-10-02): recommendation accepted, 403.
   - Recommended: no. Admin means role present and no `tenant_id`. A caller with the role and a tenant claim gets 403 on every admin endpoint. The caller-context middleware resolves such a caller as `Tenant`, and the handler precondition (`None && IsPlatformAdmin`) refuses it anyway, so this is also the behaviour that needs no code beyond the policy. It keeps a tenant user from becoming cross-tenant by a misconfigured role grant, and keeps ADR-0012's separation (an admin has no tenant, so no tenant data is ever read by accident). Keycloak lets an operator edit `tenant_id` on any user, so the combination is possible by mistake.
   - Rejected: letting a dual user act as admin. It would need the handler to accept `Tenant && IsPlatformAdmin`, and the audit actor would then be a tenant member acting across tenants, which blurs the attribution the audit log exists for.
3. **Unknown target tenant: 404 or 422?** Answered (Marco, 2026-10-02): recommendation accepted, 404 `entitlements.tenant_not_found` after validation.
   - Recommended: 404 `Not Found` with code `entitlements.tenant_not_found`. The tenant is the resource named in the URL, and the caller is already an authenticated platform admin, so confirming non-existence leaks nothing. 400 stays for malformed ids and bad bodies, 409 for the used trial.
   - Rejected: 422, which suits a body field referring to something missing, not a path resource. Rejected also: succeeding silently or creating the tenant. A grant to a tenant that does not exist would create an orphan override and a misleading audit record.
   - Consequence for Marco to know: tenants exist only after a user of that `tenant_id` has first signed in (#21 JIT provisioning). An admin cannot pre-grant a trial or override to a tenant that has never signed in. This is accepted for phase 0.
4. **"Every call audited": successes only, or refusals too?** Answered (Marco, 2026-10-02): recommendation accepted, successes only.
   - Recommended: successes only, as #24 Q2. A success writes exactly one record in the command's transaction (already built). Refused calls (401, 403, 400, 404, 409) leave only the structured log line and metric, with the fixed fields and the trace id. The 403 from a non-admin is never a handler call at all, so it is not an "admin call".
   - If refusals must be audited, this changes:
     - `Outcome` gains `refused` (and likely `denied`), which means a migration to the check constraint on `audit.audit_records`. #24's closed-set tests and NFR-36/37 change.
     - Auditing a 403 means the API writes a record for a caller who has no right to the target scope. The writer today is the Entitlements handler's own transaction (ADR-0013), so refusals before the handler need a new write path with its own connection, and the `INSERT`-only role would be reachable by unauthorised traffic: a database-write amplifier an anonymous-adjacent caller can trigger.
     - `TenantId` is a required, target-scoped column. Refusals with an unparseable or unknown tenant have no valid value, so the table shape (nullable or sentinel tenant) changes, and invariant 1 needs a new decision.
     - The #24 proof "a `Forbidden` call issues zero database commands" and NFR-38's "zero database commands" would be dropped for admin refusals.
     - That is a #24-sized change of its own, so it should be its own issue (#83), not part of #25.
   - Rejected: auditing everything in #25. It violates #84 and re-opens a Marco decision made on 2026-10-01.

---

### Story 1 — Only a platform admin can reach the admin endpoints; everyone else gets 403 (the Done-when, first half)

As a tenant user (and as the platform operator), I want every admin endpoint to refuse anyone who is not a platform admin, so that no tenant user, no half-configured account and no forged claim can grant a feature or start a trial.

**Gating feature key:** none (role-gated platform operation; explicit no-entitlement marker).

```gherkin
Feature: /api/admin/** is reachable only by a platform admin (C-2)

  Background:
    Given the Api host has an unreachable placeholder database, so a refusal proves no query ran
    And the admin endpoints are
      | verb   | path                                                                              |
      | POST   | /api/admin/tenants/7c9e6679-7425-40de-944b-e07fc1f90ae7/trial                      |
      | PUT    | /api/admin/tenants/7c9e6679-7425-40de-944b-e07fc1f90ae7/overrides/forecasting.scenarios |
      | DELETE | /api/admin/tenants/7c9e6679-7425-40de-944b-e07fc1f90ae7/overrides/forecasting.scenarios |

  Scenario Outline: A caller who is not a platform admin gets 403, a generic body and no side effect
    Given the caller holds a valid access token described by "<caller>"
    When each admin endpoint is called with a valid request body
    Then the response is 403
    And the body is a generic ProblemDetails with only status, title and traceId, with no "code" and no claim value
    And the response carries "Cache-Control: no-store"
    And no database command is issued, no entitlement row is changed and no audit record is written

    Examples:
      | caller                                                                                           |
      | a tenant user (dev-alice): role tenant-user, tenant_id 7c9e6679-7425-40de-944b-e07fc1f90ae7      |
      | a user with no tenant_id and no platform-admin role                                              |
      | a user with the role platform-admin and also a tenant_id claim (Q2)                              |
      | a user whose roles claim holds "Platform-Admin" (wrong case), "platform-admin-x" or "admin"      |
      | a user with a "platform-admin" value only in an unrelated claim (for example groups or a custom claim) |
      | a user with a malformed tenant_id claim (not a GUID), a duplicated tenant_id, or an empty tenant_id |
      | a user whose sub claim is missing, empty or duplicated                                           |

  Scenario: A platform admin passes the role check
    Given the caller holds a valid access token with the role "platform-admin", sub "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59" and no tenant_id
    When the caller sends a valid request to any admin endpoint for an existing tenant
    Then the response is not 401 or 403

  Scenario: The role is taken only from the validated access token
    Given dev-alice's valid access token
    When the request also carries the headers "X-Roles: platform-admin", "X-User-Role: platform-admin" and "X-Tenant-Id: none"
    Then the response is 403
    And the role is not read from the BFF's ID token, a cookie, a header, the query string or the body

  Scenario: An anonymous or invalid-token call gets 401, never 403 or a redirect
    Given the request carries no bearer token, an expired token or a token for another audience
    When any admin endpoint is called
    Then the response is 401 with no detail
    And no database command is issued

  Scenario: IsPlatformAdmin fails closed
    Given an ICurrentCaller that was never set, or a test double with no role information
    When IsPlatformAdmin is read
    Then it is false

  Scenario: The handler refuses a non-admin even if the endpoint policy is bypassed
    Given the ambient resolution is "no tenant" and IsPlatformAdmin is false
    When any of the three Entitlements commands is executed directly with valid input
    Then the result is "entitlements.forbidden" before validation, with no database command and no audit record
    And a "no tenant" ambient with IsPlatformAdmin true is the only combination that proceeds
    And "tenant" and "invalid" ambients are refused whatever IsPlatformAdmin is

  Scenario: Every admin route carries the policy and the explicit no-entitlement marker
    When the endpoint metadata of the Api host is inspected
    Then every route under "/api/admin" requires the platform-admin policy
    And no route under "/api/admin" is anonymous
    And each carries the explicit no-entitlement marker and no feature key

  Scenario: The admin policy applies to routes added later under the same prefix
    When a route is mapped under "/api/admin" without the policy
    Then the architecture or endpoint-metadata test fails
```

### Story 2 — A platform admin starts a tenant's trial

As a platform admin, I want to start the 14-day Pro trial for a named tenant, so that I can support a prospect without a billing flow.

**Gating feature key:** none.

```gherkin
Feature: POST /api/admin/tenants/{tenantId}/trial

  Background:
    Given a fake IClock fixed at "2026-10-01T09:00:00Z"
    And the caller is a platform admin with sub "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59" and no tenant_id
    And tenant A "7c9e6679-7425-40de-944b-e07fc1f90ae7" exists and has no trial

  Scenario: Starting a trial succeeds and is audited
    When the admin sends "POST /api/admin/tenants/7c9e6679-7425-40de-944b-e07fc1f90ae7/trial" with no body
    Then the response is 204 with no body and "Cache-Control: no-store"
    And a TrialGrant exists for tenant A with plan Pro, StartsAt "2026-10-01T09:00:00Z" and EndsAt "2026-10-15T09:00:00Z"
    And exactly one audit record exists for tenant A with Action "entitlements.trial.start", Outcome "succeeded", ActorUserId "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59" and a null FeatureKey

  Scenario: The tenant then sees Pro features
    Given the trial was started
    When tenant A's own user asks IEntitlementService for "forecasting.scenarios" at "2026-10-14T09:00:00Z"
    Then it is allowed
    And tenant B "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b" is still denied

  Scenario: A second start is refused with 409 and writes nothing
    Given tenant A already has a trial, active or expired
    When the admin sends the same request again
    Then the response is 409 with code "entitlements.trial_already_used"
    And exactly one TrialGrant and exactly one audit record exist for tenant A

  Scenario: A malformed tenant id in the path gets 400
    When the admin sends "POST /api/admin/tenants/not-a-guid/trial"
    Then the response is 400 with a generic ProblemDetails
    And the same holds for "00000000-0000-0000-0000-000000000000" with code "entitlements.tenant_invalid"
    And no audit record is written

  Scenario: A request body on the trial endpoint is ignored or refused, never stored
    When the admin sends the trial request with a JSON body that contains a "reason" and a "tenantId" for tenant B
    Then the trial is started for the tenant in the path only, or the request is refused with 400
    And tenant B has no trial

  Scenario: A state-changing call through the BFF needs the antiforgery token
    Given the admin has a valid BFF session
    When the SPA sends the POST through "/api/admin/..." with the session cookie and no antiforgery header
    Then the BFF refuses it and Decisya.Api is not called
    And with a matching antiforgery pair it is forwarded with the access token attached server-side and the cookie not forwarded
```

### Story 3 — A platform admin grants or replaces a feature override

As a platform admin, I want to grant a tenant a feature outside its plan, optionally until a date, so that I can support a design partner without changing their plan.

**Gating feature key:** none.

```gherkin
Feature: PUT /api/admin/tenants/{tenantId}/overrides/{featureKey}

  Background:
    Given a fake IClock fixed at "2026-10-01T09:00:00Z"
    And the caller is a platform admin with sub "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59" and no tenant_id
    And tenant A "7c9e6679-7425-40de-944b-e07fc1f90ae7" exists, on Free, with no override

  Scenario: Granting an override succeeds and is audited (the Done-when)
    When the admin sends "PUT /api/admin/tenants/7c9e6679-7425-40de-944b-e07fc1f90ae7/overrides/forecasting.scenarios" with the body {"reason":"Design-partner pilot","expiresAt":"2026-12-31T23:59:59Z"}
    Then the response is 204 with no body
    And a FeatureOverride exists for tenant A and "forecasting.scenarios" with ExpiresAt "2026-12-31T23:59:59Z"
    And tenant A is allowed "forecasting.scenarios" and tenant B is denied it
    And exactly one audit record exists for tenant A with Action "entitlements.override.grant", FeatureKey "forecasting.scenarios", Outcome "succeeded" and ActorUserId "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59"

  Scenario: An override without an expiry lasts until revoked
    When the admin sends the PUT with the body {"reason":"Design-partner pilot"}
    Then the response is 204
    And the override has no ExpiresAt

  Scenario: The same PUT again replaces the override and writes a further record
    Given an override exists for tenant A and "forecasting.scenarios" with reason "Design-partner pilot"
    When the admin sends the PUT with the body {"reason":"Pilot extended","expiresAt":"2027-03-31T23:59:59Z"}
    Then the response is 204
    And exactly one FeatureOverride row exists for tenant A and "forecasting.scenarios", with reason "Pilot extended"
    And exactly two audit records with Action "entitlements.override.grant" exist for tenant A

  Scenario Outline: A request the command rejects gets 400 with a stable code and writes nothing
    When the admin sends the PUT for "<feature>" with the body <body>
    Then the response is 400 with code "<code>"
    And no FeatureOverride row and no audit record is written

    Examples:
      | feature            | body                                                  | code                                |
      | nosuch.feature     | {"reason":"Pilot"}                                    | entitlements.feature_unknown        |
      | forecasting.scenarios | {"reason":""}                                      | entitlements.reason_invalid         |
      | forecasting.scenarios | {"reason":"   "}                                   | entitlements.reason_invalid         |
      | forecasting.scenarios | {"reason":"<501 characters>"}                      | entitlements.reason_invalid         |
      | forecasting.scenarios | {"reason":"Pilot","expiresAt":"2026-09-30T09:00:00Z"} | entitlements.expiry_not_in_future |
      | forecasting.scenarios | {"reason":"Pilot","expiresAt":"2026-10-01T09:00:00Z"} | entitlements.expiry_not_in_future |

  Scenario Outline: A body that fails schema validation gets 400 before any command runs
    When the admin sends the PUT with <body>
    Then the response is 400 with a generic ProblemDetails
    And no database command is issued and no audit record is written

    Examples:
      | body                                                                       |
      | no body                                                                    |
      | invalid JSON                                                               |
      | a JSON array or a string instead of an object                              |
      | {"reason":42}                                                              |
      | {"reason":"Pilot","expiresAt":"not-a-date"}                                |
      | {"reason":"Pilot","expiresAt":"2026-12-31T23:59:59"} (no UTC designator)    |
      | {"reason":"Pilot","tenantId":"2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"}       |
      | a Content-Type other than application/json                                  |

  Scenario: The reason is never echoed or logged
    When the admin sends the PUT with the reason "MARKER-9f3a Contact: anna.meier@example.com, IBAN DE89370400440532013000"
    And again with a request that fails validation
    Then no response body, header, captured log line, exception message, metric tag or activity tag contains "MARKER-9f3a", the e-mail address or the IBAN
    And no audit column contains them

  Scenario: A feature key in the path is matched exactly
    When the admin sends the PUT for "Forecasting.Scenarios" or "forecasting.scenarios%20" or an URL-encoded variant of another key
    Then the response is 400 with code "entitlements.feature_unknown"
```

### Story 4 — A platform admin revokes a feature override

As a platform admin, I want to revoke an override, so that the tenant returns to its plan's answer.

**Gating feature key:** none.

```gherkin
Feature: DELETE /api/admin/tenants/{tenantId}/overrides/{featureKey}

  Background:
    Given the caller is a platform admin with sub "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59" and no tenant_id
    And tenant A "7c9e6679-7425-40de-944b-e07fc1f90ae7" exists

  Scenario: Revoking an override succeeds and is audited
    Given an override exists for tenant A and "forecasting.scenarios"
    When the admin sends "DELETE /api/admin/tenants/7c9e6679-7425-40de-944b-e07fc1f90ae7/overrides/forecasting.scenarios"
    Then the response is 204 with no body
    And tenant A is denied "forecasting.scenarios"
    And exactly one audit record exists for tenant A with Action "entitlements.override.revoke", FeatureKey "forecasting.scenarios" and Outcome "succeeded"

  Scenario: Revoking a feature with no override is idempotent and still audited
    Given tenant A has no override for "forecasting.scenarios"
    When the admin sends the same DELETE
    Then the response is 204 and no entitlement row changes
    And exactly one audit record with Action "entitlements.override.revoke" exists for tenant A

  Scenario: Revoking for tenant A never touches tenant B's override
    Given tenant B "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b" has an override for "forecasting.scenarios"
    When the admin revokes it for tenant A
    Then tenant B's override still exists and tenant B still has its audit records unchanged

  Scenario: A well-formed key that the catalog does not list is accepted and audited (Marco, 2026-10-02)
    Given tenant A has no override for "nosuch.feature"
    When the admin sends the DELETE for "nosuch.feature"
    Then the response is 204 and no entitlement row changes
    And exactly one audit record with Action "entitlements.override.revoke" and FeatureKey "nosuch.feature" exists for tenant A

  Scenario Outline: A malformed key gets 400 and writes nothing
    When the admin sends the DELETE for the key "<key>"
    Then the response is 400 with code "entitlements.feature_unknown"
    And no database command is issued and no audit record is written

    Examples:
      | key                                           |
      | Forecasting.Scenarios                         |
      | forecasting                                   |
      | forecasting.scenarios.extra                   |
      | .scenarios                                    |
      | 1forecasting.scenarios                        |
      | forecasting.scenarios%20                      |
      | a key of 65 characters in the form a.bbbb...  |

  Scenario: A revoke for a key later removed from the catalog still works
    Given an override row exists for a key the catalog no longer lists
    When the admin sends the DELETE for that key
    Then the behaviour is the existing #23 behaviour for such a key, and the response mapping is the one in the error table
```

### Story 5 — The target tenant must exist (S-5)

As a platform admin, I want a command for a tenant that does not exist to fail clearly and write nothing, so that a mistyped tenant id never creates an orphan trial, override or audit record.

**Gating feature key:** none.

```gherkin
Feature: The target tenant is checked before any write

  Background:
    Given the caller is a platform admin with sub "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59" and no tenant_id
    And no tenant "9d4b7e21-6c3f-4a58-b0e2-5f1a8c7d3b94" exists
    And tenant A "7c9e6679-7425-40de-944b-e07fc1f90ae7" exists

  Scenario Outline: An unknown tenant gets 404 and nothing is written
    When the admin sends <request> for tenant "9d4b7e21-6c3f-4a58-b0e2-5f1a8c7d3b94"
    Then the response is 404 with code "entitlements.tenant_not_found" and a generic ProblemDetails
    And no TrialGrant, no FeatureOverride and no audit record exists for that tenant id
    And no row of any other tenant changes

    Examples:
      | request                                                            |
      | the POST trial request                                             |
      | the PUT override request with a valid body for "forecasting.scenarios" |
      | the DELETE override request for "forecasting.scenarios"            |

  Scenario: Validation errors come before the existence check
    When the admin sends a PUT for the unknown tenant with an empty reason
    Then the response is 400 with code "entitlements.reason_invalid", not 404

  Scenario: An existing tenant passes the check
    When the admin sends the POST trial request for tenant A
    Then the response is 204

  Scenario: The check runs inside the command, so a direct call is covered too
    Given the Entitlements command is executed directly for the unknown tenant
    Then the result is "entitlements.tenant_not_found" with no row written

  Scenario: The check reads only whether the tenant exists
    When the existence check runs for tenant A
    Then it returns a yes or no, no tenant data, and only for the one tenant id asked
    And the check is not an unfiltered read of the tenants table by a handler that is not attributed [AllowCrossTenant]

  Scenario: A tenant that exists but another tenant's id is guessed
    When the admin sends the POST trial request for tenant B's id while tenant B exists
    Then the response is 204 for tenant B only, and tenant A has no trial

  Scenario: A database error in the existence check is a 500, never "not found" and never "found"
    Given the existence check throws
    When the admin sends any admin request
    Then the response is 500 with a generic body
    And no entitlement row and no audit record is written
```

### Story 6 — The dev-admin user can call the admin endpoints in a local run

As a platform admin (and as Marco running the stack locally), I want the seeded dev-admin account to hold the admin role in its access token, so that I can exercise the admin endpoints locally without editing Keycloak by hand.

**Gating feature key:** none.

```gherkin
Feature: The seeded dev-admin is a working platform admin

  Scenario: The realm export defines the role and the seeded admin
    When deploy/keycloak/decisya-realm.json is inspected
    Then the realm role "platform-admin" exists
    And exactly one seeded user holds it, "dev-admin", with no tenant_id attribute
    And no seeded tenant user holds it

  Scenario: dev-admin's access token carries the admin role and no tenant
    Given the realm is imported into a real Keycloak
    When dev-admin completes the hosted login through the BFF client
    Then the access token carries the role "platform-admin"
    And the access token carries no tenant_id claim
    And dev-alice's and dev-bob's access tokens do not carry the role

  Scenario: dev-admin can reach an admin endpoint through the BFF
    Given dev-admin has a BFF session and has fetched the antiforgery pair
    And dev-alice has signed in once, so her tenant exists
    When the SPA-style client sends the POST trial request for dev-alice's tenant through the BFF
    Then the response is 204 and an audit record names dev-admin's sub as the actor

  Scenario: dev-alice gets 403 through the same path
    Given dev-alice has a BFF session and the antiforgery pair
    When she sends the same request
    Then the response is 403 and no trial exists

  Scenario: The password and secrets stay outside the export
    When the realm file is inspected
    Then dev-admin's password is the existing placeholder and no secret or real credential is added
    And the local-run documentation (GETTING-STARTED) names dev-admin as the way to call the admin endpoints, with the Visual Studio 2026 path and the CLI path side by side
```

### Story 7 — The admin module follows the platform rules (every call audited by construction)

As the platform operator, I want the admin endpoints to live in the Admin module, call the audited commands only, and add no second way to mint a tenant scope, so that a later admin endpoint cannot skip the audit or the role check.

```gherkin
Feature: Module shape and guards (C-4 handler scope, ADR-0012 point 4)

  Scenario: Endpoints call the audited commands only
    When the Admin module's endpoint code is inspected
    Then each endpoint invokes one of the three Entitlements commands through the module's public surface
    And no endpoint opens a database context, writes an audit record itself or reads a table

  Scenario: Reachability of the commands is relaxed only as far as needed
    When the G4-23-02 architecture tests run
    Then each rule that changes names the reason (the audit exists, #24; the admin endpoints exist, #25), and the Entitlements module still references no ASP.NET Core assembly
    And [AllowCrossTenant] types remain exactly the audited handlers and each still calls the audit writer (CrossTenantAuditRule passes)

  Scenario: Only CallerContextMiddleware mints a tenant resolution in Decisya.Api
    When ApiBoundaryTests runs over the Api and Admin assemblies
    Then no type other than CallerContextMiddleware calls TenantResolution.For, FromClaim or NoTenant

  Scenario: The Admin module follows the standard module shape
    When the Admin module is inspected
    Then it has an Admin and an Admin.Contracts assembly, or a documented reason for not having one (G2 decides), and its contracts reference only SharedKernel and other Contracts assemblies
    And it persists nothing itself (no DbContext, no schema, no role)
    And every instant it handles is a NodaTime Instant, and it never reads the system clock directly

  Scenario: Request telemetry names the admin calls without sensitive data
    When any admin call succeeds or is refused
    Then one structured log line and a counter record the action, the outcome and the trace id, with the hashed user id and the target tenant id, and no reason text
```

---

## Non-functional requirements

Two rows are added to `docs/requirements/nfr.md` (ISO/IEC 25010 categories): NFR-38, NFR-39.

| Id | Category | Target (summary) |
| --- | --- | --- |
| NFR-38 | Security | 100% of `/api/admin/**` requests from a non-admin (tenant user, no-tenant non-admin, malformed claim, admin with `tenant_id`, anonymous) are refused with 403 (401 anonymous) and a generic body, with zero database commands and zero rows written. The role is read only from the validated access token |
| NFR-39 | Security | 100% of 2xx admin responses are matched by exactly one audit record (actor = the token `sub`, the target tenant, the action, the feature key where relevant), and 100% of non-2xx responses by none |

## Risks carried to G2 and G3

- **R-1 Role in the access token.** Today the role reaches only the ID token. Moving or adding the claim changes the realm export and the token the whole API sees (all callers). G3 should check that no tenant user can obtain the role by any user-profile or self-service path, and that the claim shape cannot be forged through a user attribute (Keycloak user attributes already feed `tenant_id`).
- **R-2 Dual claim.** Q2's answer decides whether `Tenant && admin` is refused. The role is the single most powerful grant in the system, so the refusal must be tested at both the policy and the handler.
- **R-3 Existence check crosses the Tenancy boundary.** Entitlements has no dependency on Tenancy (#23 design). The check needs a Tenancy contract (or equivalent) and a read of `tenancy.tenants` that the Entitlements role cannot make (cross-schema isolation proof from #23 and #24 must still hold). G2 decides where it lives. It must return only existence, and it is a cross-tenant read that falls under invariant 1 (an `[AllowCrossTenant]`, audited path, or a design that avoids it).
- **R-4 Check timing.** The existence check and the write are two steps. A tenant cannot be deleted in phase 0, so the window is harmless now. A later tenant-deletion feature must revisit it.
- **R-5 JIT-only tenants.** An admin cannot act for a tenant that has never signed in (Q3 note).
- **R-6 Refusals unaudited.** If Q4's recommendation stands, a mistyped or probing admin call leaves only logs. No audit reader exists either (deferred, #83), so the log is not yet useful to an investigator without database access (#24 R-5).
- **R-7 Free-text reason.** The `reason` crosses the HTTP boundary for the first time (#23 R-3). The request logging middleware, the BFF's YARP forwarding logs and exception handling must not capture request bodies.
- **R-8 Antiforgery coverage.** The BFF applies antiforgery to state-changing `/api` verbs generically (#19, B-3). The PUT and DELETE routes must be inside that match, or an admin could be cross-site tricked into a grant.

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #25 -->

Notes: all four questions are answered. The note is the risks R-1 to R-8 above, carried to G2 and G3, chiefly the role claim missing from the access token (R-1), the existence check crossing the Tenancy boundary (R-3) and refusals being unaudited (R-6, accepted by Marco).

## Traceability

Test names are `Project: Class.Method`. Short names for the projects (all under `tests/`):

| Short | Project |
| --- | --- |
| Api | `Decisya.Api.Tests` (classes in `Authentication/` unless the path is given) |
| Admin | `Modules/Decisya.Modules.Admin.Tests` |
| Ent | `Modules/Decisya.Modules.Entitlements.Tests` |
| Ten | `Modules/Decisya.Modules.Tenancy.Tests` |
| Arch | `Decisya.ArchitectureTests` |
| Bff | `Decisya.Bff.Tests` |
| Id | `Decisya.Identity.Tests` |
| SK | `Decisya.SharedKernel.Tests` |

Lanes: Unit and Architecture run with no Docker (the unit step of CI). Integration needs Docker (Postgres 18 and Keycloak through Testcontainers). The "real stack" tests in Api use the real Api host, the real handlers, the real Audit writer and a fully migrated Postgres; the API connects as the database owner there, and the least-privilege roles are proven by the module and Migrator tests (#23, #24).

### Story 1 - Only a platform admin can reach the admin endpoints

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A caller who is not a platform admin gets 403, a generic body and no side effect | Api: `AdminAuthorizationTests.Every_non_admin_gets_the_generic_403_on_every_verb_and_never_reaches_the_commands` (8 caller shapes: tenant user, role plus `tenant_id`, role only in `realm_access`, in `groups`, wrong case, `-x` suffix, no role, non-string element next to the role; body members exactly `type,title,status,traceId`, `no-store`, placeholder database) and `AdminAuthorizationTests.A_non_admin_gets_the_identical_403_for_every_kind_of_input`, `Every_403_source_has_the_identical_shape`; `ForbiddenResponseTests.A_valid_token_without_a_sub_claim_gives_a_generic_403_problem_details_with_no_challenge_header`, `A_blank_sub_gives_a_generic_403_problem_details`, `Two_tenant_id_claims_give_a_generic_403_problem_details_with_no_claim_value`, `A_blank_tenant_id_gives_a_generic_403_problem_details`, `Two_sub_claims_on_a_principal_make_CallerIdentity_return_null`; `TenantClaimForbiddenTests.A_malformed_tenant_id_claim_gives_403_before_routing_with_no_database_access`; Admin: `AdminEndpointTests.A_caller_who_is_not_a_platform_admin_gets_403_and_no_command_runs_whatever_the_input`; Api (real Keycloak token): `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403`. "No database command" is proved by the unreachable placeholder database (a command would turn the 403 into a 500) | Unit, Integration |
| 1 | A platform admin passes the role check | Api: `AdminAuthorizationTests.A_platform_admin_with_no_tenant_passes_the_policy_and_gets_204`, `A_role_array_with_tenant_user_and_platform_admin_and_no_tenant_is_an_admin`; Admin: `AdminEndpointTests.A_platform_admin_passes_the_role_check`; Api: `AdminEndpointsPostgresTests.Starting_a_trial_succeeds_and_is_audited`, `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403` | Unit, Integration |
| 1 | The role is taken only from the validated access token | Api: `AdminAuthorizationTests.A_forged_admin_header_query_or_cookie_never_grants_the_role`; `CallerIdentityRolesTests.The_value_under_any_other_claim_type_is_ignored`, `The_role_only_inside_realm_access_is_ignored`, `The_exact_role_value_is_an_admin`, `A_multi_value_array_with_the_role_is_an_admin`, `A_missing_roles_claim_is_not_an_admin_and_the_identity_is_still_valid`, `A_non_string_roles_claim_next_to_a_string_admin_value_makes_the_set_ambiguous`, `Odd_roles_never_make_the_identity_null`, `The_role_is_read_independently_of_the_tenant_claim`; the BFF ID token is never an input (the API validates the access token only): `ApiKeycloakAuthenticationTests.An_id_token_is_rejected` | Unit, Integration |
| 1 | An anonymous or invalid-token call gets 401, never 403 or a redirect | Api: `AdminAuthorizationTests.An_anonymous_call_is_the_bare_401_challenge`, `An_anonymous_call_to_an_unknown_admin_path_is_a_401`; `AdminTokenRejectionTests.An_invalid_admin_token_gets_the_bare_401_on_every_verb_and_no_command_runs` (expired, wrong audience, wrong issuer, `alg=none`, HS256, unknown key, `typ` ID; each on the three verbs); `AdminUnmatchedRouteTests.An_anonymous_caller_still_gets_401_on_an_unknown_path_and_a_wrong_verb`; Bff: `ApiAntiforgeryTests.An_anonymous_GET_to_api_gets_401_never_a_redirect`; Admin: `AdminEndpointTests.An_anonymous_call_gets_401_on_every_verb_and_no_command_runs` | Unit, Integration |
| 1 | IsPlatformAdmin fails closed | SK: `ICurrentCallerShapeTests.An_implementation_that_only_supplies_UserId_is_not_a_platform_admin`, `IsPlatformAdmin_is_a_default_interface_member_not_an_abstract_one`, `ICurrentCaller_declares_exactly_two_members_read_only_UserId_and_IsPlatformAdmin`; Api: `CallerContextTests.Before_Set_IsPlatformAdmin_is_false_and_does_not_throw`, `Set_never_makes_a_tenant_caller_a_platform_admin`, `Set_assigns_IsPlatformAdmin_for_a_tenant_less_caller`, `A_second_Set_cannot_flip_IsPlatformAdmin`, `IsPlatformAdmin_has_no_public_or_internal_setter`; the test double defaults to false (`TestCurrentCaller.IsPlatformAdmin`) | Unit |
| 1 | The handler refuses a non-admin even if the endpoint policy is bypassed | Ent: `EntitlementsAdminPreconditionTests.A_tenant_less_caller_without_the_platform_admin_flag_gets_forbidden_with_zero_database_commands`, `A_Tenant_caller_gets_forbidden_even_with_the_platform_admin_flag_and_zero_database_commands`, `An_Invalid_ambient_gets_forbidden_whatever_the_platform_admin_flag`, `A_refused_non_admin_call_learns_no_validation_code`, `A_non_admin_tenant_less_caller_writes_nothing_on_a_real_database`; `EntitlementsForbiddenTests.A_non_admin_ambient_gets_forbidden_from_every_command_before_validation_and_with_no_database_access`, `A_non_admin_ambient_writes_no_row_for_any_command_against_any_tenant`; Api: `AdminAuthorizationTests.A_facade_Forbidden_and_the_policy_403_have_the_identical_body_and_no_code`. The only proceeding combination (`None` and admin) is every success test in Ent and Api | Unit, Integration |
| 1 | Every admin route carries the policy and the explicit no-entitlement marker | Policy half: Api: `EndpointAuthorizationTests.The_admin_routes_are_exactly_the_three_and_each_requires_the_policy_and_none_is_anonymous`; Admin: `AdminEndpointTests.The_admin_policy_applies_to_routes_added_later_under_the_same_prefix`. Marker half: **manual / deferred**: the ADR-0008 marker type and its endpoint-metadata test do not exist yet; the first gated endpoint (#26) adds both and puts the marker on the `/api/admin` group (G2 D8, to #83). The policy assertion stands in | Unit |
| 1 | The admin policy applies to routes added later under the same prefix | Api: `EndpointAuthorizationTests.The_admin_routes_are_exactly_the_three_and_each_requires_the_policy_and_none_is_anonymous` (a route added later changes the set and fails it), `Every_membership_opt_out_other_than_api_whoami_requires_the_admin_policy`, `The_tenant_membership_opt_out_set_is_exactly_api_whoami_and_the_three_admin_routes`; `HealthEndpointTests` (production endpoint set); Admin: `AdminEndpointTests.The_admin_policy_applies_to_routes_added_later_under_the_same_prefix` | Unit |

### Story 2 - Start a trial

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 2 | Starting a trial succeeds and is audited | Api: `AdminEndpointsPostgresTests.Starting_a_trial_succeeds_and_is_audited` (204, empty body, `no-store`, a Pro `TrialGrant` of exactly 14 days, exactly one audit record with the action, outcome, actor `sub` and a null feature key); `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403` (actor is dev-admin's real `sub`). The exact `StartsAt`/`EndsAt` instants against a fixed clock: Ent: `EntitlementsTrialTests.Starting_a_trial_grants_Pro_features_for_exactly_14_days`, `EntitlementsAuditTests` | Integration |
| 2 | The tenant then sees Pro features | Ent: `EntitlementsTrialTests.Starting_a_trial_grants_Pro_features_for_exactly_14_days`, `A_trial_does_not_affect_another_tenant`, `A_trial_is_active_one_tick_before_EndsAt_and_denied_at_EndsAt` (evaluation is the #23 service; no HTTP endpoint evaluates a feature yet) | Integration |
| 2 | A second start is refused with 409 and writes nothing | Api: `AdminEndpointsPostgresTests.A_second_start_is_refused_with_409_and_writes_nothing`; Ent: `EntitlementsTrialTests.A_tenant_cannot_start_a_second_trial_even_after_the_first_has_expired`; Admin: `AdminEndpointTests.Each_failure_status_of_the_facade_maps_to_its_http_status_with_a_code_only_where_the_contract_says` | Integration, Unit |
| 2 | A malformed tenant id in the path gets 400 | Api: `AdminEndpointsPostgresTests.A_malformed_tenant_id_in_the_path_gets_400` (`not-a-guid` and the all-zero GUID, both `tenant_invalid`, no row, no record); `AdminRequestValidationTests.A_bad_path_or_body_gets_400_with_a_stable_code_or_a_generic_body_before_any_command_runs`; Admin: `AdminEndpointTests.A_malformed_tenant_id_in_the_path_gets_400_with_tenant_invalid_on_every_verb`. Reconciled: both rows give `tenant_invalid` (G2 reconciliation) | Integration, Unit |
| 2 | A request body on the trial endpoint is ignored or refused, never stored | Api: `AdminEndpointsPostgresTests.A_request_body_on_the_trial_endpoint_is_ignored_never_stored` (a body naming tenant B: only the path tenant gets a trial); Admin: `AdminEndpointTests.A_request_body_on_the_trial_and_revoke_endpoints_is_ignored_never_read`. Reconciled: ignored | Integration, Unit |
| 2 | A state-changing call through the BFF needs the antiforgery token | Bff: `AdminApiAntiforgeryTests.An_admin_call_without_the_antiforgery_header_is_refused_and_the_api_receives_nothing`, `An_admin_call_with_another_sessions_antiforgery_pair_is_refused_and_the_api_receives_nothing`, `An_admin_call_with_a_mismatched_antiforgery_header_is_refused`, `An_admin_call_with_a_valid_pair_is_forwarded_with_the_bearer_token_and_no_cookie_and_no_antiforgery_header` (each on POST, PUT and DELETE against `ApiDouble`); the generic behaviour and the 4xx pass-through and 5xx replacement are #19's `ApiAntiforgeryTests` and `ApiResponseTests` | Integration |

### Story 3 - Grant or replace an override

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 3 | Granting an override succeeds and is audited (the Done-when) | Api: `AdminEndpointsPostgresTests.Granting_an_override_succeeds_and_is_audited_the_Done_when` (row with an expiry, tenant B has none, exactly one record with action, key, outcome and actor), `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403`; Ent: `EntitlementsOverrideTests`, `EntitlementsAuditTests` (evaluation and exact instants) | Integration |
| 3 | An override without an expiry lasts until revoked | Api: `AdminEndpointsPostgresTests.An_override_without_an_expiry_lasts_until_revoked`; Admin: `AdminRequestBodyTests.An_override_without_an_expiry_lasts_until_revoked_so_the_command_gets_no_expiry` | Integration, Unit |
| 3 | The same PUT again replaces the override and writes a further record | Api: `AdminEndpointsPostgresTests.The_same_PUT_again_replaces_the_override_and_writes_a_further_record` (one row with the new reason, two grant records); Ent: `GrantOverrideRaceRecoveryTests` | Integration |
| 3 | A request the command rejects gets 400 with a stable code and writes nothing | Api: `AdminEndpointsPostgresTests.A_request_the_command_rejects_gets_400_with_a_stable_code_and_writes_nothing` (5 rows: unknown key, empty, blank and 501-character reason, past expiry); Ent: `EntitlementsForbiddenTests.Validation_fails_before_any_database_access`. The row `expiresAt` equal to now is the #23 boundary test `EntitlementsOverrideTests` (fake clock; the HTTP host uses the real clock) | Integration, Unit |
| 3 | A body that fails schema validation gets 400 before any command runs | Admin: `AdminRequestBodyTests.A_body_that_fails_schema_validation_gets_400_with_a_generic_ProblemDetails_before_any_command_runs` (18 rows: no body, invalid JSON, array, string, number or null or object for `reason`, missing `reason`, unknown member, `tenantId` in the body, duplicate members, `expiresAt` as a number, not a date, without `Z`, with an offset, depth over 4, `text/plain`, form content), `A_missing_Content_Type_gets_400`; Api: `AdminRequestValidationTests.A_bad_path_or_body_gets_400_with_a_stable_code_or_a_generic_body_before_any_command_runs`, `A_Content_Type_other_than_application_json_gets_400_before_any_command_runs`. "No database command" holds because the facade is never reached (stub call count 0) | Unit |
| 3 | The reason is never echoed or logged | Api: `AdminRequestValidationTests.The_reason_is_never_echoed_or_logged_in_a_response_a_log_line_a_metric_tag_or_an_activity_tag` (the real host, Trace, every category, activity and metric tags); `AdminEndpointsPostgresTests.The_reason_is_never_echoed_or_logged` (real Postgres and EF Core and Npgsql logging at Trace, a forced check-constraint failure giving a 500, and every audit column); Admin: `AdminRequestBodyTests.The_reason_is_never_echoed_or_logged`, `A_parse_error_is_not_logged_as_an_exception_and_only_the_fixed_rejection_line_is_written`; Ent: `EntitlementsReasonConfidentialityTests`, `EntitlementsAdminPreconditionTests.GrantOverride_ToString_and_PrintMembers_never_render_the_reason`; Admin: `AdminModuleBoundaryTests.OverrideGrantRequest_ToString_never_renders_the_reason` | Unit, Integration |
| 3 | A feature key in the path is matched exactly | Admin: `AdminEndpointTests.A_feature_key_in_the_path_is_matched_exactly_and_a_malformed_one_gets_400_with_feature_unknown` (`Forecasting.Scenarios`, `forecasting.scenarios%20`, `a.b.c`, `nosuch.`, `Nosuch`, on PUT and DELETE); Api: `AdminRequestValidationTests.A_bad_path_or_body_gets_400_with_a_stable_code_or_a_generic_body_before_any_command_runs` | Unit |

### Story 4 - Revoke an override

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 4 | Revoking an override succeeds and is audited | Api: `AdminEndpointsPostgresTests.Revoking_an_override_succeeds_and_is_audited`; Ent: `EntitlementsOverrideTests` | Integration |
| 4 | Revoking a feature with no override is idempotent and still audited | Api: `AdminEndpointsPostgresTests.Revoking_a_feature_with_no_override_is_idempotent_and_still_audited` | Integration |
| 4 | Revoking for tenant A never touches tenant B's override | Api: `AdminEndpointsPostgresTests.Revoking_for_tenant_A_never_touches_tenant_Bs_override`; Ent: `EntitlementsIsolationTests` | Integration |
| 4 | A well-formed key that the catalog does not list is accepted and audited | Api: `AdminEndpointsPostgresTests.A_well_formed_key_that_the_catalog_does_not_list_is_accepted_and_audited`; Admin: `AdminEndpointTests.A_well_formed_key_that_the_catalog_does_not_list_is_passed_to_the_command_on_DELETE` | Integration, Unit |
| 4 | A malformed key gets 400 and writes nothing | Api: `AdminEndpointsPostgresTests.A_malformed_key_gets_400_and_writes_nothing` (7 rows, including the 65-character key), `AdminRequestValidationTests`; Admin: `AdminEndpointTests.A_feature_key_in_the_path_is_matched_exactly_and_a_malformed_one_gets_400_with_feature_unknown`. Reconciled: the G1 example `nosuch.feature` is well-formed (row above); malformed keys are the 400 rows | Integration, Unit |
| 4 | A revoke for a key later removed from the catalog still works | Ent: `EntitlementsOverrideTests.Revoking_an_override_for_a_key_later_removed_from_the_catalog_still_works` (the #23 behaviour; the HTTP mapping is the well-formed-key row above) | Integration |

### Story 5 - The target tenant must exist

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 5 | An unknown tenant gets 404 and nothing is written | Api: `AdminEndpointsPostgresTests.An_unknown_tenant_gets_404_and_nothing_is_written` (POST, PUT, DELETE: 404, `tenant_not_found`, body members `type,title,status,traceId,code`, no trial, override or record anywhere); Ent: `EntitlementsAdminPreconditionTests.If_the_existence_check_throws_or_returns_false_nothing_is_written_and_no_database_command_runs` (the 3 x 2 fault-matrix rows) | Integration |
| 5 | Validation errors come before the existence check | Api: `AdminEndpointsPostgresTests.Validation_errors_come_before_the_existence_check`; Ent: `EntitlementsAdminPreconditionTests.Validation_comes_before_the_existence_check` | Integration, Unit |
| 5 | An existing tenant passes the check | Api: `AdminEndpointsPostgresTests.An_existing_tenant_passes_the_check`; Ent: `EntitlementsAdminPreconditionTests.An_existing_target_tenant_still_succeeds_and_writes_exactly_one_audit_record` | Integration |
| 5 | The check runs inside the command, so a direct call is covered too | Ent: `EntitlementsAdminPreconditionTests.A_missing_target_tenant_gives_tenant_not_found_before_any_transaction` (a direct handler call on the unreachable host: a returned Result proves no transaction was opened) | Unit |
| 5 | The check reads only whether the tenant exists | Ten: `TenantExistenceTests.An_existing_tenant_gives_true`, `A_missing_tenant_gives_false`, `Under_the_scope_of_tenant_A_tenant_Bs_row_is_never_seen` (one filtered query per call), `A_scope_of_kind_None_throws_before_any_SQL`, `A_scope_of_kind_Invalid_throws_before_any_SQL`, `A_database_failure_propagates_and_is_never_turned_into_an_answer`; `TenancyModuleBoundaryTests.ITenantExistence_has_exactly_one_method_ExistsAsync_of_TenantResolution_and_CancellationToken_returning_Task_of_bool`, `TenantExistence_is_internal_unattributed_and_registered_scoped_as_ITenantExistence`, `Tenancy_declares_no_AllowCrossTenant_type`; Arch: `ModuleBoundaryTests.CrossTenantQueryRule_passes_over_the_current_architecture_scope`, `AdminMintsNothingTests` | Integration, Architecture |
| 5 | A tenant that exists but another tenant's id is guessed | Api: `AdminEndpointsPostgresTests.A_tenant_that_exists_but_another_tenants_id_is_guessed` | Integration |
| 5 | A database error in the existence check is a 500, never "not found" and never "found" | Api: `AdminEndpointsPostgresTests.A_database_error_in_the_existence_check_is_a_500_never_not_found_and_never_found` (real host, real database, a throwing `ITenantExistence`; nothing written); Ent: `EntitlementsAdminPreconditionTests.A_failing_existence_check_propagates_and_no_transaction_is_opened`; Ten: `TenantExistenceTests.A_database_failure_propagates_and_is_never_turned_into_an_answer` | Integration, Unit |

### Story 6 - dev-admin in a local run

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 6 | The realm export defines the role and the seeded admin | Id: `RealmExportFileTests.The_platform_admin_user_carries_no_tenant_id_attribute` (the `Single` over holders of the role proves exactly one), `RealmExportFileTests.The_decisya_bff_client_has_the_flat_roles_access_token_mapper_with_exact_flags`, `The_realm_roles_id_token_mapper_is_unchanged_and_stays_out_of_the_access_token`, `Only_the_two_realm_role_mappers_write_the_roles_claim`; `RealmConfigurationTests.At_least_two_enabled_tenant_users_have_distinct_tenant_ids_and_one_platform_admin_has_none`, `The_realm_roles_access_token_mapper_puts_a_flat_roles_claim_in_the_access_token_only` | Unit, Integration |
| 6 | dev-admin's access token carries the admin role and no tenant | Id: `BffLoginFlowTests.A_platform_admin_login_carries_no_tenant_id_claim_in_either_token` (roles exactly `platform-admin`), `BffLoginFlowTests.A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set` (dev-alice's `roles` has no `platform-admin`); Api: `ApiKeycloakAuthenticationTests.Dev_alice_and_dev_admin_access_tokens_authenticate_with_the_expected_claims` | Integration |
| 6 | dev-admin can reach an admin endpoint through the BFF | Proven in two halves (G2): the BFF forwards the admin verbs with the bearer token: Bff: `AdminApiAntiforgeryTests.An_admin_call_with_a_valid_pair_is_forwarded_with_the_bearer_token_and_no_cookie_and_no_antiforgery_header` (logged in as dev-admin); the API accepts dev-admin's real token and audits with the admin's `sub`: Api: `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403`. The single end-to-end click-through (BFF process to API process in one run) is **manual**: it needs the full AppHost, Marco's browser session and the realm refresh; the GETTING-STARTED smoke row is the procedure | Integration, manual |
| 6 | dev-alice gets 403 through the same path | Api: `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403` (403 on all three verbs with her real token, no trial, no record); BFF half: Bff: `AdminApiAntiforgeryTests` (the BFF forwards whatever a valid session sends; the API decides). End-to-end through the BFF is **manual** as above | Integration, manual |
| 6 | The password and secrets stay outside the export | Id: `RealmSecretRulesTests`, `RealmGuardTests`, `PlaceholderSubstitutionRegressionTests` (no secret in the export, placeholders only); Api: `GettingStartedAdminDocTests.No_kcadm_or_curl_command_in_the_guide_carries_a_password_or_a_token_on_the_command_line`, `The_guide_names_the_mapper_its_target_the_reset_cost_and_the_smoke_check`, `The_guide_gives_the_Visual_Studio_2026_path_and_the_CLI_path_side_by_side_for_the_mapper_steps` | Unit |

### Story 7 - Module shape and guards

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 7 | Endpoints call the audited commands only | Admin: `AdminModuleBoundaryTests.The_endpoints_call_only_IEntitlementAdminCommands_among_the_other_modules`, `No_Admin_type_declares_a_database_transaction_or_audit_write_of_its_own`, `The_Admin_module_references_no_persistence_no_EF_Core_no_Npgsql_no_Audit_and_no_Tenancy_assembly`, `Among_the_Decisya_assemblies_the_Admin_module_references_exactly_its_Contracts_Entitlements_Contracts_and_SharedKernel`; Arch: `AdminReachabilityTests.Only_Admin_and_Entitlements_depend_on_the_Entitlements_admin_contracts_namespace`, `The_two_allowed_assemblies_do_depend_on_the_namespace_so_the_rule_is_not_vacuous`; Ent: `EntitlementsModuleBoundaryTests.EntitlementAdminCommands_is_internal_unattributed_scoped_and_takes_exactly_the_three_handlers`, `The_facade_maps_every_error_category_and_a_Failure_becomes_an_InvalidOperationException`, `The_Contracts_Admin_namespace_exports_exactly_the_facade_the_result_the_status_and_the_codes`, `EntitlementAdminStatus_has_no_zero_value_and_Failed_rejects_Succeeded_and_undefined_values` | Architecture, Unit |
| 7 | Reachability of the commands is relaxed only as far as needed | Ent: `EntitlementsModuleBoundaryTests.No_public_member_of_the_Contracts_has_type_TenantResolution_and_none_outside_the_Admin_namespace_has_type_TenantId`, `The_Entitlements_module_references_no_ASP_NET_Core_and_no_Wolverine_assembly`, `The_AllowCrossTenant_types_are_exactly_the_three_admin_handlers_and_none_is_public`, `The_commands_handlers_and_TargetTenant_are_internal_and_not_in_Contracts`, `Decisya_Modules_Entitlements_references_Tenancy_Contracts_and_not_the_Tenancy_implementation`, `Each_handler_takes_ITenantExistence_after_the_four_fixed_parameters`, `InternalsVisibleTo_is_exactly_the_module_test_assembly`; Arch: `ModuleBoundaryTests.CrossTenantAuditRule_passes_over_the_current_architecture_scope`, `AuditBypassUsageTests.The_three_Entitlements_handlers_and_AuditWriter_reference_only_TenantResolution_For_from_the_bypass_list` | Architecture |
| 7 | Only CallerContextMiddleware mints a tenant resolution in Decisya.Api | Api: `Architecture/ApiBoundaryTests.Only_CallerContextMiddleware_mints_a_tenant_resolution_in_Decisya_Api` (unchanged); Arch: `AdminMintsNothingTests.No_Admin_type_references_TenantResolution_For_FromClaim_or_NoTenant`, `No_Admin_type_carries_AllowCrossTenant`; Admin is in `ArchitectureScope`, so `CrossTenantQueryRule` covers it | Architecture |
| 7 | The Admin module follows the standard module shape | Admin: `AdminModuleBoundaryTests.The_Admin_module_persists_nothing_it_has_no_DbContext_no_Domain_and_no_Infrastructure_namespace`, `The_Admin_module_references_no_other_modules_implementation`, `The_request_DTO_and_the_Contracts_assembly_reference_only_SharedKernel`, `The_Admin_module_never_reads_the_system_clock_directly`, `No_Admin_member_or_parameter_is_a_DateTime_DateTimeOffset_or_TimeZoneInfo`, `Only_Endpoints_and_AdminModule_depend_on_AspNetCore`, `No_Admin_type_depends_on_DbConnection_or_DbTransaction`, `InternalsVisibleTo_is_exactly_the_module_test_assembly`, `The_Admin_module_declares_no_AllowCrossTenant_type`; Api: `ApiBoundaryTests.Decisya_Api_csproj_has_exactly_the_ServiceDefaults_Tenancy_Entitlements_Audit_and_Admin_ProjectReferences_and_only_the_JwtBearer_package` | Architecture |
| 7 | Request telemetry names the admin calls without sensitive data | Admin: `AdminRequestBodyTests.Request_telemetry_names_the_action_and_outcome_and_the_target_tenant_but_no_reason` (counter tags `decisya.admin.action` and `decisya.admin.outcome`, one Information line with the action, outcome and target tenant); Api: `AdminAuthorizationTests.An_admin_role_with_a_tenant_id_logs_a_Warning_without_any_claim_value`; the hashed user id, `trace_id` and `tenant_id` enrichment is the #21 enrichment (`TenancyPipelineIntegrationTests.Interleaved_requests_from_two_tenants_never_cross_and_every_log_pairing_stays_consistent`), visible in the logs of the real-stack tests | Unit, Integration |

### Non-functional requirements

| NFR | Test(s) | Lane |
| --- | --- | --- |
| NFR-38 (100% of non-admin `/api/admin/**` requests refused, generic body, zero database commands, zero rows) | Api: `AdminAuthorizationTests.Every_non_admin_gets_the_generic_403_on_every_verb_and_never_reaches_the_commands`, `A_non_admin_gets_the_identical_403_for_every_kind_of_input`, `An_anonymous_call_is_the_bare_401_challenge`, `AdminTokenRejectionTests.An_invalid_admin_token_gets_the_bare_401_on_every_verb_and_no_command_runs`, `AdminKeycloakTests.Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403` (no trial, override or record after dev-alice's calls on a real database); Admin: `AdminEndpointTests.A_caller_who_is_not_a_platform_admin_gets_403_and_no_command_runs_whatever_the_input`. **Scope, per G3 S-3:** NFR-38 is read as "the three admin routes". An authenticated non-admin gets 404 on an unknown `/api/admin/x` and 405 on a wrong verb (after the #21 membership gate for a tenant caller), pinned by `AdminUnmatchedRouteTests` and `AdminUnmatchedRouteTenantCallerTests`. The PR body states it | Unit, Integration |
| NFR-39 (100% of 2xx admin responses have exactly one audit record, 100% of non-2xx have none) | Api: `AdminEndpointsPostgresTests.Every_2xx_admin_response_has_exactly_one_audit_record_and_every_non_2xx_none` (12 calls: 5 successes, 7 refusals; 5 records, all with the admin's `sub`), `Starting_a_trial_succeeds_and_is_audited`, `Granting_an_override_succeeds_and_is_audited_the_Done_when`, `Revoking_an_override_succeeds_and_is_audited`, and the "writes nothing" rows above; Ent: `EntitlementsAuditAtomicityTests`, `EntitlementsAdminPreconditionTests.If_the_existence_check_throws_or_returns_false_nothing_is_written_and_no_database_command_runs` | Integration |

### G3 MUSTs and the SHOULD to fix now

| Id | What it demands | Test(s) | Lane |
| --- | --- | --- | --- |
| G4-25-01 | One source and one setter for the admin fact; handler check first | Api: `CallerIdentityRolesTests` (the truth table: exact value, array, case, suffix, other claims, `realm_access`, non-string element, never null), `CallerContextTests.Set_never_makes_a_tenant_caller_a_platform_admin`, `A_second_Set_cannot_flip_IsPlatformAdmin`, `IsPlatformAdmin_has_no_public_or_internal_setter`, `Before_Set_IsPlatformAdmin_is_false_and_does_not_throw`; `AdminAuthorizationTests.Every_non_admin_gets_the_generic_403_on_every_verb_and_never_reaches_the_commands` (dual claim refused at the policy), `A_forged_admin_header_query_or_cookie_never_grants_the_role`; Ent: `EntitlementsAdminPreconditionTests` (the `None`/`Tenant`/`Invalid` by flag matrix with zero database commands; dual claim refused at the handler), `A_refused_non_admin_call_learns_no_validation_code`; SK: `ICurrentCallerShapeTests` | Unit, Integration |
| G4-25-02 | Policy and membership opt-out stay coupled | Api: `EndpointAuthorizationTests.The_admin_routes_are_exactly_the_three_and_each_requires_the_policy_and_none_is_anonymous`, `The_tenant_membership_opt_out_set_is_exactly_api_whoami_and_the_three_admin_routes`, `Every_membership_opt_out_other_than_api_whoami_requires_the_admin_policy`; `AdminAuthorizationTests.Every_non_admin_gets_the_generic_403_on_every_verb_and_never_reaches_the_commands` (zero database commands for a tenant caller); Arch: `AdminReachabilityTests` | Unit, Architecture |
| G4-25-03 | One 403, no oracle | Api: `ProblemDetailsAuthorizationResultHandlerTests` (members exactly `type,title,status,traceId`; no failure reasons; nothing after the response started; success delegated), `AdminAuthorizationTests.Every_403_source_has_the_identical_shape`, `A_non_admin_gets_the_identical_403_for_every_kind_of_input`, `A_facade_Forbidden_and_the_policy_403_have_the_identical_body_and_no_code`; 401 unchanged: `AdminAuthorizationTests.An_anonymous_call_is_the_bare_401_challenge`, `AdminTokenRejectionTests`; `ApiAuthenticationWiringTests.IAuthorizationMiddlewareResultHandler_is_registered_exactly_once_as_ProblemDetailsAuthorizationResultHandler` | Unit |
| G4-25-04 | The reason stays in the body and the one column | Admin: `AdminRequestBodyTests` (8 KiB boundary in all three feature modes, chunked bodies, the missing-Content-Length fallback, strict JSON, non-JSON content type, parse errors not logged, the canary over responses, Trace logs and telemetry); Api: `AdminRequestValidationTests.The_reason_is_never_echoed_or_logged_in_a_response_a_log_line_a_metric_tag_or_an_activity_tag`, `AdminEndpointsPostgresTests.The_reason_is_never_echoed_or_logged` (forced real-database failure, 500, no marker in logs); Ent: `EntitlementsAdminPreconditionTests.GrantOverride_ToString_and_PrintMembers_never_render_the_reason`; Admin: `AdminModuleBoundaryTests.The_strict_serializer_options_disallow_unknown_members_and_duplicates_and_cap_the_depth`; Api: `Architecture/ApiBoundaryTests.No_file_under_src_logs_or_buffers_a_request_body`, `No_file_under_src_enables_Npgsql_error_detail` | Unit, Integration |
| G4-25-05 | CSRF through the BFF, per verb | Bff: `AdminApiAntiforgeryTests` (no header, another session's pair, a mismatched header, and a valid pair forwarded with the bearer token and no cookie or antiforgery header; each on POST, PUT, DELETE); Api: `Architecture/ApiBoundaryTests.No_file_under_src_uses_UseHttpMethodOverride` | Integration, Architecture |
| S-1 | GETTING-STARTED: `kcadm.sh` with the interactive prompt only, target named, reset cost stated, smoke check given | Api: `GettingStartedAdminDocTests` (3 facts) | Unit |

### Reconciliation with G1 (G2's table applied)

| G1 text | Applied reading | Test(s) |
| --- | --- | --- |
| R-1: the role reaches only the ID token | Partly inaccurate; the flat `roles` mapper puts it in the access token and the API reads only that claim | Id: `RealmExportFileTests.The_decisya_bff_client_has_the_flat_roles_access_token_mapper_with_exact_flags`; `CallerIdentityRolesTests.The_role_only_inside_realm_access_is_ignored` |
| Story 4: "DELETE for `nosuch.feature` gives 400" against "a key later removed from the catalog uses the existing #23 behaviour" | Contradictory; Marco confirmed #23: a well-formed unknown key gives 204 and one record; a malformed key gives 400 `feature_unknown` | Api: `AdminEndpointsPostgresTests.A_well_formed_key_that_the_catalog_does_not_list_is_accepted_and_audited`, `A_malformed_key_gets_400_and_writes_nothing` |
| Story 2: `not-a-guid` gives a generic 400, all-zero gives `tenant_invalid` | Both give `tenant_invalid` | Api: `AdminEndpointsPostgresTests.A_malformed_tenant_id_in_the_path_gets_400` |
| Story 2: a body is ignored or refused | Ignored, never read | Api: `AdminEndpointsPostgresTests.A_request_body_on_the_trial_endpoint_is_ignored_never_stored` |
| Story 3: a non-JSON Content-Type gets 400 | 400, not 415 | Admin: `AdminRequestBodyTests.A_body_that_fails_schema_validation_gets_400_with_a_generic_ProblemDetails_before_any_command_runs` (the `text/plain` and form rows) |
| Story 1: each route carries the no-entitlement marker | Deferred with the ADR-0008 test to #26; the `Admin.PlatformAdmin` policy assertion stands in | Api: `EndpointAuthorizationTests.The_admin_routes_are_exactly_the_three_and_each_requires_the_policy_and_none_is_anonymous` |
| Story 1: a dual user gets 403 | Refused twice: the fact is role AND `None`, and the handler refuses `Tenant` | Api: `AdminAuthorizationTests.Every_non_admin_gets_the_generic_403_on_every_verb_and_never_reaches_the_commands`; Ent: `EntitlementsAdminPreconditionTests.A_Tenant_caller_gets_forbidden_even_with_the_platform_admin_flag_and_zero_database_commands` |
| Story 1: zero database commands for every non-admin | Holds because the admin group skips the membership gate | Api: `EndpointAuthorizationTests.Every_membership_opt_out_other_than_api_whoami_requires_the_admin_policy`, `AdminAuthorizationTests` |
| Story 7: Admin and Admin.Contracts | Both exist; Contracts holds the request DTO | Admin: `AdminModuleBoundaryTests.The_request_DTO_and_the_Contracts_assembly_reference_only_SharedKernel` |
| Story 7: ApiBoundaryTests over the Api and Admin assemblies | Admin is in `ArchitectureScope`; `ApiBoundaryTests` keeps covering `Decisya.Api` | Arch: `AdminMintsNothingTests`, `ModuleBoundaryTests.CrossTenantQueryRule_passes_over_the_current_architecture_scope`; Api: `ApiBoundaryTests.Only_CallerContextMiddleware_mints_a_tenant_resolution_in_Decisya_Api` |
| Story 6: dev-admin through the BFF | Two halves plus Marco's manual smoke row | Bff: `AdminApiAntiforgeryTests`; Api: `AdminKeycloakTests`; manual: GETTING-STARTED smoke test |

### Manual items and notes

- **Manual:** the single BFF-to-API click-through for Story 6 (scenarios 3 and 4, end to end); Marco runs the GETTING-STARTED smoke row after the realm refresh. Reason: it needs the AppHost, a browser session and Marco's local Keycloak volume.
- **Deferred, not a gap:** the ADR-0008 no-entitlement marker (#26), the OpenAPI file (#83), auditing of refused calls (Q4, #83), MFA for `platform-admin` (C-5, #29).
- **Test setup changes for the real-stack tests:** `TenancyDatabaseFixture.CreateFullyMigratedDatabaseAsync` (Tenancy, Entitlements and Audit migrated, as `MigrationRunner` does) and an entitlements connection-string override in `ApiTestFactory` and `KeycloakBackedApiFactory`.
- **Observed interaction, not a product defect:** with `AdminKeycloakTests` added and no collection, the full Api Integration lane failed `ApiKeycloakAuthenticationTests.A_refresh_token_is_rejected` and `An_id_token_is_rejected` twice (dev-alice's login form returned 200 instead of 302 after about 90 s), while each class passes alone and the lane passes without the new class. Both Keycloak classes now share `[Collection("KeycloakLogins")]` and the lane passes (58 of 58). Not retried silently; the cause is Keycloak under parallel logins of one user on one container and is worth a backlog note.

<!-- gate: G5 | verdict: PASS | issue: #25 -->
