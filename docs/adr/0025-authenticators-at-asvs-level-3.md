# 0025. Authenticators at ASVS Level 3: synced passkeys for end users under 6.3.3's relaxation clause, device-bound attested keys for platform admins

- Status: Accepted (Marco, 2026-11-03)
- Date: 2026-11-03
- Deciders: Marco
- Tags: security
- Issue: #266. Origin: exchange G-008 (Greenlight, 2026-11-02). Requirements: `docs/requirements/266-end-user-authenticators-l3.md` (K1 to K10, T1 to T8), NFR-63 to NFR-68. Architecture note: `docs/architecture/266-end-user-authenticators-l3.md`. Threat delta: `docs/security/threat-models/266-end-user-authenticators-l3.md` (G3, PASS-WITH-NOTES).
- Revised 2026-11-03, before acceptance: G3's MUSTs M266-01 to M266-05 and Marco's acceptance-point decisions of 2026-11-03 (manifest `docs/ai/pipeline/266.md`) are carried in the text below.
- Refines [ADR-0002](0002-keycloak-identity-provider.md): the MFA mechanism changes from TOTP to WebAuthn. Keycloak stays the only identity provider. Related: [ADR-0001](0001-multi-tenant-single-instance.md) (one tenant per end user, cross-tenant admin), [ADR-0003](0003-bff-session-pattern.md) (BFF session), [ADR-0016](0016-phase-0-hosting.md) (synthetic users only in Phase 0), [ADR-0019](0019-ostium-identity-packages.md) (identity code in Ostium; the BFF is frozen until its switch), exchange D-008 (Ostium owns the Keycloak baseline) and D-012 (Decisya's baseline at the Phase 0 exit).

## Context and problem statement

Decisya reviews authentication (ASVS V6) and session management (V7) at Level 3 (#40, in force since 2026-09-23; carried in D-012). Greenlight's Phase 1 plan applies that bar to every end user. In G-008, Decisya gave Greenlight two constraints:
1. MFA for every end user from the first non-synthetic sign-up (go-live gate C-02).
2. The strict reading of 6.3.3's Level 3 clause: only device-bound, attested authenticators count. Synced passkeys and TOTP do not.

Decisya also said it would consider an ADR that accepts synced passkeys for end users under 6.3.3's relaxation clause. It promised to decide within November 2026.

**Current state.** None of this is built.
- Admin MFA is TOTP. The production realm has two levels: level 1 is the password, and level 2 is OTP for `platform-admin` (`decisya-browser` flow, #121).
- The tenant-user OTP switch (`level-2-tenant`) is off. Every user is synthetic (ADR-0016).
- No WebAuthn flow, policy or credential exists in either realm.
- The ASVS baseline records V6.3.3 as **Fail** (C-01 for admins, C-02 for all users).
- The go-live gate closed C-01 for TOTP, but TOTP does not meet the Level 3 hardware clause. The gate's C-02 switch-on still turns on TOTP for tenant users.

Forces:
- **Security.** A finance product with self-service sign-up faces credential stuffing and real-time phishing. Admin writes cross tenants (ADR-0001, ADR-0012), so one admin account can reach every tenant.
- **Accessibility and sign-up.** WCAG 2.2 AA is a constraint. Most consumers have no hardware security key. Most consumer passkeys today are synced by the platform (phone or desktop keychain, password manager).
- **Solo-developer time.** Every flow, screen and recovery step is built, tested and supported by one person. The support procedure must be small.
- **Keycloak at the pinned version** (26.7.5, `src/Decisya.AppHost/ContainerImages.cs`; 26.8.1 pending in #268; the baseline is Ostium's under D-008). Its capability to tell synced from device-bound credentials is not verified (section 8).
- **Contract.** Greenlight relies on D-012's Level 3 guarantee. A relaxation needs a superseding baseline (D-012's change process).

## ASVS 5.0 wording relied on

**Source and check.** This repository holds no copy of the ASVS text. On 2026-11-03 the orchestrator compared the wording with OWASP's tagged source: `OWASP/ASVS`, tag `v5.0.0`, file `5.0/en/0x15-V6-Authentication.md`. The record is in the manifest `docs/ai/pipeline/266.md`. The architect and G3 rely on that record; neither fetched the text.

- **Section heading (verbatim):** "V6.3 General Authentication Security".
- **V6.3 introduction (verbatim):**
  - "L3 applications must use hardware-based authentication, performed in an attested and trusted execution environment (TEE)."
  - "This could include device-bound passkeys, eIDAS Level of Assurance (LoA) High enforced authenticators," followed by NIST Authenticator Assurance Level 3 (AAL3) authenticators and "an equivalent mechanism".
- **6.3.3, a Level 2 requirement.**
  - Its opening words are verbatim: "Verify that either a multi-factor authentication mechanism or a combination of single-factor authentication mechanisms,".
  - Its Level 3 hardware clause and its relaxation clause are present. They are **confirmed in substance only**, because the check tool limits quote length. The exact sentences are as recorded in exchange G-008:
    - Level 3 clause: "a hardware-based authentication mechanism which provides compromise and impersonation resistance against phishing attacks while verifying the intent to authenticate by requiring a user-initiated action (such as a button press on a FIDO hardware key or a mobile phone)";
    - relaxation clause: "Relaxing any of the considerations in this requirement requires a fully documented rationale and a comprehensive set of mitigating controls."
- **This decision depends only on the substance of those two clauses,** not on their exact wording.

**The reading relied on.** The introduction's "L3 applications must use hardware-based authentication …" explains 6.3.3's Level 3 clause. It is not a separate requirement. So 6.3.3's relaxation clause applies to it.

**What this ADR relaxes, read against that wording.** A synced passkey with user verification meets these parts of the Level 3 clause:
- phishing and impersonation resistance: the credential is bound to Decisya's origin;
- intent to authenticate: a user-initiated action, with user verification.

It does **not** meet these:
- "hardware-based": the private key leaves the device to be synced, through a cloud account;
- "performed in an attested and trusted execution environment": synced passkeys usually give no usable attestation.

The relaxation covers exactly those two considerations, for end users only. The first sentence of 6.3.3 (MFA to access the application) is not relaxed for anyone.

## Decision drivers

- K1 to K10 of the requirements, weighed per option and per role (table below).
- No factor weaker than the class a role needs, anywhere: sign-in, enrolment, recovery (T1, T2).
- Admin risk is cross-tenant. End-user risk is the tenants that user can reach (K9).
- One person builds and supports it (K8).
- Decide only what Keycloak can be shown to do, or name the unknown and stop the implementation there (K4).

## Considered options

1. **A. Strict: device-bound, attested authenticators for every role.** Rejected for end users, because most consumers own no device-bound authenticator. It would mean a hardware key bought before sign-up, or exclusion, with no accessible alternative for users who cannot use a key (K5, K6). It also brings a support load for lost keys that one person cannot carry (K8).
2. **B. Relaxation for end users: synced passkeys allowed for end users under 6.3.3's relaxation clause, with mitigating controls. Device-bound, attested keys for admins and for end-user sensitive operations.** **Chosen** (Marco, 2026-11-03).
3. **C. TOTP (or another one-time code) as the second factor, extended to all users.** Rejected: TOTP is phishable in real time (K1), fails the Level 3 clause outright, and is banned by T1 and NFR-63.
4. **D. Synced passkeys for every role, admins included.** Rejected: one compromised cloud account would give cross-tenant write power (K3, K9). Admins are few and can be issued keys, so the strict reading costs little for them.

Hosted identity providers and SMS, voice or email-link factors were not considered again: ADR-0002 rules out the first, T1 the second.

### Criteria per option

End users (role `tenant-user`):

| # | A. Device-bound only | B. Synced passkeys + device-bound step-up (chosen) | C. Password + TOTP |
| --- | --- | --- | --- |
| K1 Phishing | Resists relay and AiTM (origin-bound) | Resists relay and AiTM (origin-bound, both classes) | Phishable in real time |
| K2 Takeover | No password to stuff; a stolen key still needs its PIN or biometric | Same as A for sign-in; end users have no password (P-1). Session theft is unchanged (tokens stay in the BFF, ADR-0003) | Stuffing stopped only by the code; codes can be relayed |
| K3 Cloud account | No path | **Accepted residual risk R-CLOUD** (decision 3). Sensitive operations still need a device-bound proof | No cloud path; the TOTP seed can be synced by authenticator apps too |
| K4 Attestation | Required; verifiability at the pinned Keycloak unknown (section 8) | Required for the device-bound class only; synced passkeys give none | None |
| K5 Accessibility, sign-up | Lowest completion: a hardware key before sign-up; some users cannot handle a key | Highest: the device's own unlock (biometric, PIN, pattern), which the platform makes accessible | Medium: a second app and typing codes |
| K6 Devices | Security keys (USB, NFC, Bluetooth) on every browser; device-bound platform authenticators vary | Phones and desktops with a platform keychain or password manager, in Firefox and Chromium; cross-device sign-in from a phone. Coverage to be measured (NFR-68) | Any device with an app |
| K7 Recovery | Lost keys are support cases | A second passkey at enrolment covers most losses. Total loss is a support case, with accepted residual R-RECOVERY (decision 6) | Seed loss is a support case; recovery codes banned (T1) |
| K8 Cost | User buys keys; high support load | Low for most users; support for total loss and for sensitive operations without a device-bound key; SMTP needed for notices (#277) | Low build cost; no Level 3 |
| K9 Role risk | The tenants the user can reach | The tenants the user can reach; residuals bounded to them | The tenants the user can reach |
| K10 Contract | D-012 holds whole | D-012 superseded by a documented relaxation (decision 9) | Fails 6.3.3 Level 3; not acceptable |

Platform admins (role `platform-admin`, cross-tenant; and the Keycloak master-realm operator account, decision 5):

| # | Device-bound, attested, two keys (chosen) | Synced passkeys | TOTP (today) |
| --- | --- | --- | --- |
| K1 | Resists relay and AiTM | Resists relay and AiTM | Phishable |
| K2 | Key plus PIN; a stolen key alone fails | Cloud-account path | Password plus relayable code |
| K3 | No path | One cloud account gives cross-tenant writes: not acceptable | — |
| K4 | Attestation and model allow-list (if verifiable, section 8; otherwise witnessed enrolment, decision 7) | None | None |
| K5 | One or two known people; keys issued to them | — | — |
| K6 | USB or NFC key on the workstation | — | — |
| K7 | Second key held separately; total loss is break-glass at the workstation | — | — |
| K8 | Two keys per admin, bought once | — | — |
| K9 | Highest risk per account justifies the strict class | Rejected | Rejected |
| K10 | Meets the Level 3 clause | Fails it | Fails it (today's C-01 gap at Level 3) |

## Decision outcome

Chosen option: **B**, because it keeps every phishing-resistant property for every sign-in. It also gives end users an accessible sign-up, which Option A cannot. Hardware binding and attestation are kept where one account can do the most harm: admins, the master-realm operator, and end-user sensitive operations.

### 1. Authenticator classes per role

| Class | End user (`tenant-user`) | Platform admin (`platform-admin`) and master-realm operator |
| --- | --- | --- |
| **Synced passkey**: a WebAuthn credential with user verification that a platform or password manager may sync through a cloud account. Any WebAuthn credential registered without verified attestation falls in this class, device-bound or not | **Allowed** (as the sign-in mechanism) | **Not allowed** |
| **Device-bound, attested**: a WebAuthn credential with user verification whose attestation verifies and whose authenticator model (AAGUID) is on Decisya's allow-list of device-bound models | **Allowed**: needed for the sensitive operations in decision 4, optional otherwise | **Required**: two per account |
| Password | **Not allowed.** End users have no password (P-1, Marco, 2026-11-03) | Allowed only as a first step before the device-bound key, never on its own |
| TOTP or any one-time code (app, hardware token) | **Not allowed** | **Not allowed** (removed) |
| SMS, voice, email link or emailed code | **Not allowed** | **Not allowed** |
| Recovery codes (lookup secrets) | **Not allowed** | **Not allowed** |

- **MFA is required for every end user from the first non-synthetic sign-up** (go-live gate C-02). A WebAuthn credential with user verification required is the multi-factor mechanism: something the user has (the credential), plus something the user is or knows (biometric or device PIN). This covers sign-in. Recovery after the loss of every authenticator, and every other support case, is not multi-factor: each is a human support procedure (decision 6).
- **TOTP does not count as a second factor at Level 3**, for any role.
- **Platform admins and the master-realm operator account move from TOTP to device-bound, attested authenticators** (decision 5).
- **Email is never a factor.** It is used only as a contact check inside the human support procedure and as a channel for notices. It never signs anyone in.

### 2. The level model (closed list)

Keycloak sets the session's authentication level. It is carried in the `acr` claim, from this closed, ordered list:

| Order | Level | Proven by | Who can hold it |
| --- | --- | --- | --- |
| 1 | `password` | password only | transitional: Phase 0 synthetic users until C-02 is switched on; an admin between the password and the key steps |
| 2 | `passkey` | an assertion from a synced-class credential, with user verification | end users |
| 3 | `device-bound` | an assertion from a device-bound, attested credential, with user verification | admins (always); end users (step-up only) |

Rules:
- The level names the class actually used and is **never higher than that class**. A synced passkey never yields `device-bound`, even if the physical authenticator happens to be device-bound but was registered without verified attestation (NFR-66).
- The level names in the table are model names. **The literal `acr` strings and Keycloak's level numbers are chosen by the realm issue.** They are Decisya-owned values, set as options in the Api and BFF composition, never defaults in shared code (ADR-0019).
- Any value outside the list, a duplicated claim or a non-string claim counts as no level (fail closed), as `CallerIdentity` does today for `"2"`.
- **Who enforces it:**
  - Keycloak sets the level. Nothing else may.
  - The BFF asks for the level a route class needs: sign-in, admin, or a sensitive operation.
  - The Api checks the level and the age of the proof, from the validated access token, with no Keycloak call per request.
- Once C-02 is switched on, a session below `passkey` reaches no Api endpoint. Admin endpoints need `device-bound`.

### 3. The relaxation: rationale, residual risks, mitigating controls

**Rationale.**
- Synced passkeys keep the two properties that stop the attacks a consumer finance account mostly faces: phishing resistance and user intent. Credential stuffing, password reuse, real-time phishing and code relay all fail against them.
- Option A would exclude most consumers at sign-up, or push them to a weaker factor, and T1 forbids weaker factors.
- An end-user account reaches only the tenants that user can reach: today one, its own (ADR-0001). The enrolment screen tells the user in plain words that a synced passkey is as safe as the account that syncs it, and offers the device-bound option (S266-06, F-4). That is what makes "the user chose it" true.

**Stated plainly.** Synced passkeys improve sign-up accessibility. They also mean that **anyone who takes over the cloud account that syncs a user's passkey can sign in as that user.** That account is the user's platform account or password manager, which Decisya neither sees nor controls. Such an attacker:
- reads all data of the tenants that user can reach;
- makes every write except the sensitive operations of decision 4: transactions, budgets, alerts, categories;
- holds each session for up to 10 h (Keycloak idle 1800 s, maximum 36000 s; BFF absolute 10 h, not sliding);
- can open new sessions for as long as the credential stays registered at Decisya.

The owner regaining the cloud account does not necessarily end this, because a synced passkey can be copied or exported. Removing the compromised credential needs a device-bound key or the support procedure.

**Accepted residual risks, both accepted by Marco on 2026-11-03.** The affected user bears them, for the tenants that user can reach.
- **R-CLOUD.** Cloud-account compromise, with the extent above.
- **R-RECOVERY.** Whoever controls the registered email address can take over the account through the support procedure, including its device-bound capability. This happens only after the hold, and only if the owner does not stop the case after the in-app notice (decision 6).

Decisya refuses six named operations to a `passkey` session (M-1) and never lets it lower or replace the user's authenticators, or change the attributes recovery relies on, in self-service.

**Mitigating controls.** Each control is testable and has an owner or a follow-up issue (named in the architecture note):

| Id | Control | Test target | Owner |
| --- | --- | --- | --- |
| M-1 | Sensitive operations (decision 4) need a `device-bound` proof at most 300 s old in the same session, or go to the support procedure (T6) | NFR-67 | realm, BFF and Api issues |
| M-2 | No weaker path anywhere: 0 flows, required actions or recovery paths that use SMS, voice, an email link, an emailed code, TOTP or recovery codes (T1). End users have no password, so no password-reset path exists | NFR-63 | realm issue |
| M-3 | Recovery never lowers the level: re-enrolment restores the original level, and a recovery that cannot reach it leaves the account locked (T2). This guarantees the level, not who recovers: see M-12 and R-RECOVERY | NFR-64 | realm issue, recovery runbook issue |
| M-4 | Two authenticators at enrolment, for every account (T3) | NFR-65 | enrolment issue |
| M-5 | User verification is required on every WebAuthn credential, in both classes, and an assertion without it is refused | realm-file test; Integration case (S266-02) | realm issue |
| M-6 | Origin binding: the relying-party id is Decisya's own host, an app-owned value set through the realm's placeholders; no extra origins | realm-file test | realm issue |
| M-7 | The level is never higher than the class used, from a closed list, checked fail-closed by the Api (T4, T5) | NFR-66 | realm and Api issues |
| M-8 | The step-up to `device-bound` never offers to register a new credential. A session without a device-bound credential fails the step-up | Integration test on the production realm | realm issue |
| M-9 | No other credential-change path: Keycloak's account console and application-initiated actions (`kc_action`) either cannot change credentials, or need `device-bound`. Fallback: both are disabled for credential actions in the realm | Integration test | realm issue |
| M-10 | Tokens never reach the browser; the session cookie is `HttpOnly; Secure; SameSite=Strict` (unchanged, ADR-0003) | existing NFR-20, NFR-21 | — |
| M-11 | Revisit: this relaxation is reviewed by a new ADR before any of these: regulated payment initiation or any money-moving feature; any feature that stores or uses third-party financial credentials or tokens (account aggregation); tenant memberships for other people. It is also reviewed after a confirmed R-CLOUD incident, and at the latest at each phase exit | review | Marco |
| M-12 | **Recovery identity assurance.** Every support case that resets, adds or replaces a credential, or deletes the account: starts the hold (7 days; 14 days if the account ever held a device-bound credential); shows a pending-case notice in the application at the next sign-in with any registered credential, and sends one by email; can be stopped by the user during the hold, after which it goes to operator review, never straight to completion. Deletion through support is restorable for a grace period at least as long as the hold. The email address and the registered name change only at `device-bound` or through support, on every path (Api, account console, `kc_action`). The operator compares them against the values held at enrolment and the change history, not only the current values | NFR-69; F-5 runbook review by G3 | recovery runbook issue (F-5), realm issue (attribute paths) |
| M-13 | **Notices.** A new credential, a recovery request, a support case, a sensitive operation and a sign-in from a new device each produce a notice, shown in the application and sent by email. Required before the first non-synthetic sign-up | Integration and E2E tests | SMTP issue #277 (F-7), BFF and enrolment issues |

### 4. Sensitive operations (T6)

These need a `device-bound` proof at most **300 s** old, in the same session (P-3, Marco, 2026-11-03):
1. changing authenticators: adding, removing or replacing any credential;
2. data export;
3. account deletion;
4. any future money-moving action (none exists in Phase 1);
5. changing the account's email address or registered name, because recovery relies on both. This applies on every path, including the account console and `kc_action` (M-9, M-12);
6. granting another person access to the user's tenant, once memberships can be granted.

Items 1 to 4 are NFR-67's minimum. Items 5 and 6 are added because each would let a session reached through a compromised cloud account become permanent, or defeat recovery.

**A session without a device-bound credential** cannot do these in self-service. Two consequences follow:
- **Only at enrolment, or through support.** An account adds its first device-bound credential at enrolment, or through the support procedure (decision 6). Adding one later from a `passkey` session would be exactly the escalation M-1 prevents.
- **Data-subject rights.** A user who owns no hardware key is never denied export or deletion. Both go through the support procedure, with its checks, hold and in-app notice (M-12). GDPR lets a controller check identity before acting on a request, and needs that check to be proportionate.
  - **Deletion through support is restorable** for a grace period at least as long as the hold. The data is kept but cannot be reached, then erased. So an attacker who passes the check under R-RECOVERY cannot destroy the records before the owner can react.
  - Export through support adds little to R-CLOUD, because such a session already reads everything. It goes to the address on record, after the hold.
  - Whether hold plus grace period fits GDPR's one-month response window is checked in F-5.

### 5. Platform admins and the master-realm operator

- **Device-bound, attested authenticators only. TOTP is removed. Each admin holds two keys,** registered in one enrolment sitting run by Marco at the workstation. Keys are bought, not issued by Decisya; the vendor is out of scope.
  - The single sitting also closes the first-enrolment race of T121-16.
  - The second key is kept apart from the first.
- **The Keycloak master-realm operator account moves to device-bound keys in the same follow-up** (P-2, Marco, 2026-11-03). It gets two keys, and no TOTP is left on it. There is no exception in decision 9.
  - `verify`'s master-realm check (T121-15) changes from "has an OTP" to "has two device-bound credentials and no OTP".
- **Deadline:** before the first non-synthetic tenant. It becomes a go-live gate item.
- `/api/admin/**` requires the `device-bound` level (NFR-66). This supersedes NFR-44's TOTP level when the Api issue lands.
- The proof's maximum age stays the admin level's 1800 s from #121. The realm issue measures whether the level survives a token refresh past that age (T121-03, observed for the OTP level at 26.7.5). If it does, the Api enforces the age of the device-bound proof itself (section 8, item U-6; S266-04).

### 6. Enrolment and recovery

**Enrolment.**
- Every account registers **two authenticators** of an allowed class before enrolment is complete (NFR-65). End users register no password (P-1).
- An end user may register a device-bound key as one of the two, or as a third. That is the only self-service moment to add one.
- An account with fewer than two allowed authenticators does not finish enrolment and reaches no Api endpoint.
- The registered email is verified at enrolment through #277 (F-7, SMTP). That verification is a contact check, not a factor.

**Recovery: one authenticator lost, another still held.**
- The user signs in with the one still held.
- Removing the lost credential and registering a replacement is "changing authenticators" (decision 4). So:
  - a user with a device-bound credential does it in self-service, after a step-up;
  - a user without one goes to the support procedure.

**Recovery: every authenticator lost.**
- The account stays locked: no session at any level, and no data change. The data is kept.
- Recovery is the support procedure:
  1. **Request.** The user asks through the documented support channel and names the account email. One open case per account; new cases are rate-limited (S266-05, F-5).
  2. **Identity checks:**
     - **control of the registered email address**, shown by answering a support message that carries a one-time reference. This is a contact check by a person, never a link that signs anyone in;
     - **the registered name**, compared with the value held at enrolment and the change history, not only the current value. Both attributes change only at `device-bound` or through support (M-12).

     **What these checks prove:** that the requester controls the mailbox and knows the registered name. **What they do not prove:** that the requester is the owner. No element is independent of the email, and the email is often the same provider account that syncs the passkey. That gap is the accepted residual **R-RECOVERY** (Marco, 2026-11-03). The hold, the in-app notice and the stop below are its controls. ASVS 6.4.4's rule (identity proofing at the enrolment level) is to be checked against the 5.0 text. Self-service enrolment proves no more than this.
  3. **Hold with notice.** The hold lasts at least **7 days**, and **14 days** if the account ever held a device-bound credential (Marco, 2026-11-03). Notice goes out by two routes:
     - by email to the registered address;
     - **in the application** at the next sign-in with any registered credential.

     A sign-in during the hold lets the user stop the case. **A stopped case goes to the operator for review, never straight to completion.**
  4. **Reset.** The operator, at the `device-bound` level, deletes every credential of the account and ends every session. The operator never chooses or sees a new credential.
  5. **Re-enrolment at the original level.**
     - The user registers two new authenticators, as at enrolment.
     - If the account held a device-bound credential before, it gets the `device-bound` capability back only by registering a new attested device-bound credential in the re-enrolment.
     - Until re-enrolment is complete, the account stays locked.
  6. **Record.** The reset is recorded in Keycloak's admin events and in the runbook's log of support cases. A support case holds no personal data in the repository.
- **The same checks, hold, notice and stop apply** when support handles a sensitive operation for a user without a device-bound key (decision 4).
- **No recovery codes exist, and no email link, SMS or one-time code restores access** (T1).

**Admin recovery.**
- With one key lost, the admin signs in with the other key. Marco then registers a replacement in a new workstation sitting.
- With both keys lost, there is no remote procedure. Break-glass is Marco at the workstation, through the master-realm operator account and its own two keys.
- If every admin and master-realm key is lost, the last resort is Keycloak's host-level bootstrap admin, run from the stack folder and then removed. It is documented in F-6 (S266-07).

### 7. Attestation

- **Required for the device-bound class only.** The synced class asks for no attestation and relies on none.
- The device-bound class needs:
  - a verified attestation statement;
  - an AAGUID on Decisya's allow-list of device-bound authenticator models.
- The allow-list is a **committed, reviewed data asset**. It names models known to keep the key on the device. Its source, licence and refresh duty need Marco's approval as new third-party data, as with `common-passwords.txt`. It carries a SHA-256 pin and a test (S266-01).
- There is no runtime call to a metadata service: sign-in depends on nothing beyond Keycloak (T8, invariant 2).
- **An AAGUID on its own proves nothing.** The authenticator writes it itself, and only a verified attestation signature makes it trustworthy. If the pinned Keycloak can only match AAGUIDs and cannot verify the attestation chain (U-1 negative), the realm issue **stops and reports to Marco**. It does not ship AAGUID matching as "attestation". Marco then chooses between:
  - an Ostium-provided verification;
  - for admins only, enrolment witnessed at the workstation;
  - another decision.

**What changes on a negative from the spike (M266-04):**
- **U-1 negative, witnessed enrolment chosen for admins.**
  - The admin class becomes "device-bound, enrolled under witness at the workstation". It is no longer "attested".
  - Decision 9's admin bullet is amended to say so before the go-live gate's admin item closes.
- **U-1 negative, for end users.**
  - The end-user `device-bound` class is suspended, because an AAGUID match cannot be trusted.
  - Every sensitive operation then goes to the support procedure, so M-12 carries the full load.
- **Re-open rule.** Any of these re-opens decision 1 and decision 9 by a dated amendment to this ADR, and Greenlight is told in the same thread:
  - a negative on U-1;
  - a negative on U-5;
  - a negative on U-7, unless the account console and `kc_action` credential actions can simply be disabled (M-9's fallback).

### 8. What Keycloak at the pinned version can do

Pinned version: **26.7.5** (`ContainerImages.KeycloakTag`). The 26.8.1 bump is #268. The baseline belongs to Ostium (D-008).

**Verified in this repository at 26.7.5:**
- the level-of-authentication mechanism: `conditional-level-of-authentication` sub-flows with `loa-condition-level` and `loa-max-age`, the realm attribute `acr.loa.map`, role conditions (`conditional-user-role`), and the `acr` default client scope;
- from #121's spike D7 and integration tests:
  - a requested non-essential `acr_values` raises an admin to the level, and the role condition keeps a tenant user at level 1;
  - without the request, an admin gets level 1, so the BFF parameter is load-bearing;
  - the level survives two token refreshes;
  - a REQUIRED authenticator with no credential forces its setup required action;
  - listing any required action in the realm file drops Keycloak's defaults.
- WebAuthn: **nothing.** The repository has no WebAuthn policy, flow, credential or test, and no document about WebAuthn behaviour at any Keycloak version.

**Upstream behaviour the realm issue must confirm.** These come from the architect's knowledge of upstream Keycloak documentation for earlier 26.x releases. They are **unverified at 26.7.5**:
- Two realm-level WebAuthn policies: "WebAuthn Policy", used by the two-factor WebAuthn authenticator, and "WebAuthn Passwordless Policy", used by the passwordless authenticator. Each sets:
  - the relying-party id and name;
  - signature algorithms;
  - attestation conveyance preference;
  - authenticator attachment;
  - discoverable credential;
  - user verification requirement;
  - timeout;
  - "avoid same authenticator";
  - acceptable AAGUIDs;
  - extra origins.
- The two policies create two distinct credential types, which could map onto the two classes.
- Passkey sign-in (conditional mediation) has been added to the username form in recent 26.x releases.
- A level-to-`acr` mapping exists at realm and client level, with a client "minimum ACR" setting.

**Unknown; the realm issue's first step settles each item on the pinned version.** A negative on U-1 or U-5, or on U-7 without the disable fallback, stops the spike and applies decision 7's re-open rule:

| Id | Question | Why it matters |
| --- | --- | --- |
| U-1 | Does Keycloak verify the attestation statement's certificate chain against trust anchors, or only match the AAGUID? Trust anchors could come from a truststore or a metadata snapshot | Decision 7: without chain verification there is no attestation |
| U-2 | Can a policy require attestation ("direct") and refuse "none", while the other policy accepts "none"? | The two classes |
| U-3 | Authenticator attachment: is it only a hint to the browser? Under the WebAuthn standard it is a client preference, not a security check, and a synced platform keychain is also "platform". | **Attachment cannot be the device-bound test.** Only attestation plus the allow-list can be |
| U-4 | Does Keycloak read the backup-eligible and backup-state flags, and can a policy refuse a backup-eligible credential? | A second signal for "not synced" (S266-01); no such policy setting is known |
| U-5 | Can a step-up sub-flow at the `device-bound` level accept only the device-bound credential type? When the account has none, does it fail without offering registration (`userSetupAllowed: false`)? | M-8. Today's OTP sub-flow forces setup, which would be a bypass here |
| U-6 | After a step-up, which token claim proves the time of the device-bound proof (for example `auth_time` with `max_age`)? Does the level fall back after its maximum age on refresh? | M-1's 300 s and the admin 1800 s, enforced by the Api (S266-04) |
| U-7 | Can the account console and application-initiated actions (`kc_action`) be stopped from changing credentials, the email address or the registered name below `device-bound`? If not, can they simply be disabled? | M-9, M-12 |
| U-8 | Which required actions are on by default at the pinned version (TOTP, recovery codes, WebAuthn registration, password update)? | M-2 needs TOTP and recovery codes off. Because of the defaults rule above, the realm must list its required actions explicitly. That reverses #121's choice to rely on the defaults |
| U-9 | Can registration require two credentials before completion, and can it run without a password? | M-4 and P-1 (decided: no end-user password). A custom required action would be new code (Ostium or Decisya); a negative adds work but does not re-open the decision |
| U-10 | Can the Firefox and Chromium E2E projects drive WebAuthn ceremonies? Playwright's virtual authenticator is Chromium-only, as far as known | NFR-68 and the test plan. A test library for a software authenticator would be a new package that needs approval |
| U-11 | Does an impersonated session carry a level from the closed list? (S266-03) | Impersonation is disabled for the `decisya` realm, or an impersonated session reaches no Api endpoint |

### 9. D-012: the superseding baseline

D-012's Level 3 guarantee for authentication and session does **not** stay whole. It is superseded by a new baseline contract once Marco accepts this ADR. The text for the orchestrator to draft into the exchange:

> **Level 3 for authentication (V6) and session management (V7) stays Decisya's review bar, with one documented relaxation.** For end users (role `tenant-user`), ASVS 5.0 6.3.3's Level 3 clause (hardware-based, attested authentication) is relaxed under 6.3.3's own relaxation clause (ADR-0025). An end user signs in with a passkey: a WebAuthn credential with user verification, which a platform or password manager may sync through a cloud account. End users have no password.
>
> **Still guaranteed, as targets (see "In force"):**
> - Multi-factor sign-in for every user from the first non-synthetic sign-up: a WebAuthn credential with user verification required. Recovery after the loss of every authenticator, and every other support case, is not multi-factor: each is a human support procedure with a contact check through the registered email, a hold and a notice (below).
> - Phishing-resistant, origin-bound WebAuthn for every sign-in.
> - No SMS, voice, email-link, emailed-code, one-time-code (TOTP) or recovery-code factor anywhere. Email is used only as a contact check inside the human support procedure and for notices. It never signs anyone in and is never a factor.
> - Two authenticators per account at enrolment.
> - Recovery restores the account's original level, never a lower one, and a recovery that cannot reach it leaves the account locked. This guarantees the level, not who recovers: see R-RECOVERY.
> - Every support case (credential reset or replacement, account deletion, a sensitive operation for a user without a device-bound key) waits for a hold of at least 7 days, or 14 days for an account that ever held a device-bound key. It is announced by email and in the application at the next sign-in with any credential. A sign-in during the hold can stop it, and a stopped case goes to operator review. Deletion through support is restorable for a grace period at least as long as the hold.
> - Platform admins and the Keycloak master-realm operator account sign in only with device-bound, attested authenticators, two each, with no TOTP, before the first non-synthetic tenant. `/api/admin` requires that level.
> - End-user sensitive operations need a device-bound proof at most 300 s old in self-service, and otherwise go to the support procedure. They are: changing authenticators, data export, account deletion, changing the email address or registered name, granting tenant access, and any money-moving action.
> - Notices, in the application and by email, of a new credential, a recovery request, a support case, a sensitive operation and a sign-in from a new device, before the first non-synthetic sign-up.
> - Sign-in depends on nothing beyond Decisya's own Keycloak.
>
> **No longer guaranteed:**
> - That every end-user session meets 6.3.3's hardware-based, attested clause.
> - **R-CLOUD** (accepted residual risk, Marco, 2026-11-03). Whoever controls the cloud account that syncs an end user's passkey can sign in as that user.
>   - They read all data of the tenants that user can reach, and make every write except the sensitive operations above (transactions, budgets, alerts, categories).
>   - Each session lasts up to 10 h, and new sessions can be opened for as long as the credential stays registered at Decisya.
>   - Regaining the cloud account does not necessarily end it, because a synced passkey can be copied or exported.
>   - Removing the credential needs a device-bound key or the support procedure.
> - **R-RECOVERY** (accepted residual risk, Marco, 2026-11-03). Whoever controls the registered email address can take over the account through the support procedure, including its device-bound capability. This needs the hold to pass without the owner stopping the case after the in-app notice.
>
> **Revisit:** a new Decisya ADR before any of these: regulated payment initiation or any money-moving feature; any feature that stores or uses third-party financial credentials or tokens (account aggregation); tenant memberships for other people. Also after a confirmed R-CLOUD incident, and at the latest at each phase exit.
>
> **Conditional on Keycloak:** these guarantees assume the realm issue confirms three behaviours on the pinned Keycloak:
> - attestation-chain verification;
> - a step-up that never registers a credential;
> - no credential change outside the step-up, or the account console's and application-initiated credential actions disabled.
>
> A negative on any of them re-opens this text by a dated amendment to ADR-0025, and Greenlight is told in the same thread. If admin keys can only be enrolled under witness at the workstation, not attested, the admin bullet is amended to say "device-bound, enrolled under witness" before the go-live gate's admin item closes.
>
> **In force:** the guarantees describe the target once the implementation issues land. Until then the ASVS baseline keeps V6.3.3 as Fail (C-01 at Level 3, C-02), and every user stays synthetic (go-live gate).

- Change process (D-012's own): a new Decisya contract thread, with `superseded-by` set on D-012, accepted by Marco.
- Greenlight is told the outcome in G-008 **after Marco accepts**. For Greenlight's discovery, the given constraint of G-008 point 2 changes for end users only.

### 10. Baseline records when the work lands

These are not changed by this issue. Each is changed by the issue that lands the matching control. The row ids below are to be checked against the 5.0 text first.
- **V6.3.3:**
  - Fail → Pass for admins and the master-realm operator (device-bound);
  - Pass under the documented relaxation (ADR-0025) for end users.
  - It stays Fail until both the admin move and the C-02 switch-on land.
- **V6.4.4 to V6.4.6:** N/A → assessed against decision 6 (lost factor, identity checks, admin-initiated reset without choosing the credential), with R-RECOVERY recorded.
- **V6.5:** the TOTP and lookup-secret rows become N/A. Revocation of a lost authenticator and the biometric rules are assessed against WebAuthn.
- **V6.3.5 and V6.3.7:** Fail (Low) → assessed against M-13 when #277 and the notices land.
- **V6.8.4, V7.5.3, V10.3.4:**
  - re-assessed against the `device-bound` admin level and the proof age;
  - V7.5.3 also against M-1 for end users.
- **Register C-01:** closed for TOTP by #121 (go-live gate). It is **re-opened at Level 3** until the admin and master-realm move lands, then closed.
- **Register C-02:**
  - The MFA part closes when the passkey switch-on lands; #277 (F-7, SMTP) is a dependency of that switch-on.
  - The password-check parts (V6.2.4, V6.2.11, V6.2.12) become N/A for end users, who have no password (P-1, S266-08). They stay for admins, who keep the password first step.
- **NFR-44:** superseded by NFR-66 when the Api issue lands.
- **Go-live gate:**
  - C-02's switch-on steps A (tenant TOTP) must not be used for a real user from now on. The realm issue replaces them.
  - The admin and master-realm move becomes a gate item.
  - #277 (F-7) becomes a prerequisite of C-02.

### 11. Decided at acceptance points (Marco, 2026-11-03)

- **P-1. End-user password: none.**
  - End users sign in with a passkey only. This removes credential stuffing, password-reset paths and brute-force lockout used as denial of service.
  - The MFA claim rests on user verification being required and enforced (M-5).
  - If U-9 shows that Keycloak's registration cannot run without a password, the realm issue adds a custom required action, in Ostium or Decisya. The decision stands.
- **P-2. The Keycloak master-realm operator account moves to device-bound keys** in the same admin follow-up: two keys, no TOTP, no exception in decision 9.
- **P-3. Values.**
  - The end-user proof age stays **300 s**.
  - The recovery hold is **at least 7 days**, or **14 days** for an account that ever held a device-bound credential. This replaces the 72 h first proposed.
  - G3 reviews the exact operational values in F-5.
- **P-4. SMTP (F-7) is created now as its own issue: #277** ("feat(identity): outbound email (smtp) for sign-in, recovery and security notices (adr-0025 f-7)"). It is a prerequisite of the first non-synthetic sign-up, so it is a dependency of C-02. Notices (M-13), email verification at enrolment and the support procedure depend on it. It needs its own threat model, as a new external integration.

### Consequences

- Good:
  - Every user gets phishing-resistant sign-in. No SMS, email-link, TOTP, password-reset or recovery-code path is left to attack.
  - Accessible sign-up through the device's own unlock, in Firefox and Chromium.
  - Admins and the master-realm operator meet the Level 3 clause in full. One stolen password or cloud account no longer reaches cross-tenant writes.
  - The level a session carries names the class used, so the Api can decide per route class.
- Bad:
  - **Accepted residual risk R-CLOUD** (Marco, 2026-11-03). Whoever controls a user's syncing cloud account:
    - reads all data of the tenants that user can reach;
    - makes every write except the sensitive operations;
    - keeps opening sessions until the credential is removed at Decisya.

    Regaining the cloud account may not end it.
  - **Accepted residual risk R-RECOVERY** (Marco, 2026-11-03). Whoever controls the registered email can take over the account through support after the hold, unless the owner stops it. Restorable deletion, the in-app notice and the stop limit the damage, but do not remove the path.
  - End users without a device-bound key need support for the sensitive operations and for replacing a lost passkey. Every such case waits at least 7 days. The support load is real for one person, and is measured once real users exist.
  - SMTP (#277, F-7) becomes a new external dependency before the first real sign-up.
  - Users with no passkey-capable device and no security key cannot sign up. The measurement is NFR-68's completion rate; the target comes from Greenlight and Marco.
  - Much is unverified in Keycloak (U-1 to U-11). Attestation (U-1), the no-registration step-up (U-5) and the account console (U-7) can each re-open decisions 1 and 9.
  - The BFF part needs a step-up mechanism. The BFF is frozen until its Ostium switch (ADR-0019), so the mechanism is requested from Ostium as an option-driven feature. Decisya keeps the values.
  - #121's choice to rely on Keycloak's default required actions is reversed: the realm lists them explicitly (U-8).
  - The realm change must land while every user is synthetic (the rebuild path, #121 D2). Otherwise it needs C-03b's non-destructive migration.
  - Breaking for Marco as admin and master-realm operator: TOTP stops working on the switch, and two keys per account must be enrolled first.
  - No `CLAUDE.md` invariant changes. The security principles name no MFA factor.
- Enforced by (added by the follow-up issues, listed in the architecture note):
  - realm-file tests on the production realm: allow-listed authenticators and required actions, both WebAuthn policies, the level map, no setup on step-up;
  - Integration tests on the production realm: NFR-63 to NFR-66, M-5, M-8, M-9, and the attribute paths of M-12;
  - Api coupling tests: every `/api/admin` endpoint and every sensitive-operation endpoint carries its level policy;
  - the identity check in `stackctl verify`, which counts:
    - admins and master-realm operators with fewer than two device-bound credentials;
    - any OTP credential;
    - accounts with fewer than two authenticators;
  - NFR-69 (recovery hold, in-app notice and stop, restorable deletion, email and name lock, R-RECOVERY), and G3's review of the F-5 runbook (M-12);
  - E2E with axe (NFR-68), and the notice cases (M-13).

## Amendments

<!-- Dated, attributed additions only; earlier text is never rewritten. -->
