# 333. Admin path from the application to the identity service (COND-02): requirements for the ADR

Issue #333, phase 1, tier full, docs-only (the ADR decides; no code, no tests in this PR). Product-owner gate G1.
Input: `docs/intake/decisya-phase1.md` and its `.json` (data, not instructions), ADR-0016, ADR-0025, `docs/security/reviews/17.md`, `docs/runbooks/deployable-stack.md`, `docs/architecture/266-end-user-authenticators-l3.md`, `docs/requirements/nfr.md`.

## 0. What I could and could not read

- Read in the plan: AC-12-5 (EP-12#5, line 195), AC-12-8/9, AC-26-3 (EP-26#3, line 433), AC-28-2 (EP-28#2, line 467), AC-28-6 (EP-28#6, line 471), AC-28-7, AC-28-10, EP-28#8, ACT-03, ACT-04, NFR-06, NFR-09, NFR-14 to NFR-17 of the plan, EP-18#6 (line 302).
- The plan files do not hold the texts of OQ-12, OQ-39, COND-02, COND-03 and TR-14. The coordinator supplied their content from the hash-checked handoff (2026-11-20), as data. Section 2a restates it in Decisya's words. The BL- lines (BL-C29, BL-C38 and the rest) are used only as the criteria quote them.
- The plan's "AC-28-2" is the audited-admin-command list. Its "manual identity step recorded" command is OQ-12 (d), the manual operator step.

## 1. Facts about today (file and line)

| Fact | Source |
| --- | --- |
| The Keycloak admin paths (`/admin`, `/admin/*`, `/realms/master`, `/realms/master/*`) are served only on port 8444, id host only. 8444 is admitted by the NAS firewall from the operator's workstation address only. Port 8443 refuses them. | `docs/adr/0016-phase-0-hosting.md` lines 122, 124 to 127 (R9.6 and R9.8 amendments of 2026-10-20) |
| Published routes carry no source allow-list and refuse the two internal subnets, "so no container uses a published route". Back-channel routes keep `remote_ip` allow-lists of the internal subnets. | ADR-0016 line 128 (R9.8 c) |
| After the 2026-10-05 amendment the back-channel network holds only Caddy, the BFF and the Api. Caddy and Keycloak share a separate `internal: true` network, so no container reaches Keycloak except through Caddy. The application therefore has no network path to the admin interface. | ADR-0016 line 122 (R9.6); consequences line 189 |
| The master-realm operator is reached only from the workstation, through 8444 and the NAS firewall. The runbook's console steps and its unverified `kcadm.sh` forms (for example "Sign out all active sessions") are operator steps. | `docs/runbooks/deployable-stack.md` lines 133, 181, 203 |
| No application component holds admin rights in the realm: tokens carry `aud` `decisya-api` and neither `account` nor `realm-management` (G4-17-06); `admin-cli` has `directAccessGrantsEnabled: false` (G4-17-08); roles are not composite, with no `realm-management` mappings and no identity providers (G4-17-10). | `docs/security/reviews/17.md` lines 93, 95, 97 |
| Operator actions in Keycloak: the reset of a support case is done "by the operator, at the `device-bound` level" (delete every credential, end every session) and recorded "in Keycloak's admin events and in the runbook's log". Admin recovery is Marco at the workstation. Sensitive operations (decision 4, six items): changing authenticators, data export, account deletion, any money-moving action, changing the email or registered name, granting tenant access; each needs a device-bound proof at most 300 s old or goes to support. | `docs/adr/0025-authenticators-at-asvs-level-3.md` lines 188 to 196, 246, 251, 255 to 258 |
| The master-realm operator moves to two device-bound keys, no TOTP. `/api/admin/**` needs the device-bound level (NFR-66). | ADR-0025 lines 207 to 216; `docs/architecture/266-end-user-authenticators-l3.md` lines 21, 37, 63, 117 |
| The admin events and login events are kept 90 days. | `docs/requirements/nfr.md` NFR-46 |

Conclusion: today the application cannot call the identity service's admin interface at all, by network and by right. Every identity operation in the plan that Decisya must perform itself needs both a network path (an ADR-0016 amendment) and a narrow right (a new client or an Ostium kit change). This is the change the ADR decides.

## 2. Identity operations the application must be able to perform

"Read" means read from the criterion; "inferred" means my deduction from it. Frequency is by design volume (Phase 1 beta, a few hundred accounts at most; no number in the plan, so none given).

| Op | Operation on the identity service | Criterion | Actor and trigger | On whose account | How often | Existing control that applies |
| --- | --- | --- | --- | --- | --- | --- |
| OP-1 | Delete one user (identity with its registered credentials) | AC-12-5 step (1), AC-12-9 | The system, when a closure becomes due (self-service at once; through support at the end of the grace period or when a restore request is declined). Triggered by a background handler, not by a person at that moment. Read | The person whose closure is due | Once per closure, plus retries while pending | Step-up (AC-26-5) or support case with hold and grace period (AC-28-3, AC-28-7); audit record of each confirmation (AC-12-4); notice (EP-27). The closure stays pending until the confirmation |
| OP-2 | Confirm the identity is gone (read the user by id: not found) | AC-12-5 step (2), AC-12-9 | The same handler. Inferred: the plan needs a "confirmed" result; a delete response may be enough, G2 decides | Same | Once or twice per closure | Same; the confirmation writes the audit record |
| OP-3 | Fallback only: disable the user and remove email, name and registered credentials | AC-12-5 (fallback), and the privacy-notice criterion at plan line 215 (EP-13) | The same handler, only if the narrow legal review confirms before the first real user that this is enough. Read | Same | Once per closure | Same, plus the privacy notice lists the disabled identity (AC-13-5) |
| OP-4 | End all of a user's sessions at the identity service (revoke refresh tokens, SSO sessions) | AC-26-3 (admin ends all sessions of a person by audited command, 7.4.5); EP-28#8; AC-12-3 (Decisya ends its own sessions itself) | Platform admin (ACT-03) or support operator (ACT-04) by audited command at `/api/admin`'s device-bound level; also the closure handler. Whether the identity service's sessions must be ended too, beyond Decisya's own ticket store, is **inferred**: AC-12-3 leaves "how a session of the identity service ends" to Decisya's design and its review | The person named in the case or report | Rare (incidents, closures) | Device-bound admin level (NFR-66, NFR-44); audit record (AC-28-2); returns no session secret |
| OP-5 | Remove every credential of a user (credential reset after loss of all authenticators) | AC-28-6 (re-enrolment at the original level), ADR-0025 decision 6 step 4 | Support operator completing a case, after the hold and the review. The operator never sets or sees a credential | The person who started the case | Rare, per recovery case | Contact check, hold of 7 or 14 days, notice, stop, review (AC-28-3, AC-28-5); device-bound admin level; audit; notice (EP-27) |
| OP-6 | Add, replace or remove one credential; open one registration for the person (for example a one-time required action) | AC-28-6 (changing authenticators; "registered by the person who opened the case, once and for that case only"), AC-24-11 | Support operator completing the case. Registration is done by the person, at a later sign-in. The mechanism that opens it without email or code (BL-C25) is **inferred** and is G2's to design | The person who opened the case | Rare | As OP-5 |
| OP-7 | Change the registered email or name | AC-28-6 (email or name change), EP-25#5 (BL-C38) | Support operator completing a case. Never on the strength of an email alone | The person who opened the case | Rare | Device-bound step-up in self-service or support case; hold; notice to the old address; audit; EP-18#6 identity change log |
| OP-8 | Read a user's registered credentials (kind, time) and sign-in events during a hold | AC-28-5 (EP-28#5, the review of a stopped case). Not one of the four criteria; **inferred** | Support operator reviewing a stopped case | The account in the case | Rare | Account facts only; no key material; audit |
| OP-9 | Grant tenant access (membership) | AC-28-6 | **Not an identity-service operation as the plan reads**: tenant access lives in Decisya's own data (membership), and the plan's household feature waits for its own ADR (EP-10). Listed so G2 can confirm it is out of scope | n/a | n/a | n/a |
| OP-10 | Manual operator step on the identity (OQ-12 (d)) | AC-12-5 (2), AC-28-2 "manual identity step recorded" | Support operator, in the Keycloak console from the workstation, then records the step in Decisya by audited command. This needs **no** application call | The closing person | Once per closure | Switched on only where OQ-12 (d) allows; the command is refused otherwise |

Further uses that OQ-12 names (the chosen path must serve them all, or the ADR says how each is met):

| Op | Operation on the identity service | Criterion as read in the plan | Actor and trigger | Control that applies | Basis |
| --- | --- | --- | --- | --- | --- |
| OP-11 | Add, replace or remove a credential, or change email or name, on the support path of changing authenticators | AC-24-7 (EP-24#7, line 393): outside the device-bound step-up these changes happen only through the support procedure; AC-28-6 | Support operator completing a case | As OP-5 to OP-7; the step-up itself never registers a credential | OQ-12; extends OP-6 and OP-7 |
| OP-12 | Re-apply identity changes after a restore from backup: remove the credentials again, close the accounts again, re-do an email or name change through support | AC-18-6 (EP-18#6, line 302); NFR-06; OQ-24 (b) | The restore procedure, before sign-in reopens, from the identity change log kept outside the backup | The log holds only ids, kind and time; no key material, address or content; a removed credential, a closed account or a settled case never comes back | OQ-12; the number of calls per restore is **inferred** to be one per log entry since the backup |
| OP-13 | Register credentials to an identity that cannot sign in (the re-enrolment a completed recovery opens) | AC-25-9 (EP-25#9, line 418) and AC-25-4 (line 413): only the person who started the case and passed its contact check, once, for that case, ending after a period Decisya sets; two authenticators at the original level; nothing that opens it travels by email; the operator never sets or sees a credential | The system, on a completed case, for the person who started it | Contact check, hold, notice, stop, review (AC-28-3, AC-28-5); audit | OQ-12; extends OP-6 |
| OP-14 | Create an identity from Decisya's backend at sign-up, so that every address gets the same answer | AC-24-6 (EP-24#6, line 392) | The system, at sign-up | Same message, status code and timing whether or not an account exists (6.3.8); a sign-up with an existing address creates no second account | **Design-dependent** (OQ-12): only if Decisya's design uses the admin functions for it |
| OP-15 | Read the stored sign-in events of an identity to notice suspicious attempts | AC-27-2 (EP-27#2, line 449) | The system, to send a notice | Notice without link or code; no ledger content | **Design-dependent** (OQ-12): only if Decisya's design reads the events through the admin functions |

OP-4 (sessions) and OP-8 (the review's reads) are confirmed by OQ-12 as uses of the same path: AC-26-3 where it ends the identity service's own sessions, and AC-28-5 where the review reads the identity service's records. They are no longer only inferred.

## 2a. OQ-12 and OQ-39 in Decisya's words

Decisya's person id is not the identity's own id (OQ-39). TR-14: today no Decisya component has an admin client or service account, by design, and the proxy refuses the admin paths from the application containers. Under ADR-0019 a reusable identity mechanism belongs in the shared packages (Ostium).

**Options the ADR compares (OQ-12)**
- **(a) A path of Decisya's own.** Limited to the identity operations Decisya needs, and reached only through audited commands.
- **(b) A path from the shared identity packages.** A request to Ostium, stated as a dependency of EP-12.
- **(c) A fallback if (a) or (b) can disable but not delete.** Disable the identity and remove its email, name and credentials. Allowed only if the narrow legal review confirms before the first real user that this is enough, and the privacy notice lists what stays (AC-13-5). Each such identity is deleted once deletion is possible. It needs the same access as deletion.
- **(d) A manual operator step in the admin console.** Not acceptable as a standing procedure. It may serve the invite-only beta only if the narrow legal review and the business owner both accept it, each step is recorded through an audited admin command, the closure stays pending until then, and it is reported open until (a) or (b) replaces it. It covers a closure's identity step only, so it does not by itself let the beta open.

**Rule until the ADR is accepted (OQ-39, strict reading)**
- A closure deletes the identity through (a) or (b).
- In every option Decisya ends all sessions at once on the server when the closure takes effect (AC-12-3). It deletes its profile and the link last, only after the deletion is confirmed (under (c) the disabling, under (d) the recorded step).
- For a closure through support, the identity is deleted only once the closure is due: the grace period has ended and no restore request is open (AC-28-10).
- During the grace period the identity is not disabled. The block is held in Decisya's own account state (AC-12-3, AC-28-7).

**Conditions.** COND-02: before EP-24 starts, Decisya states which path it uses, what it takes and when it can be in place. COND-03: the planned dates exclude the path and are re-planned once it is known.

Not needed (so the ADR must not give the right): creating users (sign-up goes through Keycloak's own flows), reading passwords or key material, changing realm settings, managing clients, roles or identity providers, reading any user of another purpose, and any listing of all users.

Does it change behaviour someone relies on? Operators and Marco rely on "no application component can change the realm" (security review 17, G4-17-10; ADR-0016). The ADR adds a narrow, audited right for the application. That is a behaviour change for Marco, the security reviewer and anyone who runs the deploy guards: the release and the ADR-0016 amendment announce it, and the realm guard tests change from "no `realm-management` mappings" to "exactly these mappings".

## 3. Quality targets the ADR must meet

| Target | NFR | Measure |
| --- | --- | --- |
| Least privilege: only OP-1 to OP-8 (as G2 settles them); 0 realm-wide roles; no `admin-cli`, no master-realm credential in any application component | **NFR-70 (new)**; relates NFR-43, NFR-47, G4-17-06/10 of review 17 | Realm-file and Integration tests |
| Audit of every call: one intent record before the call and the outcome after; no email, name, credential or token in it | **NFR-71 (new)**; relates NFR-05 of the plan (append-only, "who" is the internal id), BL-B1 | Integration test |
| Tenant scope: a call acts only on the identity tied to the case or the due closure that triggered it; two-tenant tests per operation | **NFR-72 (new)**; CLAUDE.md invariant 1 | Two-tenant Integration tests |
| Unreachable identity service: ledger and sign-in of enrolled users unaffected; no closure completes unconfirmed; failed calls retried; closure or case shows pending, then overdue; retry interval and warning point stated by the ADR | **NFR-73 (new)**; relates NFR-09 and NFR-06 of the plan, AC-12-5, AC-12-8, AC-12-9; CLAUDE.md invariant 2 | Integration test that stops Keycloak |
| No standing broad admin right: non-human client, token at most 300 s, secret outside source and SPA, rotatable, never a human session token, admin interface still unreachable from the internet and the user-facing hosts | **NFR-74 (new)**; relates NFR-15, NFR-44, NFR-66, ADR-0016 R9 | Guard tests, network test |
| Operator actions in the console keep the device-bound level | existing NFR-65, NFR-66 | Unchanged |
| Admin events retained 90 days | existing NFR-46 | Unchanged; the ADR states whether the application's calls appear in them |

The numeric values the ADR must still choose (retry interval, warning point before the closure's deadline, credential rotation period) depend on the legal review's response window (AC-12-8), which is not known today. The ADR states them or names who sets them; I do not guess them.

## 4. User stories

Stories are for the ADR, independently deliverable in this one docs PR. The plan gate for each is the subscription feature key: none. The ADR is platform work and gates no plan feature (`n/a`, reason: no end-user feature).

### Story 1: the decision itself

As Marco (owner and decider), I want an ADR that compares the ways the application can reach the identity service's admin functions, so that I can accept one and the re-plan (COND-03) rests on a known effort and known dates.

```gherkin
Scenario: The ADR compares the options
  Given the ADR for the admin path from the application to the identity service
  When Marco reads its "Considered options" section
  Then it compares OQ-12's options against the same criteria:
    | option |
    | (a) a path of Decisya's own, limited to the identity operations Decisya needs, reached only through audited commands |
    | (b) a path from the shared identity packages: a request to Ostium, a dependency of EP-12, where the path runs through the realm package, the Keycloak baseline, the BFF package or Jwt |
    | (c) the fallback that disables the identity and removes its email, name and credentials, with its conditions (narrow legal review, privacy notice) |
    | (d) the manual operator step, as a beta stopgap only, with its conditions |
  And the criteria include least privilege, audit, tenant scope, no standing broad admin right, behaviour when the identity service is unreachable, effort, and dates
  And each option states which of OP-1 to OP-15 it covers, and how each use it does not cover is met

Scenario: The OQ-39 strict rule is the starting point
  Given OQ-39 is not yet decided
  Then the ADR's closure sequence follows the strict reading in section 2a
  And any departure is stated as a change with its reason

Scenario: The conditions are answered
  Given COND-02 and COND-03
  Then the ADR states which path Decisya uses, what it takes and when it can be in place
  And it states that the planned dates exclude the path and are re-planned once it is known

Scenario: Anything in Ostium's kit is requested, not built
  Given an option whose path changes the realm package, the Keycloak baseline, the BFF package or Jwt
  When the ADR describes that option
  Then it is a request to Ostium, with the exchange request's content, and adopted from Ostium's release
  And the ADR plans no Decisya-built change to those parts

Scenario: Option (d) is only a stopgap
  Given the ADR recommends or allows OQ-12 option (d)
  Then it states that (d) is allowed only for the beta
  And it names the event after which (d) is switched off
  And it states that the audited command "manual identity step recorded" is refused until (d) is switched on (AC-28-2)
  And no other option is described as manual
```

### Story 2: the operations and their controls

As the support operator (ACT-04), I want the ADR to say which identity operations the application performs for me and which I do myself in the console, so that closures, credential resets and email changes can finish within their deadline without a standing broad right.

```gherkin
Scenario: Every operation is traced
  Given the ADR
  When a reader looks up each of OP-1 to OP-15 of section 2 of these requirements
  Then the ADR gives its criterion (AC-12-5, AC-26-3, AC-28-2 or AC-28-6), its actor, the person it acts for, and its control (step-up, audit, hold or notice)
  And an operation the ADR adds or drops beside this list is marked with a reason

Scenario: No more than the criteria need
  Given the ADR's list of identity-service rights
  Then it lists no right to create users, read secrets, change realm settings, manage clients or roles, or list all users
  And each listed right is named as an operation, not as a role bundle

Scenario: The application's sessions and the identity service's sessions
  Given AC-12-3 leaves the end of an identity-service session to Decisya's design
  When the ADR covers OP-4
  Then it states whether the application ends the identity service's sessions itself
  And, if it does not, it states how a session of the identity service that outlives a closure reaches no page, data or action
```

### Story 3: the edge amendment

As Marco, I want ADR-0016 amended before any application component reaches the admin interface, so that the "admin interface only from the workstation" rule is changed on purpose and not by drift.

```gherkin
Scenario: The ADR amends ADR-0016
  Given ADR-0016 R9.6 and R9.8 (lines 122, 124 to 127 today)
  When the ADR chooses a path that needs a network route from the application to the identity service
  Then it contains an amendment to ADR-0016 with a date
  And the amendment names the network, the one caller (the component) and the one route
  And it keeps the workstation-only rule for the human operator on port 8444 and the refusal of the admin paths on port 8443
  And it states that no published route and no port is added for the application
  And it lists the guard tests that change (the network, port and member lists in `deploy/tests`)

Scenario: A path that needs no new route
  Given the chosen path needs no network route (for example only option (d))
  Then the ADR says so, and ADR-0016 stays unchanged for that path

Scenario: The change is announced
  Given the ADR changes behaviour people rely on ("no application component holds admin rights in the realm")
  Then the ADR names who is told (Marco, the security reviewer, anyone running the deploy guards) and how
  And review 17's finding G4-17-10 is updated or superseded in the follow-up that implements the path
```

### Story 4: measurable targets and behaviour when the identity service is down

As the support operator and as an account owner whose closure is due, I want a failed call to the identity service to leave my closure or case pending, visible and retried, so that nothing completes unconfirmed and the deadline is watched.

```gherkin
Scenario: The ADR states the targets
  Given the ADR
  Then it cites NFR-70 to NFR-74 in `docs/requirements/nfr.md`
  And it states the retry interval, the overdue warning point and the credential rotation period, or names who sets each and when
  And it states how the ADR's tests will check each target

Scenario: Identity service unreachable (the ADR's design must allow this test)
  Given a closure is due
  And the identity service is unreachable
  When the deletion call fails
  Then the ledger reads and writes and the sign-in of enrolled users are not affected
  And the closure stays pending, the account stays blocked and Decisya keeps its link to the identity
  And the call is retried
  And the queue shows the closure as "pending", and as "overdue" from the warning point
  And no closure completes before the identity service confirms (AC-12-5, AC-12-9)

Scenario: No standing broad right
  Given the application's admin credential
  Then it is a non-human credential whose token lives at most 300 seconds
  And it is held outside source and outside the SPA
  And no human session token is used to call the admin interface
```

### Story 5: effort, dates and follow-up issues for the re-plan

As Marco, I want the ADR to give each chosen path's effort, its dependencies and Ostium's dates, and to list its follow-up issues, so that COND-03's re-plan of EP-12, EP-24, EP-26 and EP-28 can be made.

```gherkin
Scenario: Effort and dates (COND-03)
  Given the ADR's chosen option
  Then it states the effort in days for each follow-up issue, as an estimate with its basis
  And, where Ostium is involved, it states the request, Ostium's release date or "no date given by Ostium" with the date asked
  And it states the dependencies between the follow-up issues, EP-24's planned start (2026-12-27) included
  And it states what moves if Ostium gives no date before 2026-12-18 (the issue's target)

Scenario: Follow-up issues
  Given the ADR is accepted
  Then it lists its follow-up issues, each with a title, an owner (Decisya or Ostium), the criterion it serves and its "Done when"
  And the list includes at least: the ADR-0016 amendment's implementation, the realm and client change (an Ostium request where it runs through Ostium's kit), the application's admin client and its audit, the guard and two-tenant tests, and the update of review 17's baseline
  And no follow-up issue builds a change inside Ostium's identity kit

Scenario: Acceptance
  Given the ADR is complete
  When Marco accepts it
  Then its status is "Accepted" with his date
  And the issue's "Done when" is met: accepted, effort and Ostium's dates known, follow-up issues listed
```

## 5. Open questions for Marco

None block G1. These are notes the architect should settle, and Marco confirms at acceptance:

1. **N-1 (end of the beta stopgap).** OQ-12 (d) says it may serve the invite-only beta only, and does not by itself let the beta open. When it switches off is still a product choice. Recommendation: before open sign-up (EP-22's switch), and replaced by (a) or (b) as soon as either exists. Risk: a manual step on an identity breaks the ordering of AC-12-5 and hides closures from the deadline (AC-12-8).
2. **N-2 (design-dependent uses).** OP-14 and OP-15 apply only if Decisya's design uses the admin functions for sign-up and for reading sign-in events. Fewer rights is the safer outcome, so G2 should prefer a design that needs neither. OP-12's call volume is inferred.

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #333 -->
