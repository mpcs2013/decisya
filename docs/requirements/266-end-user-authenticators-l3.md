# Requirements: end-user authenticators at ASVS Level 3 (issue #266)

Issue #266. Manifest: `docs/ai/pipeline/266.md`. Tier: full (authentication strength), docs-only change, G4 and G5 skipped. Origin: exchange G-008 (Greenlight, 2026-11-02).

Sources read: `CLAUDE.md` (security principles), `docs/security/asvs-l2-baseline.md` (V6.3.3, C-01, C-02), `docs/requirements/phase-0/production-identity.md` (D1, Story 3, Story 5, NFR-44), `docs/security/threat-models/production-identity.md`, `docs/security/threat-models/keycloak-realm.md` (T-16), `docs/security/threat-models/system-baseline.md` (C-02), `deploy/keycloak/production/realm-decisya.json`, the `keycloak` skill, exchange G-008 and contract D-012.

## Who needs this, and what changes for them

- **Who:** (a) the future end users (consumers who sign up themselves); (b) the platform operators (`platform-admin`), who today use TOTP; (c) Greenlight, which holds D-012 (Level 3 for authentication and session) as a contract and whose gate waits on this decision (G-008 point 4: decide within November 2026, report a slip before the month ends).
- **Behaviour change:** none in this issue. It writes a decision and no code, realm or BFF change. The decision, once implemented in later issues, changes behaviour for platform admins (TOTP is replaced). Admins are one person today (Marco), so the announcement is the ADR itself plus the later realm issue's runbook. If the ADR relaxes the strict reading, Greenlight relies on D-012's Level 3 guarantee, so the change is announced to it in G-008 and as a superseding baseline (Done when).
- **Plan gate (addendum check):** none. Authentication is a platform baseline for all plans; no entitlement feature key applies. No financial amounts and no advice content are involved, so the disclaimer criterion does not apply.

## Given constraints (Marco, G-008)

1. MFA for every end user from the first non-synthetic sign-up (C-02).
2. The strict reading of ASVS 6.3.3 at Level 3: only device-bound, attested authenticators count. Synced passkeys and TOTP do not.
3. Decisya gives no financial advice. It is a consumer personal-finance product, with self-service sign-up and no regulated payment initiation in Phase 1.
4. Current state (do not misstate in the ADR): admin MFA is TOTP and V6.3.3 is recorded as Fail (C-01 for admins, C-02 for all users). The production realm has level 1 (password) and level 2 (OTP, for `platform-admin`), with the tenant-user switch off. No WebAuthn flow exists yet.

## Decision criteria the ADR must weigh

Each criterion is scored per option, for end users and for admins separately.

| # | Criterion | What the ADR must say |
| --- | --- | --- |
| K1 | Phishing resistance | Which options resist real-time phishing and adversary-in-the-middle relay; origin binding of the passkey. |
| K2 | Account takeover | Credential stuffing, password reuse, session theft after sign-in (BFF holds the tokens, so token theft from the browser is out of the picture), and a stolen device. |
| K3 | Cloud-account compromise | A synced passkey is only as strong as the cloud account that syncs it (platform account, password manager). Device-bound keys do not have this path. The ADR states the residual risk and who bears it. |
| K4 | Attestation | Whether the realm can verify authenticator model (attestation conveyance, AAGUID allow-list) and what synced passkeys give (usually no usable attestation). The ADR must check this against the Keycloak version in use, not assume it. |
| K5 | Accessibility and sign-up completion | Expected drop-off when a hardware key or a specific device is required; WCAG 2.2 AA of the enrolment and sign-in screens; users with disabilities who cannot use some authenticators. |
| K6 | Device coverage | Phones, desktops, shared or work computers, Firefox (the project default) and Chromium, platform authenticators, security keys over USB, NFC and Bluetooth. |
| K7 | Recovery | Lost, broken or replaced authenticator, with no weaker fallback (see targets). The ADR names the recovery path and what it costs the user. |
| K8 | Cost | Cost to the user (hardware keys) and to Decisya (support load, build and test effort). Solo developer: the effort of a solution is a real constraint. |
| K9 | Admin versus end-user risk | Admin writes are cross-tenant, so admin risk per account is far higher; the number of admins is small and they can be issued keys. End users are many, unmanaged and self-registered, but each can reach only their own tenant. The ADR may decide different classes per role and must say why. |
| K10 | Contract and compliance | What it does to D-012, to the ASVS baseline (V6.3.3, V6.4, V6.5, V7) and to the claim Decisya makes to Greenlight. |

