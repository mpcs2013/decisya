# 0026. Admin path from the application to the identity service: narrow identity operations from Ostium's identity kit, called only by one audited Decisya module

- Status: Proposed
- Date: 2026-11-20
- Deciders: Marco
- Tags: security, module, hosting, supply-chain
- Issue: #333 (conditions COND-02 and COND-03 of the Phase 1 plan; its open questions OQ-12 and OQ-39). Requirements: `docs/requirements/333-admin-path-identity-service.md` (G1, operations OP-1 to OP-15, NFR-70 to NFR-74, notes N-1 and N-2). Architecture note: [`docs/architecture/333-admin-path-identity-service.md`](../architecture/333-admin-path-identity-service.md). Threat delta: `docs/security/threat-models/333-admin-path-identity-service.md` (G3, PASS-WITH-NOTES).
- Revised 2026-11-20, before acceptance: G3's MUSTs M333-01 to M333-05 and fix-now items F333-01 to F333-03 are carried in the text below, with Marco's values of 2026-11-20 for the caps and his answers to Q-1 to Q-3 ("Decided").
- Amends [ADR-0016](0016-phase-0-hosting.md) (amendment 2026-11-20 in that ADR, Proposed with this one: one back-channel route, one fixed address).
- Refines [ADR-0025](0025-authenticators-at-asvs-level-3.md) decision 6 steps 4 and 6 (the operator's reset runs through the path; the console stays break-glass), and applies [ADR-0005](0005-modular-monolith-wolverine.md), [ADR-0012](0012-cross-tenant-admin-commands-target-tenant-scope.md), [ADR-0013](0013-audit-records-in-the-audited-command-transaction.md), [ADR-0019](0019-ostium-identity-packages.md) and [ADR-0024](0024-wolverine-envelope-tenant-and-outbox-storage.md).

## Context and problem statement

Today no Decisya component can act on an identity in the realm, by network or by right:
- The Keycloak admin paths (`/admin`, `/admin/*`, `/realms/master`, `/realms/master/*`) are served only on port 8444, which the NAS firewall admits from the operator's workstation only. Port 8443 refuses them (ADR-0016 R9.6, R9.8 as amended 2026-10-20).
- Only Caddy shares a network with Keycloak, so no container reaches Keycloak except through Caddy. The back-channel route admits the app realm's paths only (ADR-0016 R9.6; `deploy/caddy/Caddyfile`).
- No token or role carries `realm-management`, roles are not composite, and `admin-cli` has direct grants off (review 17, G4-17-06, G4-17-08, G4-17-10).
- Every identity step in Keycloak is an operator step at the workstation (`docs/runbooks/deployable-stack.md`; ADR-0025 decision 6 step 4).

The Phase 1 plan needs Decisya to act on identities itself: delete the identity when a closure is due (AC-12-5, AC-12-9), remove credentials and change the registered email or name when a support case completes (AC-28-6), open a case-bound re-enrolment (AC-25-9), end sessions by audited command (AC-26-3), read credential facts for a case review (AC-28-5), and re-apply identity changes after a restore (AC-18-6). G1 lists fifteen operations, OP-1 to OP-15. Condition COND-02 asks which path Decisya uses, what it takes and when it can be in place, before EP-24 starts (planned 2026-12-27). COND-03 says the planned dates exclude the path and are re-planned once its effort and Ostium's dates are known (by 2026-12-21).

Forces:
- **Security.** Whatever can act on identities can take over accounts: changing a registered email opens the support procedure to whoever holds the new mailbox (R-RECOVERY, ADR-0025). Deleting identities destroys access. The right must be the narrowest that serves the operations, unusable outside them, and every use must be recorded.
- **The Ostium boundary (Marco's standing rule, 2026-11-20; ADR-0019).** Everything in Ostium's identity kit (`Ostium.Keycloak.Realm` and its rules, the Keycloak baseline, `Ostium.Bff`, `Ostium.Jwt`) is requested from Ostium and adopted from its release. A reusable identity mechanism belongs in the shared packages. Decisya keeps only its composition, configuration, realm content and tests.
- **What Keycloak's built-in admin API can narrow.** Its rights are role bundles (`manage-users`, `view-users`, `view-events`). Fine-grained admin permissions (version 2, in upstream 26.x documentation; not verified at the pinned 26.7.5) narrow *whose* accounts an admin may manage, not *which* operation: "manage" covers updating any attribute, required actions, credentials and sessions. A generic user update (`PUT /admin/realms/{realm}/users/{id}`) can rewrite the `tenant_id` attribute, required actions or the email in one body that no proxy can inspect. Keycloak offers no built-in way to open a registration that is bound to a case and not sent by email (AC-25-9).
- **Solo-developer time.** One person builds, runs and supports the path. A new process, a new image pipeline or a second message transport each costs weeks.
- **Dates.** The first operations are needed by EP-26 (2027-03-02, ending sessions) and EP-28 (2027-05-25, completing support cases). Closures (EP-12) start 2027-09-06. Ostium's dates are not known.
- **Wolverine today.** Messages are invoke-only (`MediatorOnly`); durable messaging, scheduled sends and retries arrive with backlog #243 item 231-1, and a nested invoke loses its caller until item 231-5 (ADR-0024 amendments 1 and 2).

## Decision drivers

- NFR-70 to NFR-74: least privilege refused by the identity service itself, one intent record before and one outcome record after every call, tenant scope per call, no effect on the ledger or sign-in when Keycloak is down, no standing broad admin right.
- Fewer rights over more convenience (G1 N-2): an operation that can be met without an admin right is met without one.
- Nothing in Ostium's kit is built in Decisya; every app-owned value is a Decisya option, never a default in shared code.
- The operator's console path on 8444 stays as it is.
- Effort and dates that the re-plan can use.

## Considered options

1. **(a) A path of Decisya's own on Keycloak's built-in admin API.** A confidential client with a service account in the `decisya` realm, holding either `manage-users` and `view-users` (a1) or fine-grained permissions scoped to a group of end users (a2), called by a Decisya-built client through a new Caddy route to `/admin/realms/decisya/users/*`, narrowed by a method and path allow-list at the edge. **Rejected.**
   - a1 grants creating users, listing every user, resetting passwords and editing every attribute: it fails NFR-70 outright.
   - a2 still lets one generic update rewrite `tenant_id`, required actions or the email, and the edge cannot see a request body. "Refused by the identity service" (NFR-70) then holds only for whose accounts, not for which operations.
   - It cannot serve OP-13: Keycloak has no built-in, case-bound registration without email.
   - It moves a reusable mechanism (the admin client) and a realm-guard rule change into Decisya, against ADR-0019 and Marco's rule.
   - It opens `/admin/...` to a container for the first time, which breaks the "admin paths only on 8444" rule rather than amending it.
2. **(b) Narrow identity operations from Ostium's identity kit, called only by one audited Decisya module.** Ostium delivers a Keycloak provider that exposes exactly the operations listed here, each behind its own client role on one service-account client, plus the typed .NET client, the realm rule and the test fixture. Decisya delivers the module that calls it (audit, retries, tenant scope), its realm content, its edge route and its tests. **Chosen.**
3. **(c) Disable the identity and remove its email, name and credentials when deletion is not possible.** Kept as a defined but switched-off operation of (b): (b) can delete, so (c) is not needed. It is switched on only by a dated amendment, after the narrow legal review confirms before the first real user that it is enough and the privacy notice lists what stays (AC-13-5).
4. **(d) A manual operator step in the console, recorded by an audited command.** Allowed only as a stopgap for the invite-only beta, for a closure's identity step only, ending before open sign-up (EP-22's switch; N-1, Marco 2026-11-20). Not a standing procedure.

Two further choices inside (b):
- **Who holds the client.** The Api, inside one module (chosen), or a separate worker process. The worker was rejected: it costs a new image, its scan, signing, a Compose service and a message transport between processes, and it gains little. An attacker who controls the Api can still enqueue commands the worker would run, and, once D-3's edge refusals are in, an exfiltrated secret is useless outside the stack because only the Api's address may reach the operations (decision 8). The client-credentials token endpoint is on the published route, so this holds only with those refusals. That is why the provider is never mounted before them (decision 13, G6-333-01).
- **Where OP-4 (ending sessions) is enforced.** In Decisya's own session store plus a forced authentication at every BFF sign-in (chosen), with the identity service's sessions also ended through the path as hygiene; or only through the identity service. The first needs no admin right for the security property (decision 2).

### Criteria per option

| Criterion | (a1) built-in roles | (a2) built-in, fine-grained, edge filter | **(b) Ostium operations (chosen)** | (c) disable and strip | (d) manual step |
| --- | --- | --- | --- | --- | --- |
| NFR-70 least privilege | Fails: create, list, reset password, all attributes | Whose accounts only; the operation is narrowed by the edge, not by Keycloak | One client role per operation, no `realm-management` at all; Keycloak refuses everything else | Same access as deletion (needs (a) or (b)) | No application right |
| NFR-71 audit | Decisya's records; Keycloak admin events | Same | Decisya's intent and outcome records; the provider emits Keycloak admin events | As (b) | Recorded by the audited command only; the console step itself is not verifiable by Decisya |
| NFR-72 tenant scope | Decisya only | Decisya only; a body can rewrite `tenant_id` | Decisya, plus the provider refuses a user whose scope attribute differs from the request's | As (b) | The operator's diligence |
| NFR-73 unreachable | Retries | Retries | Retries; no request path depends on it | As (b) | Stays pending until the operator acts |
| NFR-74 no broad standing right | Fails | Partly | Met: 300 s client-credentials token, no human token accepted, edge admits only the Api | As (b) | Met |
| OP coverage | All but OP-13 | All but OP-13 | All needed (decision 2) | OP-3 only | OP-10 only (closures) |
| Ostium boundary | Breaks it (client, realm guard) | Breaks it | Kept: kit parts requested, Decisya composes | — | — |
| Decisya effort | M | L | M + M + L + S items (decision 13) | S on top of (b) | S |
| Ostium effort and dates | None | None | L + M + S + S + M; no date given yet | — | — |

## Decision outcome

Chosen option: **(b)**, because it is the only option in which Keycloak itself refuses every operation outside the list, no `realm-management` right exists anywhere, OP-13 has a safe mechanism, and nothing in Ostium's kit is built in Decisya. (c) stays defined and switched off. (d) is allowed for the beta only and ends before open sign-up.

### 1. The path

- **Keycloak side (Ostium's kit, request R-1).** A provider in the Keycloak baseline exposes the identity operations of decision 3 under the app realm's own path, `/realms/<app realm>/<provider path>/...`. It accepts only a bearer token issued by client credentials to the one configured client's service account, checks one client role per operation, emits a Keycloak admin event per call, and offers nothing else. It does not use `/admin/...`.
- **Client side (request R-2).** A typed .NET client package from Ostium with one method per operation and no generic call, client-credentials token handling, and result classification for retries.
- **Decisya.** A new module pair, `Decisya.Modules.IdentityOps` and `Decisya.Modules.IdentityOps.Contracts`, in the Api process. It owns the link from Decisya's person id to the identity id (OQ-39: the two are different), the operation records, the audit of every call and the retries. Other modules reach it only through its Contracts and Wolverine messages (ADR-0005). The operator's commands stay in `Decisya.Modules.Admin` at `/api/admin`'s device-bound level (NFR-66).
- **Edge.** One new back-channel route on Caddy admits the provider path only from the Api's fixed address; every other route refuses it (ADR-0016 amendment 2026-11-20).

### 2. How each operation is met

"Path" means decision 3's operations; "own data" means Decisya's own stores, with no admin right; "not needed" means the design avoids the operation.

| Op | Operation | How it is met | Mechanism | First needed by |
| --- | --- | --- | --- | --- |
| OP-1 | Delete the identity with its credentials | Path | `delete-identity` | EP-12 (2027-09-06) |
| OP-2 | Confirm it is gone | Path | `read-identity-state` returns not found. The delete's own success is not the confirmation (AC-12-9) | EP-12 |
| OP-3 | Fallback: disable and strip | Not needed (switched off) | `disable-and-strip` is defined, its role is never mapped; mapping it needs a dated amendment after the narrow legal review | — |
| OP-4 | End all of a person's sessions | **Own data**, with the path as hygiene | Decisya ends every BFF session of the person in its ticket store (request R-5), and every BFF sign-in forces authentication, so a live identity-service session reaches no Decisya page, data or action without a passkey (AC-12-3). Keycloak's account console still accepts that session; ADR-0025 M-9 and M-12 limit what it can change there (F333-02). The path's `end-identity-sessions` also ends Keycloak's sessions after a credential reset and at closure | EP-26 (2027-03-02) |
| OP-5 | Remove every credential | Path | `remove-credentials` (all) then `end-identity-sessions` | EP-28 (2027-05-25) |
| OP-6 | Add, replace or remove one credential; open one registration | Path | Removal: `remove-credentials` (one id). Adding: `issue-enrolment-ticket` for one credential of a named class, handed by the BFF to the person's browser at a later sign-in; never by email | EP-28 |
| OP-7 | Change the registered email or name | Path | `change-email`, `change-name`: each sets that attribute only | EP-28 |
| OP-8 | Read credentials and sign-in events for a review | Path for credentials; own data for sign-ins | `read-credentials` returns id, class and creation time only, no key material. Sign-in events come from the BFF's own sign-in record (time, class from `acr`, new or known device). If EP-28's design needs the exact credential of each sign-in, an event feed is requested (R-6), never `view-events` | EP-28 |
| OP-9 | Grant tenant access | Not an identity-service operation | Decisya's membership data (EP-10's ADR) | — |
| OP-10 | Manual operator step | No application call | Option (d), decision 11 | Beta only |
| OP-11 | Support-path credential, email or name changes | Path | As OP-6 and OP-7 | EP-28 |
| OP-12 | Re-apply identity changes after a restore | Path | The same operations, one call per change-log entry, run by an audited admin command of the restore procedure before sign-in reopens. No new right | EP-18 (2027-11-21) |
| OP-13 | Case-bound re-enrolment after total loss | Path | `issue-enrolment-ticket` for re-enrolment at the account's original level: single use, minted only when the case's browser presents its case secret (AC-25-9), at most 300 s life, never by email, never seen by the operator | EP-25 (2027-04-13) and EP-28 |
| OP-14 | Create an identity at sign-up | **Not needed** | Sign-up runs in Keycloak's own registration flow. The same-answer behaviour for an existing address (AC-24-6) and the beta-code check (EP-22) need a registration extension in the Keycloak baseline: that is the EP-24 realm issue's dependency on Ostium (ADR-0025 U-9, #266 F-1), not this path. If it cannot be built, creating identities would need a new right, which this ADR does not grant: it takes a dated amendment | — |
| OP-15 | Read sign-in events to notice suspicious attempts | **Not needed by the path** | Built from what Decisya observes: sign-ins with device recognition, step-up failures at the BFF callback, sign-up with an existing address. If G3's definition of "suspicious" (EP-27 AC-27-2) needs failures that happen only inside Keycloak, the event feed R-6 serves it; never `view-events` | EP-27 (2027-03-09) |

**N-2 answered.** Sign-up and the sign-in-event read need no admin right in this design: OP-14 runs in Keycloak's registration flow with a registration extension, and OP-15 uses Decisya's own observations, with an optional event feed. Both are possible; neither is part of this path's rights.

**Self-service changes (EP-24 AC-24-7).** Adding, replacing or removing a credential and changing the email or name after the device-bound step-up run in Keycloak's own application-initiated actions, gated at the `device-bound` level (ADR-0025 M-9, U-7). If the realm spike (U-7) shows they cannot be gated, they are disabled, and the Api runs the same path operations after checking the device-bound proof (at most 300 s old). That adds triggers, not rights.

### 3. The rights (NFR-70)

- **One client**, `decisya-identity-ops` (the name is a Decisya value): confidential, service account on, standard flow, implicit flow, direct access grants and device grants off, no refresh token for client credentials, `fullScopeAllowed` off, access token life at most 300 s.
- **Its service account holds exactly these client roles of its own client**, one per operation, none composite. The role names are Decisya values passed to the provider through the realm's configuration:

| Role (Decisya value) | Operation | Request (shape only; Ostium fixes the paths) |
| --- | --- | --- |
| `identity-ops.delete-identity` | OP-1 | delete one user by id |
| `identity-ops.read-identity-state` | OP-2 | read `{exists, enabled}` for one user by id |
| `identity-ops.read-credentials` | OP-8 | list one user's credentials as `{id, class, created}` |
| `identity-ops.remove-credentials` | OP-5, OP-6, OP-11, OP-12 | remove one credential by id, or all of them created before a given time (the operation's request time, M333-03) |
| `identity-ops.change-email` | OP-7, OP-11, OP-12 | set the email of one user; marks it unverified (AC-24-7) |
| `identity-ops.change-name` | OP-7, OP-11, OP-12 | set the registered name of one user |
| `identity-ops.end-identity-sessions` | OP-4, OP-5 | end every Keycloak session of one user |
| `identity-ops.issue-enrolment-ticket` | OP-6, OP-13 | mint one single-use, short-lived registration ticket for one user, for one credential or for a re-enrolment of two at a given class |
| `identity-ops.disable-and-strip` | OP-3 | **defined, never mapped** (option (c), switched off) |

- **No other right exists.** The client holds no `realm-management` role, no realm role and no fine-grained permission, so Keycloak's admin API refuses it everything. The provider offers no create, list, search, count, attribute write (other than email and name), required-action write, password, impersonation, role, group, client, realm-setting, identity-provider or event operation, and it returns no key material, public key, label, AAGUID or attestation data.
- **Protected accounts.** The provider refuses every operation on a user who holds any role on a configured protected list (Decisya: `platform-admin`) or who is a service account. The path can never touch an admin.
- G4-17-10 ("no `realm-management` mappings") therefore stays true. G4-17-01 ("no `clientRoles`; a secret only on `decisya-bff`") changes to "client roles only on `decisya-identity-ops`, exactly the list above, mapped only to its service account; secrets only on the two clients".

### 4. Who calls, and how

- Only `Decisya.Modules.IdentityOps` references the client package. Inside it, one internal gateway type is the only user of the client, and it writes the intent record before every call (decision 5).
- A call is triggered only by:
  - an audited admin command at `/api/admin`'s device-bound level (the operator's support-case and restore commands, EP-28, EP-18);
  - a closure that becomes due (EP-12), sent by the closure's owning module;
  - for `issue-enrolment-ticket` only, the BFF's sign-in for a completed case, after the case binding is checked (AC-25-9).
- **The case binding is checked in the Api, never only in the BFF (M333-02 (a)).** The BFF forwards the case secret its browser presents. The case's owning module (or IdentityOps through the owner's Contracts) checks it before the ticket is requested. The case secret:
  - is stored only as a hash and compared in constant time;
  - is bound to one case and one person;
  - is good for one ticket at a time;
  - ends with the case's enrolment period.

  The ticket endpoint is rate-limited per case and per source, and it locks the case for operator review after a small number of failed checks (the number is a Decisya value, set in D-4 and reviewed by its G3). A ticket counts toward the ticket cap of decision 7.
- **The ticket is never written down (M333-02 (b)).** It appears in no log, audit record, metric, trace attribute or kept URL:
  - Caddy's access log on the id host drops the query parameter that carries it (a log filter with a `deploy/tests` guard, D-3);
  - the BFF, YARP and the Api never log the ticket response or the redirect `Location` (D-4, D-5);
  - the realm keeps its `Referrer-Policy: no-referrer` default (D-2).
- Every call except the ticket runs as a **durable Wolverine message** handled off the request path. Its envelope carries the tenant and the caller id of the request that asked for it (ADR-0024), so no made-up service actor is needed and a retry keeps the original caller. The ticket is synchronous, because the person's browser waits for it.
- This depends on backlog #243 items 231-1 (durable messaging, scheduled retries) and 231-5 (the caller id on a nested message), and on the pre-generated handler code that ADR-0024 amendment 1 makes a prerequisite of the first handler.

### 5. Audit (NFR-71)

- **Before every call**, in its own transaction with the operation record's state change (ADR-0013): one intent record. **After the call**: one outcome record. A call whose intent record is not committed is never sent.
- Each record holds only: the actor (the internal id from the envelope's caller), the tenant, the internal person id, the operation kind, the operation id, the attempt number, the case or closure reference, the outcome code and the time.
- It never holds the identity id, an email, a name, a credential id, a ticket, a token or a response body. The identity id stays only in the module's link table, so that, after a completed closure, no record links a person id to an identity (AC-12-5).
- Keycloak's admin events also record every call (the provider emits them, with no representation), kept 90 days (NFR-46). The operator's identity check compares their daily count per operation with Decisya's outcome records (detective control; decision 13, D-8).

### 6. Tenant scope (NFR-72)

- An operation names Decisya's person id and the case or closure that triggered it. The module resolves the identity id from its link table under the tenant of the message's envelope (ADR-0024). It refuses before any call unless the case or closure belongs to that tenant and to that person.
- Each request carries the expected tenant. The provider refuses unless the user's scope attribute (Decisya: `tenant_id`) equals it, and refuses a user without the attribute (fail closed).
- Every operation has a two-tenant isolation test.

### 7. Credential and token (NFR-74)

- The client secret is a Compose file secret given to the Api and, for the realm import placeholder, to Keycloak's wrapper; never to the BFF, the SPA or any other service, never in source.
- Rotation: at most every **90 days**, at once on any suspicion, and at each phase exit. The operator regenerates it at the workstation console (8444), replaces the secret file and restarts the Api. No code changes.
- Tokens come from the client-credentials grant only, live at most 300 s, are cached in memory until 30 s before expiry, and are never logged, stored or forwarded. Their audience is the operations client only, so the Api's own JWT validation (audience `decisya-api`) refuses them. The provider refuses any token not issued by client credentials to that client's service account, so no human session token can call it.
- **Blast radius: caps per operation kind (M333-01 (a); values set by Marco, 2026-11-20).** The provider enforces each cap per realm, over a rolling hour and a rolling 24 hours. The caps are realm configuration, which the client cannot change: it holds no realm-settings right.

  | Operation kind | Per rolling hour | Per rolling 24 h |
  | --- | --- | --- |
  | `issue-enrolment-ticket` | at most 5 | at most 10 |
  | `change-email` and `change-name` together | at most 5 | at most 10 |
  | Destructive: `delete-identity`, `remove-credentials`, `disable-and-strip` | at most 30 | at most 60 |

  - `read-identity-state`, `read-credentials` and `end-identity-sessions` are not capped. None of them changes who controls an account.
  - Above a cap, the provider answers "too many" and emits an admin event.
  - Decisya treats "too many" as a transient failure and raises an operator alert at once.
  - For a restore (OP-12), the operator may raise the destructive cap through the realm configuration at the console, and lowers it again afterwards (a runbook check, D-8).

### 8. Edge (ADR-0016 amendment 2026-11-20)

- The Api gets a fixed address on the back-channel network.
- One back-channel route on the id host admits the provider path of the app realm, only from that address. The existing back-channel realm route refuses that path for every other source, the BFF included, and the published realm route refuses it, so no LAN client reaches it.
- The refusals and the admitting route match the provider segment as a prefix without a trailing slash, so no path variant (bare segment, `;x` parameter, suffix) falls through to the broader realm routes (M333-05).
- The id host's access log drops the query parameter that carries the enrolment ticket (M333-02 (b)).
- No published port, network, alias or route to `/admin/...` is added. The console stays on 8444 from the workstation only. The operator's path is unchanged.

### 9. When Keycloak is unreachable (NFR-73)

- No ledger endpoint, no sign-in of an enrolled user, and no readiness check of the Api depends on the path.
- Per-call timeout 10 s, one call in flight at a time.
- A transient failure (no connection, timeout, server error, "too many") is retried: first after **1 minute**, doubling, capped at **1 hour**.
- **How long retries run depends on the operation (M333-03):**
  - **Without limit** while pending, for `delete-identity`, `read-identity-state` and `end-identity-sessions`. Their goal cannot go stale.
  - **Until a not-after time**, for `remove-credentials`, `change-email` and `change-name`. The not-after time is a Decisya value: **7 days** after the request, or the end of the case's enrolment period if that comes first.
    - After it, the operation becomes "expired, operator review" and is never sent.
    - `remove-credentials` (all) removes only credentials created before the operation was requested. A late retry then never removes a credential the person registered meanwhile.
- **Cancel.** The operator can cancel any pending operation by an audited admin command. A cancelled operation is never sent.
- The pending email or name value is cleared on settlement, expiry or cancellation.
- A refusal (the provider says the token or role is wrong) means misconfiguration or a rotated secret. It is retried at the 1-hour cap, within the same limits, and raises an operator alert at once.
- Any operation pending more than **24 hours** raises an operator alert, whatever its deadline (S333-02, D-8).
- The operation shows "pending" in the operator's queue from the first failure. A closure shows "overdue" from the warning point: **7 days before the closure's deadline** (AC-12-8). The deadline itself is the response window the narrow legal review confirms; Marco sets it when the review answers. Both are Decisya options with no default in shared code.
- No closure completes before `read-identity-state` returns "not found".
- The enrolment ticket fails with the generic error; the case stays completed and waiting for enrolment. How its enrolment period treats an outage is EP-25's design.

### 10. Closure sequence (OQ-39, strict reading kept)

1. When the closure takes effect, Decisya ends every session of the person (OP-4, own data) and blocks the account in its own state. During a support closure's grace period the identity stays enabled and is not disabled.
2. When the closure is due (at once in self-service; at the end of the grace period, or when an open restore request is declined, through support), its owner asks IdentityOps for `delete-identity`.
3. IdentityOps deletes, confirms with `read-identity-state`, and publishes the outcome. On failure it retries as in decision 9; the closure stays pending, blocked, with the link kept.
4. Only after the confirmation (or, under (d), the recorded step) does the owner delete the profile. It then asks IdentityOps to remove the link last. IdentityOps refuses to remove a link without a confirmed deletion or a recorded manual step for that person.

There is no departure from the strict reading.

### 11. Options (c) and (d)

- **(c)** `disable-and-strip` exists in the provider, and its role is defined but never mapped. Mapping it needs a dated amendment to this ADR, after the narrow legal review confirms before the first real user that it is enough, and the privacy notice lists the disabled identity (AC-13-5). Each such identity is deleted once deletion works again.
- **(d)** is allowed only for a closure's identity step, only during the invite-only beta, and only if the narrow legal review and the business owner accept it (OQ-12 (d)). The operator deletes the user at the console from the workstation and then records it with "manual identity step recorded" (EP-28 AC-28-2). The rules:
  - The command is refused and not offered until a deployment setting switches it on.
  - The Api refuses to start with that setting on while open sign-up is on (EP-22's deployment setting). So (d) ends at the latest at the switch to open sign-up (N-1, Marco 2026-11-20).
  - It is also switched off as soon as `delete-identity` is live in the deployment.
  - It is reported open until then.
  - It covers no other operation. It does not by itself let the beta open.

### 12. What is Decisya's and what is requested from Ostium

**Decisya's (built and tested in Decisya):**
- the `Decisya.Modules.IdentityOps` module pair, its schema, role and migration, its durable messages, audit, retries, tenant checks and telemetry;
- the realm content: the client, its roles and their mapping, the provider's configuration values (client id, role names, protected roles, scope attribute, the caps per operation kind) and the import placeholder for the client secret;
- the composition values (realm, base address, client id, secret file, retry and warning values);
- the Caddy route, the Api's fixed address, the Compose secret and the provider mount, and their guards;
- the operator commands in `Modules.Admin`, the runbook steps, the identity check, and all of Decisya's tests.

**Requested from Ostium (exchange request; adopted from Ostium's release, never built in Decisya):**

| Id | Request | Acceptance criteria |
| --- | --- | --- |
| R-1 | **Identity-operations provider in the Keycloak baseline**, compatible with the baseline's Keycloak version | (1) Exactly the operations of decision 3, under the realm's own path, none under `/admin`. (2) Accepts only a bearer token that passes every one of these checks (F333-01): signature with the realm's active keys, algorithm on an RS256/ES256 allow-list; `iss` is the app realm; `exp` not passed; `typ` is `Bearer`; `azp` is the configured client; `sub` is that client's service-account user; the configured client is in `aud`. Any other token or client gets 401 or 403. (3) One configured client role per operation, checked per call; nothing is allowed with no configuration (fail closed). Client id, role names, protected roles, scope attribute and the caps are realm configuration with no defaults. (4) Refuses users holding a protected role, service-account users, and users whose scope attribute differs from the request's expected value or is missing. (5) Returns no key material, public key, label, AAGUID or attestation data; credential classes are mapped from the two WebAuthn credential types and "other". (6) `change-email` and `change-name` change only that attribute; `change-email` marks the email unverified. Neither triggers a user event whose details carry the old or the new email or name (S333-08). `remove-credentials` (all) takes a "created before" time and removes only credentials created before it (M333-03). (7) `issue-enrolment-ticket` mints a single-use action token for one user, one purpose (one credential of a class, or a re-enrolment of two at a class), life set per call and at most 300 s, returned to the caller and never emailed. Consuming it runs only that registration, never registers at a lower class, and **creates no Keycloak sign-in (SSO) session**: the person then signs in through the BFF, where forced authentication applies (M333-02 (c)). Each ticket counts toward the ticket cap. (8) Each call emits one admin event, without representation, naming the operation, the client and **the target user (its resource path)** (M333-01 (b)). (9) **A per-realm cap per operation kind, per rolling hour and per rolling 24 hours** (decision 7's table), answering "too many" above it and emitting an admin event (M333-01 (a)). The behaviour of the counters across a Keycloak restart is stated (S333-05). (10) Shipped with its tests, its SBOM and its source commit, covered by Ostium's signed checksums: preferably inside an `Ostium.*` NuGet package, so ADR-0019's chain and Decisya's locked restore cover it, or by another channel with the same evidence. It bundles no third-party library beyond what Keycloak provides, or lists every bundled one in its SBOM (M333-04 (a)). (11) Mounted read-only into the stock Keycloak image's providers folder, with no custom image. (12) With the provider mounted and its configuration missing or wrong, Keycloak starts and serves sign-in, and the operations fail closed; every call has a bounded execution time (S333-05) |
| R-2 | **`Ostium.Keycloak.IdentityOps` .NET client** (name Ostium's choice) | One method per R-1 operation and no generic call; options with no defaults (base address, realm, client id, secret source); client-credentials token cached until 30 s before expiry, at most 300 s; a result type that tells done, already gone, not found, refused, too many and unavailable apart; per-call timeout option; no logging of tokens, bodies, emails, names or tickets; OpenTelemetry spans with no personal data; a hook before and after each call that the caller can use to write its records, where an exception in the "before" hook stops the call. Optional, asked for: signed-JWT client authentication (`private_key_jwt`), so that Keycloak holds only a public key (S333-06) |
| R-3 | **`Ostium.Keycloak.Realm` rule option** for one identity-operations client | Given the app's client id and role list as options, the rules check: service account only; standard, implicit, direct and device grants off; no refresh token for client credentials; token life at most 300 s; `fullScopeAllowed` off; exactly the listed client roles, none composite, mapped only to that service account; **no realm role on the service account, the default-roles composite included** (S333-09); no `realm-management` mapping anywhere (kept); its secret is a placeholder, or a public key under `private_key_jwt` (S333-06); any role the app lists as "never mapped" is unmapped. **On every client of the realm, standard token exchange is off, and the legacy token-exchange feature is off** (F333-01). The token-exchange rule applies with or without the option. Without the option, today's other rules stand |
| R-4 | **`Ostium.Keycloak.Testing` support for the provider** | The fixture can start Keycloak with the R-1 provider mounted as in production, so Decisya's Integration tests run against the real provider and realm |
| R-5 | **`Ostium.Bff`: end every session of one subject; forced authentication at sign-in** | An option-driven way for the host to end every ticket of one subject in the ticket store (needed by AC-26-3, AC-26-4 and AC-12-3), and an option to force authentication on every sign-in request (for example `prompt=login`), while keeping the step-up's own maximum age. Values are the host's |
| R-6 | *(only if EP-27's or EP-28's design needs it)* **Minimal sign-in event feed** from the Keycloak baseline | Delivers, per event: the user, the event type, the credential class, the time and a device signal; no IP address unless the design asks for it; to a Decisya back-channel endpoint, authenticated. Not requested now |

### 13. Effort, order and dates (COND-02, COND-03)

**COND-02.** Decisya uses option (b). It takes the Ostium requests R-1 to R-5 and the Decisya issues D-1 to D-9 below. The path's Decisya side can be in place about five weeks of Decisya work after Ostium releases R-1 to R-4.

**COND-03.** The planned dates in `docs/intake/decisya-phase1.md` exclude the path. They are re-planned from this section once Ostium answers.

Sizes: **S** = up to 2 working days, **M** = 3 to 5, **L** = 6 to 10. The basis is by analogy with this repository's earlier issues of the same shape:
- a realm-content change with realm-file and Integration tests (#25's mapper, #121);
- an edge change with its guards and runbook (#178);
- a new module with schema, audited commands and its tests (#23 to #25).

They are estimates for one person, not measurements.

| Id | Follow-up issue (draft title; Marco creates it) | Owner lane | Size | Depends on |
| --- | --- | --- | --- | --- |
| D-1 | Exchange request to Ostium for R-1 to R-5 (drafted by the orchestrator from this ADR; Marco approves). It carries R-1's criteria (12) and the counters' restart behaviour (S333-05), R-1 (6)'s no-email-in-user-events rule (S333-08), R-2's and R-3's `private_key_jwt` option (S333-06), and R-3's no-realm-role rule (S333-09) | orchestrator | S | ADR-0026 accepted |
| — | ADR-0019 block 2 (`Ostium.Keycloak.Testing`) and block 3 (`Ostium.Keycloak.Realm`, the realm guard) switches: already planned in ADR-0019's order, sized in their own issues | identity-dev | — | Ostium releases already available |
| D-2 | `feat(identity): identity-operations client and provider in the production realm (adr-0026)`. Scope includes: the caps per kind in the realm configuration; the service account with no realm role (S333-09); token exchange off; `Referrer-Policy: no-referrer` kept (M333-02 (b)); the provider (or its SBOM) added to the ADR-0015 scan targets with the image thresholds and exception rules (M333-04 (a)); a test that derives the committed SHA-512 pin from the JAR inside the `.nupkg` the A3 check verified (M333-04 (b)); V-1, V-2, V-3 and V-5 settled first. **The provider's mount in the deployable stack lands only with or after D-3's refusals** (G6-333-01); D-3's guard enforces it | identity-dev; devops for the stack mount, pin and scan; platform-dev for the AppHost's Keycloak resource | M | R-1, R-3, R-4; blocks 2 and 3 switched; D-3 merged before or with it |
| D-3 | `feat(deploy): back-channel route to the identity operations from the api only (adr-0016 amendment 2026-11-20)`. Scope includes: the prefix refusal without a trailing slash and the variant guards (M333-05); the id host's access-log filter that drops the ticket's query parameter, with its guard (M333-02 (b)); an `ip_range` on the back-channel network that excludes every fixed address, with a Compose guard (S333-01); **a guard that fails when Keycloak has a provider mount in the Compose file unless the Caddyfile refuses the provider prefix on both realm routes, the published one and the back-channel one** (G6-333-01) | devops | M | Nothing from D-2. **Merged before or with D-2** (G6-333-01). The refusals need nothing from D-2, and the admitting route serves nothing until the provider is mounted |
| D-4 | `feat(identity-ops): audited identity-operations module with durable retries (adr-0026)`. Scope includes: the case-binding check in the Api, the hashed case secret, constant-time compare, rate limit and lock (M333-02 (a)); no ticket in any log or record (M333-02 (b)); retry limits, not-after time, expiry and the audited cancel (M333-03); the pending email or name, preferably kept in the support case and read at call time, otherwise `[Sensitive]`, validated, cleared and in the erasure job's scope (S333-03); the one cross-tenant send-side entry in the shape S333-04 states | backend-dev; test-engineer at G5 | L | #243 items 231-1 and 231-5 and the pre-generated handler code; R-2; D-2; D-3 |
| D-5 | `feat(bff): end every session of one person and force authentication at sign-in (adr-0026 op-4)`. Scope includes: the BFF and YARP never log the ticket response or the redirect `Location` (M333-02 (b)) | identity-dev at the BFF switch | S (after R-5) | R-5; ADR-0019 block 4 (BFF) switch. May fold into EP-26's sessions issue |
| D-6 | `docs(security): review 17 baseline for the identity-operations client (g4-17-01, g4-17-10)` | security-reviewer | S | D-2 |
| D-7 | `docs(adr): adr-0025 pointer to adr-0026 for the operator's reset and record` | architect | S | ADR-0026 accepted |
| D-8 | `docs(runbooks): identity-operations secret rotation, caps, compromise response and the scheduled admin-event comparison (adr-0026)`. Scope (M333-01 (e), S333-02, S333-05, S333-06):<br/>- **compromise response:** rotate the client secret at the console, or unmap the roles; list the provider's admin events since the earliest suspect time; for each identity touched, review with the person, through support, every credential registered by ticket and every email or name change;<br/>- **the daily comparison runs on a schedule**, not from memory, and flags any day whose count per operation kind exceeds a set threshold, even when Decisya's records agree;<br/>- the alert for any operation pending more than 24 hours;<br/>- unmounting the provider as the rollback;<br/>- a rotation updates the Api's secret file and the wrapper's together;<br/>- after an OP-12 restore, the raised cap is lowered again | devops | S | D-2, D-3 |
| D-9 | *(conditional)* `feat(admin): manual identity step switch for the beta, refused once sign-up is open (oq-12 d)`. Scope includes (S333-07): the operator re-reads the user in the console before recording the step; when `delete-identity` goes live, a one-off reconciliation runs `read-identity-state` for every closure completed under (d), from the identity change log | backend-dev | S | EP-28's command; only if `delete-identity` is not live when closures reach beta users |

**Done when (G6-333-03):**
- **D-2 to D-5 and D-9** are done when the G2 note's rule rows for that issue pass (`docs/architecture/333-admin-path-identity-service.md`, "Architecture-test, realm-guard and deploy-guard rules to add", column "Issue").
- **D-1** is done when the request carrying R-1 to R-5 with their acceptance criteria has been sent, and Ostium's answer (dates or refusal) is recorded by 2026-12-11.
- **D-6** is done when review 17's G4-17-01 and G4-17-10 restate the new baseline and cite D-2's tests.
- **D-7** is done when ADR-0025 decision 6 carries a dated pointer to ADR-0026.
- **D-8** is done when each scope bullet is a runbook step with its check, and the admin-event comparison runs on a schedule.

No issue is "tests only": each issue ships its own tests (CLAUDE.md). No follow-up issue builds any part of Ostium's kit. G3's SHOULDs S333-01 to S333-09 are placed in the issues above (D-1, D-2, D-3, D-4, D-8, D-9); none is left for #243.

**Ostium's side:** R-1 L, R-2 M, R-3 S, R-4 S, R-5 M (sizes as seen from Decisya; Ostium sizes its own work). **Dates: no date given by Ostium.** The exchange request (D-1) asks for release dates, with an answer by **2026-12-11**, so that the effort is known by **2026-12-21**.

**Latest useful release dates (for the re-plan):**
- **R-5 by 2027-02-01**, so that D-5 lands before EP-26 starts (2027-03-02).
- **R-1 to R-4 by 2027-04-01**, so that D-2, D-3 and D-4 (about five weeks of Decisya work) land before EP-28 starts (2027-05-25). EP-25's re-enrolment (AC-25-9, from 2027-04-13) uses the ticket from D-4.

**What moves if Ostium gives no date before 2026-12-18 (the issue's target):**
- EP-24 keeps its start (2026-12-27). It needs this decision, not the path.
- Marked "depends on Ostium, no date":
  - EP-26's AC-26-3 and AC-26-4 (on R-5);
  - EP-25's re-enrolment (AC-25-9);
  - EP-28's completion actions;
  - EP-12's identity step and completion;
  - EP-18's AC-18-6 re-apply.
- Option (d) is then the only identity step for closures in the beta. It covers a closure's identity step only and provides no re-enrolment (OP-13).
- The beta cannot open without re-enrolment (EP-25 AC-25-9). So **the beta's date moves with the release date of Ostium's R-1, whatever (d) does.**
- Decisya waits (Q-1, decided by Marco 2026-11-20). The path's dates follow Ostium's.

### 14. Unverified behaviour, and the stop rule

These come from upstream Keycloak documentation for earlier 26.x releases and are **not verified** at the pinned 26.7.5 (26.8.1 pending in #268). Ostium's R-1 and Decisya's D-2 settle each of them first:

| Id | Question |
| --- | --- |
| V-1 | A realm resource provider can authenticate a service-account bearer token and check client roles at the pinned version |
| V-2 | A custom action token can run a WebAuthn registration of a given policy (synced or device-bound class) without email, single use, at most 300 s, and refuse a lower class |
| V-3 | The stock image picks up a provider mounted read-only into its providers folder at `kc.sh start` (the container is not read-only today) |
| V-4 | A forced authentication at every BFF sign-in (R-5) asks for the passkey even with a live Keycloak session, and keeps the step-up's maximum age |
| V-5 | Admin events emitted by the provider appear in the realm's admin event store, without representation, naming the operation, the client and the target user |
| V-6 | Ending a user's Keycloak sessions from the provider also revokes their refresh tokens |

A negative on V-1, V-2 or V-3 stops D-2 and goes to Marco, with no silent fallback. It re-opens this decision by a dated amendment, and G3 reviews it.

**A negative on V-5 also stops D-2 (M333-01 (c))**, unless the provider records each call in a store the Api cannot write. The detective control for an Api compromise (R-APIOPS) must not depend on the Api.

### Consequences

- Good:
  - Keycloak itself refuses every operation outside the list. No application component holds `realm-management`, and G4-17-10 stays true.
  - The `/admin` paths stay unreachable from every container. The operator's console path on 8444 is unchanged.
  - Every call is recorded twice: Decisya's intent and outcome records, and Keycloak's admin events. Neither holds an email, a name, a credential or a token, and Decisya's records hold no identity id.
  - OP-13 gets a case-bound re-enrolment that never travels by email.
  - Sign-up and sign-in events need no admin right (N-2).
  - The mechanism is reusable by the second application through Ostium (ADR-0019).
- Bad:
  - **Breaking for the operator, the security reviewer and anyone running the deploy guards:** "no application component holds admin rights in the realm" ends. The Api holds a narrow right. They are told by this ADR, the ADR-0016 amendment, the release notes of the release that ships D-2 and D-3, and review 17's updated baseline (D-6).
  - **Residual R-APIOPS (Marco accepts it by name at acceptance; G3 rates it Medium with M333-01's controls).** Whoever controls the Api process can, within the caps per kind of decision 7 (at most 10 enrolment tickets, 10 email or name changes and 60 destructive operations per 24 hours), on the account of any end user who is not protected (admins and service accounts are refused), in any tenant, until the operator responds:
    - delete identities and remove credentials;
    - change emails, which opens R-RECOVERY to a mailbox the attacker holds;
    - **register their own passkey on an account through an enrolment ticket**, at the device-bound class if they ask for it. **That access persists after the Api is patched and its secret rotated**, until support removes the credential.

    The scope-attribute and case-binding checks stop none of this, because they run in the process the attacker controls. The controls are:
    - the edge (only the Api's address, only the provider path, no `/admin`);
    - protected accounts refused;
    - the caps, which the Api cannot change;
    - Keycloak's admin events naming each target user, which the Api cannot write;
    - the scheduled daily comparison with its threshold, and D-8's compromise response, which reviews every touched identity with the person through support.

    **Only the edge, the protected-account refusal and the caps prevent anything.** Keycloak's admin events, the scheduled daily comparison and D-8's response are detective. They find a compromise after the fact, not at once. A passkey registered before the response stays usable, also after the Api is patched and its secret rotated, until support removes it.

    Its precondition is code execution in the Api.
  - **Residual R-PROVIDER (Marco accepts it by name at acceptance; G6-333-02).** Third-party code runs **inside the Keycloak process**. A defect or a compromised build therefore reaches:
    - every realm, the master realm and the operator's account included;
    - Keycloak's database connection;
    - the sign-in of every user. A fault can stop sign-in for everyone (T333-11).

    NFR-70 rests on the provider's own checks. The controls are:
    - Ostium's signed chain;
    - the SHA-512 pin derived from the verified package;
    - the ADR-0015 scan (M333-04);
    - D-2's Integration tests, the only test of its correctness;
    - the edge, which limits who can call it (the Api's address only, once D-3 is in);
    - unmounting the provider as the rollback (D-8).

    **The caps of decision 7 do not bound R-PROVIDER**, because the provider itself enforces them.
    - The listed controls lower the chance that a defective or compromised build reaches production, and limit who can call it.
    - None of them contains what such a build can do once it runs.

    Accepting R-PROVIDER accepts that full scope:
    - every realm, the master realm and the operator's account included;
    - Keycloak's database connection;
    - every user's sign-in.
  - Decisya depends on Ostium for a new kind of artifact (a Keycloak provider) and for dates it does not control.
  - **A Keycloak security bump never waits for the provider (M333-04 (c)).** If no compatible provider exists, Keycloak is bumped and the provider is unmounted. The identity operations then stay pending (decision 9), (d) covers beta closures, and Ostium is asked for a compatible release. A non-security bump waits for a compatible provider.
  - A new module, a new secret to rotate, a fixed address and a new route to guard.
  - The path cannot run before durable Wolverine messaging (#243 231-1, 231-5).
  - Adding the client to the realm on a database that already holds real users needs the non-destructive realm migration (C-03b). While every user is synthetic, the rebuild path applies.
- Changes to earlier decisions:
  - ADR-0016 R9.6 and R9.8, by its amendment 2026-11-20.
  - ADR-0025 decision 6:
    - step 4: the operator's reset runs through the path, as an audited command;
    - step 6: the record is Decisya's audit plus Keycloak's admin events. The console stays the break-glass path.
- Invariants: no `CLAUDE.md` invariant changes. Invariant 1 holds: every operation runs in its envelope's tenant, with a two-tenant test each.
- Enforced by (the rules and tests are listed in the architecture note):
  - `Decisya.ArchitectureTests`:
    - only IdentityOps references the client package;
    - one gateway type uses the client;
    - no identity id outside the module;
    - the Contracts surface is pinned;
  - realm rules from `Ostium.Keycloak.Realm` (R-3), and Integration tests against the real provider: every operation outside the list is refused, protected accounts and wrong tenants are refused, human tokens are refused, each cap per kind answers "too many" above its value, and each admin event names the target user;
  - `deploy/tests` guards: the route, the path-variant refusals, the ticket log filter, the fixed address, the secret table, the provider mount, and its pin derived from the verified package;
  - the ADR-0015 scan over the provider or its SBOM;
  - Integration tests: the audit (NFR-71), two-tenant isolation (NFR-72), stopping Keycloak (NFR-73), the case-binding check, lock and rate limit, and the expiry and cancel of stale operations;
  - the start-up refusal of option (d) with open sign-up.

## Decided (Marco, 2026-11-20)

These were open questions in the first draft. Marco answered them on 2026-11-20 (manifest `docs/ai/pipeline/333.md`, "G2 decisions" and "G3 decision"). Acceptance of the ADR itself is still Marco's.

- **Q-1. If Ostium declines R-1 or gives no date: wait.** The path's dates follow Ostium's. Option (d) covers only the identity step of the beta's closures; it gives no re-enrolment.
  - The options not chosen were (ii), a dated amendment to option (a2) as a stopgap, and (iii), a custom Keycloak image.
  - The accepted risk: the beta cannot open without re-enrolment (OP-13, EP-25 AC-25-9), and re-enrolment has no safe built-in mechanism. So the beta's date moves with the release date of Ostium's R-1, whatever (d) does.
- **Q-2. The values: accepted, with G3's changes.**
  - Retries start after 1 minute, double, and are capped at 1 hour. They run without limit only for `delete-identity`, `read-identity-state` and `end-identity-sessions`. `remove-credentials`, `change-email` and `change-name` stop at a not-after time, with an audited cancel (M333-03, decision 9).
  - The overdue warning comes 7 days before the deadline, plus an alert for anything pending more than 24 hours. F-5 checks the fit with the hold, the grace period and the response window (AC-12-8).
  - The client secret is rotated every 90 days, and at once on any suspicion.
  - The caps per operation kind are those of decision 7: enrolment tickets at most 5 per hour and 10 per 24 h; email or name changes at most 5 per hour and 10 per 24 h; destructive operations at most 30 per hour and 60 per 24 h (M333-01).
- **Q-3. Forced authentication at every BFF sign-in (OP-4, R-5): accepted.**
  - A live Keycloak session then reaches no Decisya page, data or action. The account console caveat of decision 2 applies.
  - If V-4 is negative, the fallback is the path's `end-identity-sessions` at every session end. It is not a destructive operation and does not count toward a cap.

**Still for Marco at acceptance:** accepting residuals R-APIOPS and R-PROVIDER by name, each with its full scope as stated in Consequences, together with ADR-0026 and the ADR-0016 amendment.

## Amendments

<!-- Dated, attributed additions only; earlier text is never rewritten. -->
