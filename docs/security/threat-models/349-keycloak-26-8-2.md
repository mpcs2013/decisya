<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #349 -->
# Threat delta: Keycloak 26.8.2 for the 2026-11-22 scan drift (java Critical, guava) (#349)

- Written 2026-11-23 by security-reviewer (G3, full tier, class ci-tooling), before G4.
- **Scope.** Only the planned change (manifest `docs/ai/pipeline/349.md`):
  - the Keycloak pin moves from `26.8.1@sha256:3f3c5f8e…7332` to `26.8.2@sha256:7e66a96e4739239070aeaf72e4c5decf0f7f682cfc725b9f7ea2b336eaba38b3` under Marco's cooldown waiver of 2026-11-22;
  - the five #268 `keycloak` entries (E-1 to E-5) on the new digest;
  - the guava row;
  - the 26.8.2 `kc-admin-api` client scope and CVE-2026-19688.

  `image_scan.py`, ADR-0015, ADR-0022, `ci.yml`, the Compose overlay, the wrapper, the realm files and the non-keycloak entries do not change, and I did not re-audit them.
- **Sources read:**
  - the manifest; ADR-0015 with both amendments; ADR-0022;
  - `exceptions.json`;
  - `image_scan.py`: `exception_errors` :222-295, `resolve_tag_state` :405-426, `_keeps_fixed_exception` :634-644, `evaluate` :647-699, `select_mode` :818-838, `finding_key`/`new_findings` :952-1000, `compare_alias` :1014-1083, `run` :1113-1183;
  - `ContainerImages.cs`; `AppHost.cs` :59-150; `images.Dockerfile`; `deploy/compose/docker-compose.yaml` (Keycloak service); `docker-compose.stack.yaml` :91-136 and :294-324;
  - `deploy/tests/test_stackctl.py` :708-747;
  - both realm files (in full or searched); `RealmGuard.cs`; the Identity tests that call the Admin API or check scopes and audiences (`RealmConfigurationTests`, `ProductionRealmImportTests`, `ProductionIdentityCheckTests`, `BffLoginFlowTests` :90-97 and :429-442);
  - the `keycloak` skill;
  - the precedents `keycloak-26-8-1-268.md`, review `268.md` and `325-scan-drift-pg-redis.md`; `asvs-l2.md` :69-91.
- **Data, not verified by me.** The scan rows, the digests, the push time and the 26.8.2 behaviour come from the manifest and from O-047 as the caller quotes it. I ran no scanner, read no registry, and did not open the exchange or Keycloak's release notes.

## Verdict

**PASS-WITH-NOTES.** 26.8.2 is the right pin. It clears CVE-2026-114938 (Critical) and CVE-2026-114939 to -114942 on the 26.8.1 pin, and it adds no new row: the six rows left are the five #268 keys plus guava.

G4 may start under M-1 to M-5 once Marco has decided D-A and D-B. Two parts of the stated Done-when cannot be met as written:

1. **Guava cannot become a time-limited exception (F-1).**
   - GHSA-xxph-c9ww-hj94 is a `fixed` `java-archive` finding (guava 33.7.2).
   - `evaluate()` keeps an exception for a fixed finding only under ADR-0015 item 3 (OS packages: `apk`, `deb`, `rpm`; :634-644). For every other type it records "exception not applied: a fix is now available" (:681-693).
   - A guava entry would therefore pass the validator and then never apply. That is a false record, the T325-01 class.
   - ADR-0022 item 2 says any way round D-2 is an ADR-0015 amendment with its own G3, never an entry.
   - So the Done-when "every remaining keycloak finding is a time-limited exception" cannot hold for guava. The scheduled scan on `main` will keep showing exactly one blocking keycloak row.
   - Marco amends the Done-when (D-A), as he did for #268 and #265.
2. **E-1 to E-5 still match on the new digest, but their texts would be false (F-2).**
   - Matching is `image`, `package` (`artifact.name`) and `id` only (:680). The package version is not part of it.
   - So the move of `java-21-openjdk-headless` to `1:21.0.13.1.2-1.2.el9` does not stop E-1 from matching, and the four `sqlite-libs` entries match too.
   - All five texts, however, name the 26.8.1 digest, build `26.8.1-2`, O-035 and openjdk `1:21.0.13.0.9`. The sqlite evidence they cite belongs to the old digest.
   - They are rewritten for 26.8.2 with fresh evidence (D-B, M-2).

