# ASVS 5.0 Level 2 baseline

Filled at the Phase 0 exit by issue #27 (0.15), using the `asvs-checklist` skill: L2 everywhere, with V6 and V7 at L3. Control-by-control results and evidence: `docs/security/asvs-l2-baseline.md`. Findings and their classification: the register in `docs/security/threat-models/system-baseline.md`. Assessed at `main` `a3e56dc`, 2026-10-03.

| Chapter | Status | Evidence | Notes |
| --- | --- | --- | --- |
| V1 Encoding and sanitization | Pass | asvs-l2-baseline.md § V1 | EF Core only; raw SQL banned by IL rules |
| V2 Validation and business logic | Fail (1) | § V2 | V2.4.1 anti-automation: C-09 |
| V3 Web frontend security | Pass (1 Low) | § V3 | HSTS max-age: Low, #83 |
| V4 API and web service | Pass | § V4 | |
| V5 File handling | N/A | § V5 | No file feature yet |
| V6 Authentication (L3) | Fail | § V6 | MFA and password checks: C-02 (open, go-live gate); C-01 closed by #121 |
| V7 Session management (L3) | Pass (2 Low) | § V7 | Admin step-up: C-01 closed by #121; two Lows, #83 |
| V8 Authorization | Pass | § V8 | |
| V9 Self-contained tokens | Pass | § V9 | |
| V10 OAuth and OIDC | Pass | § V10 | V10.3.4: C-01 closed by #121 |
| V11 Cryptography | Pass | § V11 | Rotation procedures: C-04, C-05 |
| V12 Secure communication | Fail (deployment) | § V12 | Internal TLS: C-11 |
| V13 Configuration | Fail (deployment) | § V13 | Secret store and rotation: C-20; environment pin: C-04 |
| V14 Data protection | Pass (retention linked) | § V14 | Retention: C-04 |
| V15 Secure coding and architecture | Fail (1) | § V15 | Rate limiting: C-09; SBOM: #29 |
| V16 Security logging and error handling | Pass | § V16 | Authentication events: C-10 closed by #121 |
| V17 WebRTC | N/A | | Not used |

## Phase 0 exit verdict (2026-10-31)

**Verdict: zero open High.** In `asvs-checklist` terms this is PASS-WITH-NOTES. The notes are the open Mediums listed below, all gated by deployment or by a named trigger.

- Reviewer: security-reviewer, issue #228, on branch `issue/228-phase-0-exit-audit` at `main` `6b50409` (the branch changes docs only).
- Method: I read the status lines of the register (`threat-models/system-baseline.md`), the go-live gate (`docs/runbooks/go-live-gate.md`), and every threat model and review under `docs/security/`. I did not re-audit code. The chapter table above is the 2026-10-03 baseline; rows V6, V7, V10 and V16 were brought up to date on 2026-10-31 with Marco's approval. Since then #121 has closed C-01, C-03 and C-10, and #122 has closed C-09a.
- No threat model or review carries a BLOCK verdict.

### Open High: 0

