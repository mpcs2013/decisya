# ASVS 5.0 Level 2 baseline

Filled at the Phase 0 exit by issue #27 (0.15), using the `asvs-checklist` skill: L2 everywhere, with V6 and V7 at L3. Control-by-control results and evidence: `docs/security/asvs-l2-baseline.md`. Findings and their classification: the register in `docs/security/threat-models/system-baseline.md`. Assessed at `main` `a3e56dc`, 2026-10-03.

| Chapter | Status | Evidence | Notes |
| --- | --- | --- | --- |
| V1 Encoding and sanitization | Pass | asvs-l2-baseline.md § V1 | EF Core only; raw SQL banned by IL rules |
| V2 Validation and business logic | Fail (1) | § V2 | V2.4.1 anti-automation: C-09 |
| V3 Web frontend security | Pass (1 Low) | § V3 | HSTS max-age: Low, #83 |
| V4 API and web service | Pass | § V4 | |
| V5 File handling | N/A | § V5 | No file feature yet |
| V6 Authentication (L3) | Fail | § V6 | MFA and password checks: C-01, C-02 |
| V7 Session management (L3) | Pass with Fails | § V7 | Admin step-up: C-01; two Lows |
| V8 Authorization | Pass | § V8 | |
| V9 Self-contained tokens | Pass | § V9 | |
| V10 OAuth and OIDC | Pass (1 Fail) | § V10 | V10.3.4: C-01 |
| V11 Cryptography | Pass | § V11 | Rotation procedures: C-04, C-05 |
| V12 Secure communication | Fail (deployment) | § V12 | Internal TLS: C-11 |
| V13 Configuration | Fail (deployment) | § V13 | Secret store and rotation: C-20; environment pin: C-04 |
| V14 Data protection | Pass (retention linked) | § V14 | Retention: C-04 |
| V15 Secure coding and architecture | Fail (1) | § V15 | Rate limiting: C-09; SBOM: #29 |
| V16 Security logging and error handling | Fail (1) | § V16 | Authentication events: C-10 |
| V17 WebRTC | N/A | | Not used |