## Measurable outcome targets

These are targets for the later implementation issues; the ADR must adopt, adjust or reject each with a reason. The decided rows are in `docs/requirements/nfr.md`: T1 = NFR-63, T2 = NFR-64 and NFR-69 (the recovery hold, notice, deletion grace and email/name lock), T3 = NFR-65, T4 and T5 = NFR-66, T6 = NFR-67, T7 = NFR-68 (T8 stays here; it restates invariant 2). Decided by Marco 2026-11-03: Option B, so T6 applies; T1 and T2 hold with no recovery codes; re-enrolment restores the original level. Targets, category in ISO/IEC 25010 terms:

| Id (to be numbered when added) | Category | Target |
| --- | --- | --- |
| T1 | Security (authenticity) | 0 MFA paths that use SMS, voice or an email link, for enrolment, sign-in or recovery; 0 end-user passwords and 0 password-reset paths (P-1). |
| T2 | Security (authenticity) | 100% of recovery flows end at an authentication level not lower than the level the account held before recovery; re-enrolment restores the original level. Recovery by email control, and every support case that resets, adds or replaces a credential, deletes the account, or performs a sensitive operation, waits a hold of at least 7 days, or 14 days for an account that ever held a device-bound key, and is announced; the next sign-in shows an in-app notice that offers a stop, 100% of stopped cases go to operator review and 0 complete without it; support-route deletion is restorable for a grace period at least as long as the hold; email and name change only at the `device-bound` level or through support, on every path. SMTP delivery is a prerequisite of the first non-synthetic sign-up. 0 recovery flows that grant a session at a lower level. Refined by NFR-69. T1 is unchanged: the email proves control of the mailbox for recovery only after the hold and is never an MFA factor or a sign-in. |
| T3 | Security (authenticity) | The Keycloak master-realm operator account holds 2 device-bound keys and no TOTP (P-2). Each account has at least 2 registered authenticators of an allowed class before it is treated as recoverable (or the ADR states the lockout consequence for accounts with 1). |
| T4 | Security (accountability) | The `acr` (or equivalent authentication-level claim) of an end-user session is set by Keycloak only, is one of a closed list defined by the ADR, and names the class used. The ADR states which level an end-user session carries per allowed class and which level an admin session carries. A session that used a class below the role's required class never carries the required level. |
| T5 | Security | 100% of `/api/admin/**` requests whose token lacks the admin authenticator level are refused (NFR-44 successor); the ADR states the new required level. |
| T6 | Security | If synced passkeys are allowed: operations the ADR lists as sensitive (for example, changing authenticators, exporting data, deleting the account, and any future money-moving action) require a device-bound authenticator in the same session, with a stated maximum age of that proof. |
| T7 | Usability / Accessibility | Enrolment and sign-in screens meet WCAG 2.2 AA with 0 axe violations in the Firefox and Chromium E2E projects; the ADR states the target sign-up completion rate or the measurement to take. |
| T8 | Reliability | MFA sign-in does not depend on the AI lane or on an external service beyond Keycloak (invariant 2). |

## Acceptance criteria for the ADR itself

G3 and G6 check these. "The ADR" means the new file under `docs/adr/` that G2 writes.