CVE-2026-19688 does not touch Decisya in a way that needs action beyond this bump (ruling 3). No High or Medium flaw was found in unchanged code.

## Trust boundaries (delta)

- **TB-1** quay.io → the Keycloak pin. A third-party image enters by digest **inside its cooldown** (waiver).
- **TB-2** `exceptions.json` → the scan decision: the PR delta run, the scheduled drift run and `stackctl`.
- **TB-3** Keycloak token issuance → the Keycloak Admin API. New in 26.8.2: the Admin API checks the token's audience, and the built-in `kc-admin-api` scope (`oidc-admin-api-audience-mapper`) adds that audience. Today the only Admin API callers are the test fixtures (master `admin-cli` password grant) and, on a future deploy, the operator's `kcadm.sh` on 8444.
- Unchanged: browser | BFF | API, and the stack networks (`idp`, `pg-kc` internal; nothing deployed).

## Rulings

### 1. Guava GHSA-xxph-c9ww-hj94

- **Route: no entry.** The reason is F-1 above. The route is unchanged since #268 (T268-04) and the ADR-0022 G3 (T216-15).
  - **PR gate.** The key `(keycloak, guava, java-archive, GHSA-xxph-c9ww-hj94)` is High at the 26.8.1 base, so in delta mode it reads `at base` and does not block. M-4 proves this on the run.
  - **Scheduled and dispatched runs on `main`.** It stays a `FAIL` row.
  - **Deploy.** `stackctl check` and `up` refuse until a Keycloak release carries guava 33.7.2 or later (D-008).
- **Reachability.** The advisory text is not in my inputs, so I cannot rule class (a) or (b).
  - It does not change the route: D-2 voids an entry whatever the reachability.
  - It does not change the urgency either. The stack is not deployed. The dev Keycloak publishes only on loopback (keycloak skill). Testcontainers and CI use synthetic data.
  - S-3 records the affected API for the drift issue.
- **The 14-day deadline.** ADR-0022 sets 14 days for a High on a LAN request path. For guava it passed around 2026-11-13. OVERDUE is a notification only, and the deadline is accepted as unmeetable (adr-0022 G3, T216-15).
- **If Marco wants a deploy before Keycloak ships the fix:** that needs a new ADR-0015 item for a fixed `java-archive` finding in a vendor image with no vendor release.
  - It would be bounded like the retired item 4: one alias, one package, 14 days, a sunset, an approval phrase and a presence-keyed deploy guard.
  - It needs its own issue, G2 and G3, plus code and red tests in `image_scan.py`. That is a gate change, so it runs in gate-change mode.
  - It never goes inside #349 (S-5).
- **Proposed Done-when amendment, for Marco's approval (D-A):**
  > A scheduled or dispatched image-scan on `main` shows no blocking keycloak row other than guava GHSA-xxph-c9ww-hj94. That row is a fixed `java-archive` finding with no exception route (ADR-0015 D-2, ADR-0022 item 2). It stays tracked under D-008 and as drift, and the deploy gate refuses until a Keycloak release carries guava 33.7.2 or later. Every other remaining keycloak finding is a time-limited exception Marco approved under G3.

### 2. The five #268 exceptions on `7e66a96e…`

| Entry | Key on 26.8.2 | Still matches? | What changes |
| --- | --- | --- | --- |
| E-1 CVE-2026-91403 | keycloak / `java-21-openjdk-headless` | Yes. The version is not part of the key. Non-`fixed`, so it applies until expiry whatever the tag state | Version `1:21.0.13.0.9-1.2.el9` → `1:21.0.13.1.2-1.2.el9`; digest; Ostium baseline O-035 → O-047; build tag; fix state as recorded on this digest; drop the 2026-11-08 ca-certificates sentence (history of 26.7.5) |
| E-2 to E-5 sqlite-libs 119373, 119586, 119374, 119379 | keycloak / `sqlite-libs` | Yes (`not-fixed`) | Digest, build, O-047; the M-2 load evidence redone on this digest, with the evidence path `349.md` |

- **Tag state.** Item 3 matters only if a fix appears.
  - If Red Hat ships a `sqlite-libs` (rpm) fix, the entries stay only while `26.8.2` still resolves to the pin and the span is at most 30 days. A Keycloak rebuild under the same tag ends them at once. That is the right failure.
  - E-1 is `wont-fix`, so only its expiry ends it.