- **C-03**, the only High in the register (production half of keycloak-realm T-01/T-02), was closed by #121 (go-live gate item C-03; re-checked with `stackctl.py verify` at go-live). What is left of it is C-03b (a non-destructive realm migration). C-03b is Medium after go-live and Low in Phase 0 (production-identity T121-13). It is open at the go-live gate.
- **C-14** is Medium today and becomes High once a Redis or object-storage key holds a tenant. No such key is recorded, so its trigger has not fired. *(2026-11-01: C-14 is closed by #231, which adds the one key builder `TenantKey` and its rule R10/R11. See the register row and `reviews/231.md`.)*
- **High rows still marked "Open" in G3 threat models.** These are G3 requirements that later G6 reviews closed:
  - agent-sandbox T-01 to T-05, T-07 and T-09: closed as register A-21 (review 36).
  - agent-sandbox-docker-sidecar T-41-02: closed as A-22 (review 41).
  - remove-claude-review T95-04: closed as A-14 (key revoked 2026-10-01).
  - image-scan-renew-120 T-05 ("High if done"): a condition on #143's merge path, not a flaw. Review 143 left F-1 to Marco.

### Open Medium: 13, plus 2 accepted

| Id | Item | Held by | Trigger |
| --- | --- | --- | --- |
| C-02 | Tenant-user MFA, breached-password check | Go-live gate | Before the first non-synthetic account |
| C-03b | Non-destructive realm migration (Low until go-live) | Go-live gate | Before the first non-synthetic account |
| C-04 | Environment pin, hash key, OTLP TLS and retention | #29 (Phase 1) | Before any non-dev deployment |
| C-05 | Key ring: #122 done; #30 restore drill outstanding | Go-live gate, #30 (Phase 1) | Before go-live |
| C-09b | Edge rate limit (Caddy, app and id hosts) | Go-live gate | Blocks public-internet exposure |
| C-12 | PII under an innocent placeholder name | #83 (#69) | First issue that handles user free text |
| C-13 | Wolverine tenant and handler discovery | #83 | The issue that adds Wolverine. *(2026-11-01: closed by #231. The durable store is held by #243 item 231-1. See the register row and `reviews/231.md`.)* |
| C-14 | Tenant-keyed Redis or storage key | #83 | The first such key. *(2026-11-01: closed by #231, which adds the builder and its rule. The storage wrapper is a criterion of the storage issue. See the register row and `reviews/231.md`.)* |
| C-15 | `claude-config` runs the PR's own `lint.py`/`gates.py` | #83 (#37) | "Phase 0 exit review": now due, reopened into Phase 1 as #37 and #38 (Marco, 2026-10-31), re-checked at the Phase 1 exit at the latest |
| C-16 | Skip approvals are free text | #83 (#38) | "Phase 0 exit review": now due, reopened into Phase 1 as #37 and #38 (Marco, 2026-10-31), re-checked at the Phase 1 exit at the latest |
| C-18 | Sandbox engine residuals | #83 | Before the next `--with-docker` sandbox run |
| C-19 | Host-leak canary used the real user-secrets store | #83 (#49) | Before the next canary run |
| C-20 | Production secret store and rotation | #29, go-live gate | Before go-live |

Accepted, not counted as open: C-11 (plaintext same-host hops; ADR-0018, deployable-stack T120-10) and C-17 (agent and dependency code run as Marco; ADR-0011).

Closed since the baseline: C-01, C-03, C-10 (#121); C-06 (#120, G4-120-04); C-07, C-08 (#28); C-09a (#122).

### Dependency check

`dotnet list package --vulnerable --include-transitive`, run 2026-10-31 on this branch, found no vulnerable package in any of the 32 projects (advisory source: nuget.org). There is no High or Critical. `npm audit` was not run in this audit.

### Accepted residuals (cited, not re-ruled)

- **Keycloak bundled guava** (GHSA-xxph-c9ww-hj94, fixed in guava 33.7.2; no Keycloak release carries the fix):
  - The deploy gate refuses `check` and `up` by design. ADR-0015 D-2 allows no exception for a fixed `java-archive` finding.
  - Records: ADR-0022; G3 `threat-models/adr-0022-pr-gate-delta.md` (O2 ruling, T216-15).
  - Nothing is deployed. The finding is tracked under D-008 in Marco's waiver of 2026-10-31 (`docs/PHASE-0.md`, "Exit state").
- **#212 go-jose v3** (GHSA-mgcc-cww5-fp2q) in the self-built Caddy image:
  - Unfixed upstream; class (b), not reachable.
  - Approved by Marco 2026-10-29; expires 2026-11-12.
  - Records: review 212, `threat-models/caddy-image-go-jose-212.md`, T225-04.
- **ADR-0015 exceptions** (`.github/image-scan/exceptions.json`):

  | Image | Packages | Issue | Expires |
  | --- | --- | --- | --- |
  | keycloak | pcre2, pcre2-syntax (3 ids each) | #83 | 2026-11-02 |
  | keycloak | java-21-openjdk-headless, ca-certificates | #190 | 2026-11-02 |
  | keycloak | java-21-openjdk-headless | #195 | 2026-11-02 |
  | postgres | brotli-libs, libcurl | #195 | 2026-11-02 |
  | caddy | go-jose v3 | #212 | 2026-11-12 |
  | api, bff | libc6 (CVE-2016-20013) | #190 | 2026-11-23 |
  | migrator | libc6 (CVE-2016-20013) | #119 | 2026-12-31 |

  The 11 entries ending 2026-11-02 lapse two days after this verdict. After that they need a bump or a renewal (new issue and G3). Otherwise they become unexcepted findings that the deploy gate refuses, alongside guava. This does not change today's verdict.