```gherkin
Feature: The ADR decides end-user and admin authenticators at ASVS Level 3

  Scenario: The ADR names the allowed authenticator classes per role
    Given the ADR for issue #266
    Then it lists, for the role "end user" and for the role "platform-admin", each allowed authenticator class
    And it lists the classes that are explicitly not allowed, including TOTP, SMS, voice and email links
    And it states, for synced passkeys, "allowed" or "not allowed" for each role, with no "maybe"

  Scenario: The ADR applies the given constraints
    Given the ADR for issue #266
    Then it states that MFA is required for every end user from the first non-synthetic sign-up (C-02)
    And it states that TOTP does not count as the second factor at Level 3
    And it states that platform admins move from TOTP to device-bound authenticators

  Scenario: The ADR weighs every decision criterion
    Given the criteria K1 to K10 in this document
    Then the ADR addresses each one for each option it considers
    And it considers at least these options: strict device-bound for all, and a relaxation for end users

  Scenario: A relaxation states its rationale and mitigating controls
    Given the ADR allows synced passkeys for end users
    Then it contains a fully documented rationale
    And it lists the mitigating controls, each as a testable statement with a later issue or a named owner
    And it states the sensitive operations that need a device-bound authenticator (target T6)
    And it states the residual risk and who accepts it
    And it quotes the wording of ASVS 6.3.3 that it relies on, checked against the official 5.0 text

  Scenario: The ADR records cloud-account compromise as an accepted residual risk
    Given Marco decided on 2026-11-03 that end users may use synced passkeys
    Then the ADR states the tradeoff plainly: synced passkeys improve sign-up accessibility, and compromise of the cloud account that syncs them stays possible
    And it names cloud-account compromise as an accepted residual risk, accepted by Marco, with the date
    And it preserves the safeguards: no SMS or email-link fallback (T1), recovery never lowers the level (T2), and a device-bound proof for sensitive operations (T6)
    And it states that the relaxation is revisited before any regulated payment initiation

  Scenario: The ADR names R-RECOVERY as an accepted residual
    Given G3 found threat T266-02 (email control alone passes recovery) and Marco decided on 2026-11-03 to accept a residual
    Then the ADR names the residual "R-RECOVERY": whoever controls the registered email can take over the account after the hold
    And it records the residual as accepted by Marco on 2026-11-03
    And it lists the controls that bound it: the hold, the in-app notice and stop, the restorable deletion grace period, and the email and name lock
    And it states that the residual is revisited before any regulated payment initiation

  Scenario: Admins and attestation follow the decision
    Given the ADR for issue #266
    Then it requires platform admins to use device-bound, attested authenticators before the first non-synthetic tenant, with TOTP removed and two keys per admin
    And it requires attestation for the device-bound class only
    And it records what Keycloak at the pinned version can verify, checked by G2, or states that it cannot

  Scenario: The recovery procedure is minimal and restores the original level
    Given the ADR for issue #266
    Then it requires a second registered authenticator at enrolment
    And it describes the support procedure for total loss as the smallest safe set of steps, with identity checks
    And it states that every support case that resets, adds or replaces a credential, deletes the account, or performs a sensitive operation waits the hold and is announced
    And it states a hold of at least 7 days, or at least 14 days for an account that ever held a device-bound key
    And it states an in-app notice at the next sign-in that offers a stop, that 100% of stopped cases go to operator review, and that 0 complete without it
    And it states that support-route deletion is restorable for a grace period at least as long as the hold
    And it states that email and name change only at the "device-bound" level or through support, on every path
    And it states that SMTP delivery is a prerequisite of the first non-synthetic sign-up
    And it states that re-enrolment restores the user's original authentication level
    And it states that no recovery codes exist

  Scenario: A strict decision states its cost
    Given the ADR requires device-bound authenticators for end users
    Then it states the expected effect on sign-up completion and the mitigation for users without a hardware key or a supported device
    And it states how accessibility needs are met

  Scenario: The ADR states the recovery path
    Given the ADR for issue #266
    Then it describes recovery for a lost or replaced authenticator, for an end user and for an admin
    And it states that recovery never lowers the authentication level (target T2)
    And it states that no SMS or email-link fallback exists (target T1)
    And it states what happens to an account that has lost every authenticator

  Scenario: The ADR states the authentication level an end-user session carries
    Given the ADR for issue #266
    Then it states the level claim for each allowed class (target T4)
    And it states who enforces it (Keycloak sets it, the Api and BFF check it)
    And it names no concrete runtime value as a requirement; values are chosen by the realm implementation issue

  Scenario: The ADR states what changes in D-012
    Given the ADR for issue #266
    Then it states whether D-012's Level 3 guarantee for authentication and session stays whole, or is superseded
    And if superseded, it names the new baseline contract, what it still guarantees, and what it no longer guarantees
    And it states that Greenlight is told in exchange thread G-008, after Marco accepts

  Scenario: The ADR updates the baseline records
    Given the ADR for issue #266
    Then it states how the ASVS baseline rows V6.3.3, V6.4.4 to V6.4.6 and V6.5 and the register rows C-01 and C-02 change when the work lands
    And it states the future issues (realm, BFF, Api check, enrolment and recovery screens, tests) as a list without implementing any of them

  Scenario: The decision is dated and sized
    Given the ADR for issue #266
    Then its status is "proposed" until Marco accepts it
    And it is accepted before the end of November 2026, or the slip is reported in G-008 before that month ends

  Scenario: The ADR stays inside its scope
    Given the diff of the issue
    Then it changes only docs (the ADR, the architecture note, the threat model, requirements, the pipeline files)
    And it changes no source, realm file, BFF code, test or workflow
```