- **Two routes (D-B).**
  - **(R, recommended) A fresh 30-day acceptance under #349.**
    - Each entry is replaced in place, with `issue` `#349`, `added` `D` and `expires` `D + 30` at most. `D` is the date of Marco's approval.
    - This is the "new issue and fresh G3" that #268's texts and backlog S-2 require for E-1 before 2026-12-08. This G3 re-checked the premises: the Keycloak service is still on `idp` and `pg-kc` only, both internal (`docker-compose.stack.yaml` :101-103, :307-318); neither realm file has an identity provider, an SMTP server or a JWKS URL; Keycloak has no egress network. The sqlite load evidence is redone on the digest (M-2).
    - It removes the renewal issue that would otherwise be due within two weeks during the Phase 1 tooling freeze.
    - Cost: one cap-test edit.
    - Risk: a `wont-fix` Critical stays accepted about 15 days longer, with fresh evidence, on a stack that is not deployed. Each further renewal still needs its own issue and G3.
  - **(K) Rewrite in place, keep #268.**
    - `issue`, `added` and `expires` stay as they are (2026-11-08 to 2026-12-08). The text changes as in the table, plus "Rewritten in place for this digest under #349; Marco approved the rewrite on `D`." The wording differs on purpose, so the text keeps exactly one `approved by Marco` phrase (the #325 redis precedent).
    - The cap test does not change.
    - Risk: the E-1 renewal issue is still due before 2026-12-08, and all five lapse on that date.
- **Never:** keeping the 26.8.1 texts unchanged on the new pin (F-2), or a second entry beside an existing key (the validator rejects duplicates, :289-292).

### 3. `kc-admin-api`, the realm guard and CVE-2026-19688

- **CVE-2026-19688 ("Admin API missing audience check", fixed in 26.8.2).** On 26.8.1, the Admin API would accept a token issued to any client. Decisya has no exposure that needs action:
  - **Decisya realm.**
    - No user holds a `realm-management` role: the dev users have only `tenant-user` or `platform-admin`, and the production realm imports no users.
    - `decisya-bff` has `fullScopeAllowed: false`, and its scope mapping is limited to those two realm roles, so its tokens carry no admin role.
    - `admin-cli` has every grant disabled.
    - No client has service accounts enabled.
  - **Master realm.** Only Marco's admin. On the stack the master realm and `/admin*` sit behind 8444 (workstation only).
  - **Exposure today.** Nothing is deployed, and the dev Keycloak publishes only on loopback.
  - **Callers.** No Decisya product code calls the Admin API. The ADR-0026 admin path is future work (S-4).
- **`kc-admin-api`: the risk that 26.8.2 creates.** A client that has this scope among its default or optional scopes gets the Admin API audience in its tokens.
  - For `decisya-bff` that would be harmless today, because its tokens carry no admin role. It would still remove one layer of defence, and the BFF's token is the one most exposed (it lives in the Redis ticket store).
  - Realm-level default scopes would pass the audience to every client created later.
  - A realm that 26.8.2 migrates (the persistent dev realm) can come out differently from a fresh import.
  - What G4 tests is in M-3.
- **The realm guard.** `RealmGuardTests` and `realm-guard-cases.json` form a static scan for new launch paths (#17, #77). They do not depend on the Keycloak version and read no client scopes.
  - They must pass unchanged. A new test under `tests/` that names the realm file is exempt by rule.
  - The `kc-admin-api` check is a runtime check against a running 26.8.2, because the built-in scope exists only there. It is not a realm-guard case.
  - If O-047 point 8 means a guard of Ostium's, Decisya does not copy it. Ostium develops Ostium changes, and a new package needs Marco's approval.
- **The trap G4 must avoid.** The Identity fixtures call the Admin API with a master `admin-cli` password-grant token (`KeycloakRealmFixture.cs:135-160`, `ProductionKeycloakStack.cs:263-280`). On 26.8.2 these calls test the new audience check from the positive side.
  - If they return 401 or 403, stop and report.
  - Never "fix" them by adding `kc-admin-api` to any client or realm file without a G3 re-check.

### 4. The pin change

See M-1 and M-4. The waiver covers **this index digest only**. A rebuild under `26.8.2` is a new image: it gets a new cooldown, or a new waiver from Marco.

### 5. Residual risk of the waived cooldown, at full scope

- **The threat.** A hijacked, malicious or broken `26.8.2` index is pinned and run within 4 days of its push (pushed 2026-11-19 07:42 UTC; the cooldown would end 2026-11-26 07:42 UTC). The cooldown exists to give the community time to notice exactly this.
- **What prevents:**
  - The digest pin: what is scanned, tested and reviewed is byte-for-byte what runs. A later tag move cannot swap the image.
  - M-1's two-way read at G4, with a stop if the tag no longer resolves to `7e66a96e…` (or the amd64 manifest to `75b5c435…`).
  - The official `quay.io/keycloak` repository, the same one every earlier pin came from.
  - No deploy is possible: the deploy gate is shut by guava (D-008) and by the strict full scan (ADR-0022 item 4).
- **What only detects:**
  - Grype (known CVEs only, never a planted payload).
  - The Identity, AppHost, E2E and ZAP suites (broken behaviour, not a stealthy payload).
  - Ostium's independent baselining and suite runs on the same digest (O-047): a second consumer, not a control Decisya owns.
  - Keycloak's own advisories and a retraction or rebuild of the tag, seen only if someone looks (S-1).
- **What nothing in Decisya contains:**
  - **Marco's host.** A malicious Keycloak image runs in the persistent `decisya-keycloak` container and in local Testcontainers.
    - There it holds the Keycloak database password, the BFF client secret, the dev user password and the Aspire-generated admin password, all dev values.
    - It reaches the dev Postgres with the `keycloak` role.
    - It has normal Docker outbound network access, so it could send data out.
    - It shares the Docker engine with other projects' containers (memory: Docker is shared).
    - Only Docker's isolation (the Docker Desktop VM) and the host firewall stand between it and the host. Decisya has no egress control for dev containers.
  - **CI.** It runs on GitHub's ephemeral runners with synthetic data. The job token is not passed into the container.
  - **Impact if it happened.** Dev secrets and synthetic data leak. Those values must then be rotated. Production secrets do not exist yet.
- **The waiver is not needed to meet the deadline.** The cooldown ends 2026-11-26 07:42 UTC, three days before the ADR-0022 Critical deadline (about 2026-11-29). The waiver buys an earlier start for G4, which runs the image on Marco's host. It does not buy a fix that waiting would lose. S-1 keeps the waiver's reach as small as possible within Marco's decision.

## Threats

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T349-01 | TB-1, pin inside cooldown | T, S, I | Hijacked or broken image pinned and run before it could be noticed | Medium (dev host only; not deployed) | Digest pin; M-1 two-way read with stop; Ostium on same digest; S-1 | V15.1.1, V15.2.1 | Accepted by Marco (waiver), residual per ruling 5 |
| T349-02 | TB-2, guava | T, R | A guava entry validates, never applies, and reads as an acceptance that does not exist; or the Done-when is "met" by such an entry | Medium (process) | F-1: no entry; D-A Done-when amendment; M-4 "at base" on the PR | V15.1.1, V15.2.1 | Open, fix-now |
| T349-03 | TB-2, E-1 to E-5 | R | Entries keep matching on 26.8.2 while their texts describe 26.8.1 (digest, java version, evidence) | Low | F-2: rewrite (R or K), fresh M-2 evidence | V15.1.1 | Open, fix-now |
| T349-04 | E-1, JVM trust store | S, I | Weak eMudhra roots trusted by Keycloak as a TLS client | Medium (as T268-02) | Premises re-checked (ruling 2); 30-day bound; renewal needs issue and G3 | V12.3.2, V15.2.1 | Accepted to `D + 30` (R) or 2026-12-08 (K) |
| T349-05 | TB-3, `kc-admin-api` | E | A Decisya client (or every future client through realm defaults, or the migrated dev realm) gets the Admin API audience | Low (no admin roles in decisya-realm tokens) | M-3 tests; owner check on the dev realm; no scope added to fix a failing test | V8.4.2, V9.2.3, V10.3.1 | Open, M-3 |
| T349-06 | 26.8.1 → 26.8.2 behaviour | T, E | A changed option or a new default-on feature (the S-268-01 class) | Medium if it occurs | M-4 upgrading-notes check; `ProductionFeatureTests` | V13, V8.4.2 | Open, M-4 |
| T349-07 | Pin copies | T | The stack or dev runs another image than the one scanned | Low | M-1 three copies; parity test; deploy-guards drift check | V15.2.1 | Mitigated if M-1 holds |

## Findings

- **F-1 (fix-now, Medium, process; T349-02).** No guava entry may be written. The Done-when as stated cannot be met. Marco decides D-A.
- **F-2 (fix-now, Low, a few lines in `exceptions.json`, which the PR changes; T349-03).** E-1 to E-5 are rewritten for the new digest (D-B).

## MUSTs for G4

- **M-1. Pin, provenance and the three copies (T349-01, T349-07).**
  - (a) At G4, before the commit, read `quay.io/keycloak/keycloak:26.8.2` two ways:
    - Quay's tag API (`manifest_digest`, `is_manifest_list: true`, the push time);
    - the registry's `Docker-Content-Digest`, with the index `Accept` header.

    Both must equal `sha256:7e66a96e4739239070aeaf72e4c5decf0f7f682cfc725b9f7ea2b336eaba38b3`, and the linux/amd64 entry must be `sha256:75b5c435a83b530caba5923922f93d47b2e3f8b4ad9e79200581122996653e55`. Record the build tag (`26.8.2-<n>`) if Quay lists one. If any value differs, stop and report: the waiver does not cover another digest.
  - (b) Write the same reference into `ContainerImages.cs` (`KeycloakTag` `26.8.2`, `KeycloakSha256`), `.devcontainer/engine/images.Dockerfile` and `deploy/compose/docker-compose.yaml:41`. The Compose file must equal a fresh publish (the deploy-guards drift check); if hand-edited, only the `image:` value changes.
  - (c) The `ContainerImages.cs` comment records:
    - the push time and the two-way read;
    - Ostium baseline O-047;
    - "cooldown waived by Marco 2026-11-22 for this index digest only (would end 2026-11-26 07:42 UTC); sets no precedent";
    - what the pin clears (CVE-2026-114938 to -114942);
    - the six rows it keeps, with their owners (E-1 to E-5 under D-B; guava under D-008);
    - "Earlier pin (#268): 3f3c5f8e…".
  - (d) Scan the head with CI's pinned Grype (v0.120.0, linux/amd64) and record the database build. The High and Critical rows must be exactly the six. For each, record `artifact.name`, version, type and `fix.state`. `java-21-openjdk-headless` and `sqlite-libs` must be the exact names. Record the guava version (expected 33.6.0-jre). Any other High or Critical row, a changed package name, or a `fixed` state on E-1: stop and report.
  - (e) Record the `java-archive` component diff between `3f3c5f8e…` and `7e66a96e…` (one scanner, one database). This is information for G6, not a stop rule.
- **M-2. Exceptions (F-1, F-2, T349-02 to T349-04).**
  - No `guava` entry.
  - E-1 to E-5 are replaced in place with the D-B texts below, keeping five keys and no duplicate.
  - Before writing E-2 to E-5, record on this digest, with `docker run --network none`:
    - the started Keycloak JVM maps no `libsqlite3` (`/proc/<pid>/maps`);
    - no file under `/opt/keycloak` bundles an SQLite library, a JDBC driver (`org/sqlite/`) or a native `.so`.

    If either fails, write no entry for any of the four, and stop and report.
  - Re-read E-1's Red Hat CVSS from the Grype record. If it is no longer 6.8, correct the sentence and report it.
  - Under R, set the keycloak branch of `deploy/tests/test_stackctl.py:739-742` to `issue == "#349"` and `expires <= "<D + 30>"`, and keep the `dev-only` assertion and the 30-day loop at :744-747. Under K, the test does not change.
  - Never touch `image_scan.py`, `.github/image-scan/Dockerfile`, `ci.yml` or the ruleset.
  - Check `.claude/tests` (outside my search scope) for any keycloak issue or date pin, and change it the same way.
- **M-3. `kc-admin-api` (T349-05).** In the Identity suite, against a running 26.8.2:
  - (a) Dev realm (`RealmConfigurationTests`) and production realm (`ProductionRealmImportTests`):
    - `GET /admin/realms/decisya/default-default-client-scopes` and `/default-optional-client-scopes` contain no `kc-admin-api`;
    - no client in the realm carries it in its default or optional client scopes, except Keycloak's own built-in admin clients (`security-admin-console`, `admin-cli`);
    - the test fails on any other client, and G4 records which built-ins carry it;
    - `decisya-bff`'s existing exact default-scope assertions stay, and the production test also asserts `optionalClientScopes` is empty.
  - (b) `BffLoginFlowTests` :94-97: the access token's `aud` is exactly `["decisya-api"]` (an equivalence, not `Contain`/`NotContain`). This catches the Admin API audience whatever its value. If the current token carries another audience, stop and report before changing the expectation.
  - (c) The existing Admin API calls of the fixtures pass on 26.8.2 with no change to any realm file or client (ruling 3, the trap).
  - (d) **Owner step (Marco; agents never use the admin console or its password).** After the AppHost has recreated `decisya-keycloak` on 26.8.2 against the existing `decisya-postgres-data` volume:
    - in the admin console, realm `decisya`: Clients → `decisya-bff` → Client scopes shows exactly the six defaults and no optional scope;
    - Client scopes → `kc-admin-api` (if listed) has the assigned type "None".

    G4 records Marco's result in the manifest. If either differs, stop and report. Never reset the volume to make it pass: that is Marco's decision (keycloak skill).
- **M-4. Behaviour and suites (T349-06).**
  - G4 records that the 26.8.2 upgrading and release notes rename, remove and re-default none of the options the stack sets: #268's M-5 list, plus `KC_FEATURES_DISABLED`.
  - G4 also records that 26.8.2 turns on no new feature by default. Any change: stop and report. A fix inside #349 needs a G3 re-check.
  - Green on the new digest, within bounded run times, before the PR:
    - the full `Decisya.Identity.Tests`, `Category=Integration` included (realm guard, production stack, `ProductionFeatureTests` for `scim-api`, M-3);
    - the AppHost tests (host);
    - the fast .NET suite, `deploy/tests` and the `.claude` suite.
  - **On the PR:**
    - the `image-scan` mode reads `delta`, and only `keycloak` is scanned with `0 new blocking`;
    - guava reads `at base`;
    - CVE-2026-91403 and the four sqlite ids read `excepted`, and no keycloak line reads "exception not applied";
    - CVE-2026-114938 to -114942 are absent at the head;
    - the validator reports no error;
    - deploy-guards (drift check), build-test, apphost-tests, E2E and ZAP are green.

    Any other mode, or a new row: stop and report.
- **M-5. Approvals and the record after the merge.**
  - Marco approves the D-A and D-B texts in his own words, with the date `D`.
  - `added` = `D`. It is never before the approval and never after the commit date.
  - Each entry carries exactly one `approved by Marco`.
  - Banned in the texts: "dev-only" and IP literals.
  - After the merge, a dispatched `image-scan` on `main` shows:
    - keycloak `FAIL` on the guava row only;
    - five keycloak rows `excepted`;
    - no keycloak `stale` or "exception not applied" note.

    Anything else: stop and report. Postgres and redis rows belong to #347 and #325 S-3, and they do not affect this Done-when.

## SHOULDs

- **S-1 (Marco). Keep the waiver's reach small.**
  - Merge no earlier than 2026-11-26 07:42 UTC unless something new forces haste. The 2026-11-29 deadline is still met, and `main` then never carries a pin younger than the cooldown.
  - At merge time, re-read the tag (M-1 (a)) and check Keycloak's GitHub security advisories for 26.8.2. Record both.
  - Where practical, recreate the persistent dev container (M-3 (d)) after the cooldown end, so the long-lived container with dev secrets runs a post-cooldown image.
  - Risk of not doing it: the ruling-5 exposure lasts 3 days longer on the host.
- **S-2 (backlog #243, and an exchange request to Ostium). Another route for E-1.** E-1 is `wont-fix` and will need a G3 every 30 days.
  - Candidate: a narrowed Keycloak trust store (only the Caddy root, not the system store), if Keycloak 26 allows it.
  - That is the Keycloak path, so Ostium develops it; Decisya does not.
- **S-3 (backlog #243). Guava affected-code record.**
  - G4 copies the advisory's description and affected API from the Grype record into the manifest.
  - The drift issue gets a "not affected" statement (ADR-0022 item 2) if the text supports one. That would close the drift deadline only; it does not unblock the deploy.
- **S-4 (backlog #243, for the ADR-0026 admin path's G3).** The first Decisya Admin API caller uses its own confidential client, with `kc-admin-api` assigned to that client alone, and least-privilege `realm-management` roles. The decisya realm's defaults and `decisya-bff` stay free of it, and M-3 already guards both.
- **S-5 (Marco, only if a deploy is wanted before Keycloak ships guava ≥ 33.7.2).** A new ADR-0015 item for a fixed vendor `java-archive` finding, in its own issue with G2 and G3 (ruling 1). Otherwise no action.

## Proposed exception entries (Marco's approval, D-B)

Under **R**: `issue` `#349`, `added` `D`, `expires` `D + 30` (2026-12-23 if `D` = 2026-11-23). `<fix state>` is G4's M-1 (d) value. `<build>` is the build tag if Quay lists one; otherwise omit ", build `<build>`".

**E-1, R:**

> CVE-2026-91403, java-21-openjdk-headless 1:21.0.13.1.2-1.2.el9 in the Keycloak 26.8.2 image (sha256:7e66a96e4739..., build `<build>`, Ostium baseline O-047): the OpenJDK CA store trusts roots that signed eMudhra intermediates with weak private keys (CWE-295); fix state: `<fix state>`, so no package update ends this entry before its expiry. Grype rates it Critical; Red Hat CVSS 3.1 is 6.8 (preliminary) AV:N/AC:H/PR:N/UI:R/S:U/C:H/I:H/A:N. This package is the JVM that serves Keycloak and it is loaded. Its default trust store is the system ca-certificates store (Red Hat's statement for the RHEL OpenJDK RPMs); this entry rests on reachability alone. Keycloak trusts the store as a TLS client (KC_TRUSTSTORE_PATHS adds the Caddy root to it and does not replace it); browsers do not use this store. Deployed context (#120, ADR-0018 D2): Keycloak is on the internal idp and pg-kc networks only; Caddy admits the LAN to /realms/decisya* and /resources/* on 8443 and the workstation to the admin paths on 8444, and port 9000 is never routed; the stack is not deployed today, and the deploy gate refuses while Keycloak's bundled guava (GHSA-xxph-c9ww-hj94) has no fixed release (ADR-0022, D-008). Reachability: Keycloak's only outbound TLS peer is the back-channel logout call to the app host, which resolves to Caddy's fixed address on the internal idp network; JDBC to Postgres is plaintext on the internal pg-kc network, so no CA store is involved; the realm has no identity provider, no SMTP server and no client JWKS URL; Keycloak has no network with egress. A man in the middle needs a position on idp, which holds only Caddy and Keycloak (both with all capabilities dropped, NET_RAW included), or root on the Docker host, and either is already a compromise of the identity tier. Void if any of these changes: any egress network for Keycloak, any outbound peer outside idp and pg-kc, or an identity provider, SMTP server or client JWKS URL in the realm. History: first accepted on 2026-11-08 under #268 for the 26.8.1 image (sha256:3f3c5f8e03c6..., java 1:21.0.13.0.9-1.2.el9). Keycloak is on the request path of a LAN-reachable service, so under G3 S-120-07 the approval is recorded here: renewed under issue #349 as a fresh 30-day acceptance on this digest, approved by Marco `D`; added `D`, expires `D + 30`, 30 days. Not renewed past this expiry without a new issue and a fresh G3.

**E-2, R** (and E-3, E-4, E-5: the same text with `CVE-2026-119586`, `CVE-2026-119374` and `CVE-2026-119379` in `id` and in the first words):

> CVE-2026-119373, sqlite-libs 3.34.1-11.el9_8 (rpm) in the Keycloak 26.8.2 image (sha256:7e66a96e4739..., build `<build>`, Ostium baseline O-047): Grype rates it High; fix state: not-fixed. sqlite-libs is an operating-system library of the image's base. Reachability (G4 evidence on this digest, docker run --network none, recorded in docs/ai/pipeline/349.md): the started Keycloak JVM maps no libsqlite3, and no file under /opt/keycloak bundles an SQLite library or JDBC driver; Keycloak's store is Postgres over JDBC on the internal pg-kc network, so no request to Keycloak reaches SQLite code. Deployed context (#120, ADR-0018 D2): Keycloak is on the internal idp and pg-kc networks only; Caddy admits the LAN to /realms/decisya* and /resources/* on 8443 and the workstation to the admin paths on 8444, and port 9000 is never routed; the stack's bind-mount table admits no provider or theme JAR; the stack is not deployed today, and the deploy gate refuses while Keycloak's bundled guava (GHSA-xxph-c9ww-hj94) has no fixed release (ADR-0022, D-008). Void if the Keycloak JVM or any provider JAR loads SQLite, or if the bind-mount table admits a provider JAR. History: first accepted on 2026-11-08 under #268 for the 26.8.1 image (sha256:3f3c5f8e03c6...; that digest's evidence is in docs/ai/pipeline/268.md). Keycloak is on the request path of a LAN-reachable service, so under G3 S-120-07 the approval is recorded here: renewed under issue #349 as a fresh 30-day acceptance on this digest, approved by Marco `D`; added `D`, expires `D + 30`, 30 days. Not renewed past this expiry without a new issue and a fresh G3.

**Under K, starting from the current #268 texts:**
- Replace "26.8.1 image (sha256:3f3c5f8e03c6..., build 26.8.1-2, Ostium baseline O-035)" with "26.8.2 image (sha256:7e66a96e4739..., build `<build>`, Ostium baseline O-047)".
- E-1: change the java version, use the recorded fix state, and replace the ca-certificates sentence with "this entry rests on reachability alone."
- E-2 to E-5: change the evidence path to `349.md` and add "(the 26.8.1 digest's evidence is in docs/ai/pipeline/268.md)".
- Insert "Rewritten in place for this digest under #349; Marco approved the rewrite on `D`." before "Not renewed".
- `issue`, `added`, `expires` and "approved by Marco 2026-11-08" stay unchanged.

**Validator check (both routes).**
- The seven `ENTRY_KEYS`, all strings.
- The ids match `ID_RE`, and `keycloak` is in `EXCEPTION_IMAGES`.
- The package names match `PACKAGE_RE`, and `#349` or `#268` matches `ISSUE_RE`.
- Each span is 30 days: at most 90, within S-120-07, and within item 3's 30 days if a fix appears.
- `added` is not in the future, and there are no duplicates.
- The approval-phrase rule binds only `caddy`. Each entry still carries exactly one phrase, not after `added`.

## Decisions for Marco

- **D-A. Guava and the Done-when** (ruling 1).
  - **Recommended:** approve the amendment text above.
  - Risk: `main`'s scheduled scan stays red on one keycloak row, and the deploy stays shut, until Keycloak ships guava ≥ 33.7.2.
  - Alternative: S-5, a new ADR-0015 item in its own issue. Risk: it widens D-2 for a vendor library on the LAN request path.
- **D-B. E-1 to E-5 route and texts** (ruling 2).
  - **Recommended: R**, renewal under #349 to `D + 30`.
    - Risk: a `wont-fix` Critical accepted about 15 days longer, on fresh evidence, with nothing deployed.
  - **K:** keep 2026-12-08.
    - Risk: a renewal issue is still due within two weeks, and if it slips, all five lapse into drift.
  - Either way, approve the texts in your own words with the date `D`.
- **D-C. S-1 timing.** Merge on or after 2026-11-26 07:42 UTC (recommended). Risk: none to the 2026-11-29 deadline unless G4 or CI slips by more than 3 days.

## Could not check

- The guava advisory text (reachability; S-3), and Keycloak's 26.8.2 release and upgrading notes (M-4).
- Whether 26.8.2 attaches `kc-admin-api` to built-in clients in non-master realms, and how its migration treats a realm created on 26.8.1 (M-3 (a), (d)).
- What O-047 point 8 calls the "realm guard" (I did not open the exchange).
- `.claude/tests` pins of keycloak entry issues or dates (outside my search scope; M-2).
- Every registry and scan fact (M-1).
- **Tool note.** Besides the listed commands, I ran `git diff 8b40e30 22f60ee --stat -- deploy/caddy/Caddyfile deploy/keycloak/entrypoint-stack.sh deploy/compose/docker-compose.stack.yaml` (read-only; not denied) to confirm that the Keycloak deployed context had not changed since #268. That command form is not on my list. It showed 8 added lines in the stack overlay only, and Grep showed they are the Api's `ConnectionStrings__ledger` secret (#341), not Keycloak.