## Out of scope

- Implementation: the realm change (WebAuthn flows, attestation policy, level mapping), the BFF change (requested level, step-up), the Api check, the enrolment and recovery screens, the tests, and the C-02 switch itself. Each is a later issue.
- Changing the admin check (NFR-44) or the go-live gate file; the ADR only states what changes.
- The payment or open-banking authentication (PSD2 strong customer authentication); no regulated payment initiation in Phase 1.
- Choosing the hardware key vendor or buying keys.
- Password policy and breached-password checks (C-02 remainder, V6.2.x): unchanged by this issue.

## Open questions

None open. All decided by Marco on 2026-11-03.

**Q1. End-user class: decided, Option B (Marco, 2026-11-03).** Synced passkeys are allowed for end users under ASVS 6.3.3's relaxation clause, with T1, T2 and T6, an explicit superseding baseline for D-012, and a revisit before any regulated payment initiation. The tradeoff, stated plainly: synced passkeys improve sign-up accessibility, and they leave cloud-account compromise as an **accepted residual risk**. The ADR records that explicitly and preserves the safeguards.

**Q2. Admins: decided, yes (Marco, 2026-11-03).** A hard move to device-bound, attested authenticators before the first non-synthetic tenant, with TOTP removed and two keys per admin.

**Q3. Recovery: decided, yes, qualified (Marco, 2026-11-03).** A second authenticator is required at enrolment. Total loss goes to a support procedure with identity checks, kept as small as safely possible. Re-enrolment restores the user's original authentication level. No recovery codes.

**Q3a. Recovery by email control (G3 finding T266-02, High): decided (Marco, 2026-11-03).** Marco accepts the named residual **R-RECOVERY**: whoever controls the registered email can take over the account after the hold. Bounded by: a hold of at least 7 days, or 14 days for an account that ever held a device-bound key, applied to recovery by email and to every support case that resets, adds or replaces a credential, deletes the account, or performs a sensitive operation (G6-266-01); an in-app notice at the next sign-in that offers a stop, with every stopped case going to operator review and none completing without it; support-route deletion restorable for a grace period at least as long as the hold; email and name changeable only at `device-bound` or through support, on every path; SMTP as a prerequisite of the first non-synthetic sign-up. Targets in NFR-69.

**Q4. Attestation: decided, yes (Marco, 2026-11-03).** Required for the device-bound class only. G2 verifies what Keycloak at the pinned version can check.

**Q5. Date: decided, yes (Marco, 2026-11-03).** The ADR is accepted by 2026-11-20.

**Instruction to G2 (from the earlier Q5 on wording):** quote ASVS 6.3.3 from the official 5.0 text before relying on it.

Note for G2 (PASS-WITH-NOTES): whether Keycloak can enforce device-bound-only for the step-up and verify attestation is unverified; the ADR must state the finding and not assume it.

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #266 -->
