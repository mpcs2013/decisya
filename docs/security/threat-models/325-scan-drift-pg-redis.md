<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #325 -->
# Threat delta: scheduled scan drift 2026-11-16/18, postgres and redis OpenSSL, api/bff libc6 (#325)

- Written 2026-11-20 by security-reviewer (G3, full tier), before G4. Addendum 2026-11-20 (Redis bundled modules, D-F (a)) at the end; the verdict is unchanged.
- **Scope.** Only the planned change (manifest `docs/ai/pipeline/325.md`, D-1 (A)):
  - the Postgres pin moves from `123b1799…` to a later `18-alpine` rebuild;
  - OpenSSL exceptions (CVE-2026-95691, -95692, -88823, -107496 on `libssl3` and `libcrypto3`) for postgres and redis;
  - the api, bff and migrator libc6 CVE-2016-20013 entries;
  - the `deploy/tests/test_stackctl.py` caps and the files that move with a pin.

  `image_scan.py`, `release_images.py`, ADR-0014/0015/0021/0022, `ci.yml`, the Compose overlay, the keycloak and caddy entries and the `$comment` are unchanged and are not re-audited.
- **Sources read:** the manifest; ADR-0014 item 4; ADR-0015 with item 3 of the 2026-10-06 amendment; ADR-0022 items 1 to 5; `exceptions.json`; `image_scan.py` (`exception_errors` :222-295, `resolve_tag_state` :405-426, `_keeps_fixed_exception` :634-644, `evaluate` :647-699, `scan_targets` :724-780, `select_mode` :818-838, `compare_alias` :1014-1083, `run` :1113-1183); `ContainerImages.cs`; `AppHost.cs` :95-153; `.devcontainer/engine/images.Dockerfile` :20-27; `deploy/compose/docker-compose.yaml` :3, :20; `stackguards.py` :413-449 (S-120-12); `deploy/tests/test_stackctl.py` :696-722; `docs/ai/pipeline/190.md`, `320.md`; the precedents `image-scan-drift-265.md`, `image-scan-renew-120.md` and `deployable-stack.md` (2026-10-06 exception classes).
- **Scan data:** the orchestrator's Grype v0.120.0 scans of 2026-11-20 (database built 2026-11-20 06:21 UTC). I read `grype-pg-b6972e11.json` and `grype-redis-9f8d2d78.json` in full. I did not read `grype-pg-123b1799.json` (too large to read whole) or `grype-pg-ff64c9e3.json`, and I used the orchestrator's summary for them. I ran no scanner and read no registry.

## Verdict

**PASS-WITH-NOTES.** G4 may start under M-1 to M-5, after Marco decides D-A. One part of D-1 (A) cannot work as planned:

1. **The postgres OpenSSL exceptions would never apply on the `b6972e11…` pin (F-1).**
   - On `b6972e11…` Grype reports all four OpenSSL CVEs as `fix.state: fixed` (versions `3.5.9-r1`, first observed 2026-11-19).
   - For a fixed finding, `evaluate()` keeps an exception only under ADR-0015 item 3, and item 3 needs `tag_state == "unchanged"` (`_keeps_fixed_exception`, :634-644).
   - `18-alpine` already resolves to `ff64c9e3…`, so the tag state is `moved`, and every postgres OpenSSL row is `FAIL` with "exception not applied: a fix is now available, bump the image (tag moved …)" (:681-693).
   - The PR's own delta run hides this: the same eight keys are at the base (`123b1799…`), so they count as "at base" and the PR passes. The first scheduled or dispatched run on `main` is red on eight postgres rows.
   - The 8 entries would validate and then never apply. That would be a false record (#265 ruling 2, T265-04), and the Done-when bullet "no blocking postgres row" could not be met.
2. **Redis is fine as planned.** On `9f8d2d78…` the four CVEs match by NVD CPE with `fix.state: unknown`, so `evaluate()` applies the exception whatever the tag state.
3. **The libc6 renewal is fine** as a fresh 30-day acceptance under S-120-07, with the M-3 evidence.
4. **The 38 PostgreSQL 18.6 server Highs** need no message to Marco before 2026-11-22, unless both G4 checks in "Item 4" turn out positive.

No High or Medium flaw was found in unchanged code.

## Trust boundaries (delta)

- **TB-1** Docker Hub → the Postgres pin (and the Redis pin if D-C is taken): supply chain, under the 7-day cooldown.
- **TB-2** `exceptions.json` → the scan decision: the CI schedule, the PR delta, `release_images.py` and `stackctl`.
- **TB-3** Internal peers → Postgres (`pg-app`, `pg-kc`) and Redis (`cache`): unchanged. Neither publishes a port in the stack, and the stack is not deployed (#132 not done, deploy gate shut by guava, D-008).

## Rulings

### 1. Postgres: pin, OpenSSL and the tag-moved behaviour

**The pin `b6972e11…` is acceptable without a waiver.**
- It was pushed 2026-11-15 04:08 UTC, so the cooldown ends 2026-11-22 04:08 UTC.
- The scan JSON shows PostgreSQL 18.7 on Alpine 3.24.4, built from `docker-library/postgres` revision `185fae3519025ac5715b35134e440af1948f4de2`, path `18/alpine3.24`.
- Its High and Critical rows are exactly the eight OpenSSL rows. zlib, krb5-libs, libxslt and the 38 PostgreSQL 18.6 rows are gone.

**The four CVEs, from the advisory text in the scan:**

| Id | Grype severity (CVSS 3.1) | Affected code | Used by Postgres or Redis? |
| --- | --- | --- | --- |
| CVE-2026-95692 | High (7.5) | AES-GCM-SIV IV length in `EVP_CipherInit_ex` / `ossl_aes_gcm_siv_init` (default provider) | No. TLS cipher suites never use AES-GCM-SIV. pgcrypto maps its cipher names through a fixed table (bf, aes, des, 3des, cast5; cbc/ecb; PGP uses CFB), so user input cannot select GCM-SIV. |
| CVE-2026-88823 | High (7.5) | AES-GCM-SIV zero-length AAD update | No, for the same reason. |
| CVE-2026-107496 | High (7.5; OpenSSL itself rates it Low) | `CMS_encrypt` / `CMS_encrypt_ex` with an attacker-supplied EC recipient certificate of group order 1; "TLS and QUIC do not use this path" | No. Neither server does CMS. |
| CVE-2026-95691 | High (8.1, AV:A) | OpenSSL **QUIC client**: retransmitted CRYPTO frame, use after free | No. Neither server is a QUIC client. |

- **Class (b) for all four, in both images: the libraries are loaded, but the vulnerable path is not reachable.**
  - Postgres links both libraries (`--with-openssl`) and calls libcrypto on every login (SCRAM digests and HMAC, `pg_strong_random`). None of those calls reaches GCM-SIV, CMS or QUIC.
  - The argument does **not** rest on TLS being off: TLS 1.2 and 1.3 do not touch any of the four paths. S-120-12 (`stackguards.py:418-449`) keeps TLS off in the stack anyway.
  - It also does not rest on pgcrypto being absent.
- **Other OpenSSL users in the postgres image:** `libcurl`, reached only through `libpq-oauth` in a client-side OAuth flow that no Decisya component uses (Npgsql is managed code); krb5 and openldap (no GSSAPI or LDAP auth configured); `apk`; busybox `ssl_client`. G4 records whether libcurl imports the OpenSSL QUIC client (M-2).

**What happens with the tag (the decisive point):**
- The fix exists in Alpine (`3.5.9-r1`, first observed 2026-11-19), but no Postgres image carries it yet. `b6972e11…` and `ff64c9e3…` both ship `3.5.9-r0`.
- Item 3 reads "no fixed image exists" from "the tag still points at the pin". Here that inference fails twice:
  - the tag moved to a rebuild that is **not** fixed;
  - the 7-day cooldown is longer than the recent rebuild interval of `18-alpine` (11-06, 11-15, 11-18). A pin that has cleared the cooldown is therefore rarely the tag head.
- The code fails closed, which is correct, but it leaves no exception route on `b6972e11…` (S-2).

**The options (decision D-A):**
- **(A4, recommended)** Pin `b6972e11…` on 2026-11-22/23, as D-1 says, and write **no** postgres OpenSSL entries.
  - The eight postgres OpenSSL rows stay red on `main` as drift until a rebuild carrying `3.5.9-r1` clears its cooldown. D-1 already sends that bump to the backlog; it needs a follow-up issue with a date.
  - The Done-when bullet "no blocking postgres row" is amended for these eight rows.
  - It meets the ADR-0022 7-day Critical deadline for postgres zlib, which has blocked since the tag moved on 2026-11-15, so it is due about 2026-11-22/23. The orchestrator's "about 2026-11-30" should be checked against ADR-0022 item 2.
  - Nothing is deployed, and `stackctl` refuses on any non-excepted row.
- **(A2)** Pin `ff64c9e3…` (the tag head) after its cooldown ends on 2026-11-25 22:14 UTC, and merge on 2026-11-26 with the eight postgres entries (P-1 below). Item 3 then applies while `18-alpine` stays at `ff64c9e3…`.
  - Risks:
    - the zlib Critical, the krb5 and libxslt rows and the 38 PG Highs stay on `main` 3 to 4 days longer, and the zlib deadline is missed (a notification, not a block);
    - the api/bff libc6 entries lapse from 2026-11-24 to the merge, so `Release / build` is red on `main` (not a required check; Marco accepted that window in #320 D-1 A);
    - if the tag moves again before 2026-11-26 (the cadence suggests it may), the entries are void again.
- **(A3)** Pin `ff64c9e3…` on 2026-11-22/23 under a recorded one-off cooldown waiver, with the P-1 entries.
  - I do not recommend it. Unlike #265 and #183, this waiver buys no fix that `b6972e11…` lacks. It only makes the bookkeeping work, and it would be the sixth "one-off" waiver (#265 S-1).
- **Expiry for any postgres OpenSSL entry (A2 or A3):** **2026-12-02**, inside the ADR-0022 date the orchestrator gives for OpenSSL. That is at most 30 days, as item 3 requires.
- **Tag-moved behaviour:** the entry ends at once on the next `18-alpine` rebuild. If that rebuild carries `3.5.9-r1`, bump to it after its cooldown and delete the entries. If it does not, the chase repeats. Never extend an entry to cover a moved tag.

**The postgres zlib entry (#265) must be removed (F-2).**
- `b6972e11…` (and `ff64c9e3…`) carry no zlib CVE-2026-4738 row, so the entry matches nothing.
- Its justification describes `123b1799…`.
- Removing it is justified only by the M-1 scan evidence.

### 2. Redis: OpenSSL, TLS and 8.10.2 or 8.10.3

**The OpenSSL entries are acceptable for redis.**
- The image (Alpine 3.23.7) is built with `BUILD_TLS=yes`, so `redis-server` links libssl and libcrypto. The four paths in the table above are still not used.
- The stack's `redis.conf` and Compose service carry no `tls-` option (S-120-12 guard), and the config loads no module.
- The only client is the BFF, on the internal `cache` network, under a session-only ACL.
- Fix state `unknown` (NVD CPE match, `< 3.5.10`) means the entry applies whatever the tag state.
- If Alpine 3.23's secdb later reports a fix, item 3 governs: the entries span 30 days or less, so they stay while the tag is unmoved and end when the tag moves.

**Dev AppHost.** It uses Aspire's typed `AddRedis` and `AddPostgres` (`AppHost.cs:95-153`) with Aspire's defaults. Whether Aspire 13.5 turns on TLS for either with the dev certificate is not visible in the repository. It does not change the ruling, because TLS does not reach the four paths, but G4 records it (M-2) so that no justification says "TLS off" about the dev host.

**8.10.2 or 8.10.3 (decision D-C).**
- I recommend `8.10.3-alpine` (`7958cfbb…`, pushed 2026-11-11; the cooldown ended 2026-11-18). The reason: `8.10.2-alpine` has not been rebuilt since 2026-10-26, although 8.10.3 shipped on 2026-11-11. That fits a patch tag upstream no longer builds.
  - On a frozen tag, item 3's "tag unmoved, so no fixed rebuild exists" is always true and proves nothing. That matters for the redis zlib entry (#265, `fixed`, kept by item 3) and for any OpenSSL entry whose fix state turns `fixed`.
  - On `8.10.3-alpine`, the tag that is still maintained, the premise holds again.
  - It also picks up Redis's own patch fixes. The orchestrator says it carries the same OS findings.
- Cost: G4 evidence on the new digest, and the redis zlib entry's justification rewritten in place for the new digest (its key, `added` and `expires` unchanged), which Marco re-approves.
- If Marco declines, staying on `8.10.2-alpine` is acceptable for this PR. The exceptions apply either way, and the 2026-12-09 zlib expiry bounds the weak premise.
- **Expiry for the redis OpenSSL entries:** **2026-12-02**.

### 3. api, bff, migrator libc6 CVE-2016-20013

**The renewal is acceptable.**
- The release bases are Ubuntu 24.04 (noble) chiseled, not Debian. aspnet is `cae5b962…` (api, bff) and runtime is `b64d614c…` (migrator), from #320.
- They carry `libc6 2.39-0ubuntu8.9`, the same as before (#320 manifest).
- The finding is Ubuntu "Negligible". It is raised to `Elevated(negligible)` because a CVSS score is at least 7.0, and its fix state was `wont-fix`, "Ignored" on noble (#190 M-3, re-checked 2026-10-24).
- The affected code is glibc `crypt()` SHA-crypt with very long keys, which the .NET apps never call.
- Release images are evaluated strictly (`evaluate()` without a tag state). The entries therefore end at once if the fix state ever becomes `fixed`, which is the right failure.
- **Form:** edit in place.
  - `issue` `#325`, `added` = Marco's approval date `D`, `expires` = `D + 30` at most (S-120-07; `test_stackctl.py:719-722`).
  - Exactly one `approved by Marco D`.
  - The first acceptance (2026-10-05, #120) and the #190 renewal stay in the text.
  - Update the base digests from `3ed6558056b5…` to `cae5b962…` / `b64d614c…`.
- **Migrator, aligned (D-1 (A), Marco's call):**
  - set `expires` to the same `D + 30`, which is earlier than 2026-12-31 and so strictly safer, with `issue` `#325`, `added` = `D` and the same evidence;
  - it is not on the request path, so the 30-day loop does not apply to it, but one re-check date for all three is simpler.

### 4. The 38 PostgreSQL 18.6 server Highs on the current pin

- **Not urgent enough for a message before 2026-11-22, on the evidence available.**
  - The current pin runs only in the dev AppHost on Marco's host, in Testcontainers and in CI, with synthetic data.
  - The stack is not deployed.
  - The pin is replaced on 2026-11-22/23 under A4 (2026-11-26 under A2).
- **Two G4 checks can change that.** If both are positive, tell Marco at once, and stop the dev Postgres container until the bump:
  - (a) any of the 38 is exploitable **before authentication**, from the advisory text in `grype-pg-123b1799.json`, which I could not read;
  - (b) the dev AppHost Postgres container (`decisya-postgres` or its persistent equivalent) publishes its port on anything other than loopback (`docker ps` port column).
- Under (b) alone, the Aspire-generated superuser password still guards the server: no message, but record it.

### 5. Caps and other files that change

- **`deploy/tests/test_stackctl.py:696-722`:**
  - `:701-703`: api, bff and migrator `expires` = `D + 30` (2026-12-22 if `D` = 2026-11-22).
  - `:704-710`: replace "every postgres and redis entry belongs to #265" with a per-issue cap: `{"#265": "2026-12-09", "#325": "2026-12-02"}`. Every postgres or redis entry has an `issue` in that map and `expires` at or before its cap; the 30-day span and "no dev-only" stay.
  - Add one assertion: no `postgres` entry carries `#265` (F-2).
  - The `:719-722` loop stays.
- **`.claude/tests`** (outside my search scope): G4 checks for any test that pins postgres or redis entry issues or dates, or the libc6 dates, and changes it in the same way. `test_committed_file_is_valid` must pass.
- **`ContainerImages.cs`:**
  - `PostgresSha256` and the comment: the #325 provenance (push time, two-way read, revision, PG 18.7, Alpine 3.24.4), what the pin fixes (zlib, krb5, libxslt, 38 PG CVEs), what it does not (OpenSSL `3.5.9-r0`), the tag state at commit, "no waiver: cooldown ended 2026-11-22 04:08 UTC", and one line of history, "Earlier pin (#265, waiver 2026-11-09): 123b1799…".
  - Redis likewise if D-C is taken.
  - Do not claim that item 3 covers the postgres OpenSSL rows under A4.
- **`.devcontainer/engine/images.Dockerfile:20-21`** (and `:27` for D-C), kept equal by `ContainerImageParityTests`.
- **`deploy/compose/docker-compose.yaml:3`** (and `:20`), regenerated by the publish, never edited by hand.
- **`exceptions.json`:**
  - remove the postgres zlib entry;
  - add the eight redis OpenSSL entries (R-1);
  - add the postgres OpenSSL entries only under A2 or A3 (P-1);
  - edit the three libc6 entries in place (L-1);
  - under D-C, rewrite the redis zlib justification in place.

## Threats

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T325-01 | TB-2, postgres OpenSSL entries | T, R | Entries are written on a moved tag. They validate, never apply, and read as an acceptance that does not exist; the PR's delta run is green, so the gap shows only on `main` | Medium | F-1; D-A; M-2 (tag state at commit and at merge) | V15.1.1, V15.2.1 | Open, fix-now |
| T325-02 | TB-1, Postgres pin | T, S | A same-tag rebuild is pinned inside its cooldown, or the pin is not the scanned digest | Medium | M-1: two-way read, push time, provenance, scan, a commit after 2026-11-22 04:08 UTC | V15.2.1 | Mitigated if M-1 holds |
| T325-03 | OpenSSL in Postgres and Redis | I, D, T | A vulnerable OpenSSL path is reached from a peer | Low (class (b) for all four CVEs) | Ruling 1 table; M-2 symbol evidence; internal networks only; not deployed | V15.2.1 | Accepted to 2026-12-02 if M-2 holds |
| T325-04 | TB-2, stale #265 postgres zlib entry | R | A stale entry describes a digest that is no longer pinned | Low | F-2; M-1 evidence; the cap test | V15.1.1 | Open, fix-now |
| T325-05 | libc6 in the release images | D | glibc `crypt()` is reached in api, bff or migrator | Low (`wont-fix`, never called) | M-3; strict evaluation ends the entry if a fix appears | V15.2.1 | Accepted to `D + 30` if M-3 holds |
| T325-06 | Frozen patch tag `redis:8.10.2-alpine` | T | Item 3 keeps a fixed-OS exception on a tag upstream no longer rebuilds | Low | D-C (8.10.3); S-2 | V15.2.1 | Open, Marco decides |
| T325-07 | Approval record | R | An entry dated before Marco's own approval, or approved by an agent's message | Medium (process) | M-5 | V15.1.1 | Open, fix-now |

## Findings

- **F-1 (fix-now, Medium, process; T325-01).** The postgres OpenSSL exceptions planned in D-1 (A) cannot apply on `b6972e11…` (ruling 1). Marco decides D-A. Under A4 no postgres OpenSSL entry is written.
- **F-2 (fix-now, Low, a few lines in changed files; T325-04).** The #265 postgres zlib entry becomes stale with the bump. Remove it, and assert in the cap test that no postgres entry carries #265.

## MUSTs for G4

- **M-1. Postgres pin evidence (T325-02, F-2).**
  - (a) The index digest of `docker.io/library/postgres:18-alpine`, read two ways (Docker Hub tags API `digest`, and the registry `Docker-Content-Digest` with the index `Accept` header). Record the head and the push time of the chosen pin (`b6972e11…` under A4).
    - If the head has changed from `ff64c9e3…`, scan the new head and stop and report: if it carries `3.5.9-r1`, it is the better pin once past its cooldown.
  - (b) Commit the pin no earlier than its cooldown end (`b6972e11…`: 2026-11-22 04:08 UTC). Record the provenance: source `docker-library/postgres`, revision, `18/alpine3.24`, PG_VERSION 18.7, Alpine 3.24.4.
  - (c) A scan of the pin with the pinned Grype. It must show exactly the eight OpenSSL rows (`libssl3` and `libcrypto3` `3.5.9-r0`; four ids each; `fix.state` recorded) and no zlib, krb5-libs, libxslt or PostgreSQL row. Record the zlib, krb5-libs and libxslt versions. If anything else appears, stop.
  - (d) PG 18.6 → 18.7 is a minor change: run the Postgres-touching `Category=Integration` suites (Identity, Api, Bff) and the AppHost tests within bounded run times.
  - (e) Remove the #265 postgres zlib entry on this evidence only.
- **M-2. No void entries; reachability evidence (T325-01, T325-03).**
  - Write the redis OpenSSL entries only if the redis scan at commit time shows these four ids on `libssl3` and `libcrypto3` with `fix.state` `unknown`, or `fixed` with `tag state unchanged`.
  - Write postgres OpenSSL entries only under A2 or A3, and only if `18-alpine` resolves to the pinned digest at commit time and again on the merge day.
  - `package` is the Grype `artifact.name` (`libssl3`, `libcrypto3`), never `openssl`.
  - For each image that gets entries, run with `--network none`:
    - `scanelf -s CMS_encrypt,CMS_encrypt_ex,OSSL_QUIC_client_method,OSSL_QUIC_client_thread_method -R` over `/usr/local` and `/usr/lib`: no importer except `libcrypto.so.3` or `libssl.so.3` themselves. A libcurl that imports the QUIC client is recorded by name, and the postgres justification then names `libpq-oauth` as the only route.
    - No `GCM-SIV` string in `postgres`, `pgcrypto.so`, `redis-server` or `redis-cli`.
    - The `ssl` setting and the Redis `tls-*` arguments of the **dev AppHost** containers (`SHOW ssl`; `docker inspect` args). This is a record only.
  - If any check fails, write no entry for that image, and stop and report.
- **M-3. libc6 evidence (T325-05).**
  - The PR's `Release / build` log shows the api, bff and migrator rows `libc6 2.39-0ubuntu8.9 CVE-2016-20013 Elevated(negligible) fix=wont-fix excepted`, and no `FAIL`. Record the run id.
  - The Ubuntu CVE tracker status for noble, re-checked on the commit date.
  - Any fix state other than `wont-fix`: stop and report; do not edit the entries.
  - `Release / build` passes on the PR (it runs on `.github/image-scan/**`).
- **M-4. File set, mode and caps.**
  - Changed paths are exactly `ContainerImages.cs`, `images.Dockerfile`, `exceptions.json`, `deploy/compose/docker-compose.yaml` (publish), `deploy/tests/test_stackctl.py`, any `.claude/tests` pin found under ruling 5, and docs or the manifest.
  - Never touched: `image_scan.py`, `release_images.py`, `.github/image-scan/Dockerfile`, `ci.yml`, the ruleset.
  - The PR's `image-scan` log shows `mode: delta`, only `postgres` scanned (and `redis` under D-C), and `0 new blocking`.
  - Under A4 the eight postgres OpenSSL rows read "at base". Under A2/A3 they read "excepted (no rebuilt image: …)" with `tag state unchanged`.
  - Any other mode or a new row: stop.
  - Before push, run the full .NET unit suite, `deploy/tests` and `.claude/tests` (Git Bash first in PATH).
- **M-5. Approvals and dates (T325-07).**
  - Every entry text below is approved by Marco in his own words, with the date `D`.
  - `added` = `D`, never before the approval and never after the commit date (the validator rejects a future `added` on the PR run).
  - Exactly one `approved by Marco D` per entry.
  - Banned in the texts: "dev-only", IP literals, "TLS off" stated about the dev host.
  - After the merge, a dispatched `image-scan` on `main`:
    - redis OpenSSL ×8 and redis zlib `excepted` with `tag state unchanged`;
    - no postgres row other than the eight OpenSSL rows (A4, `FAIL`, owned by the follow-up) or none at all (A2/A3);
    - `stale` notes for nothing in scope.

    Anything else: stop and report.

## SHOULDs

- **S-1 (Marco, D-C).** Move redis to `8.10.3-alpine` in this PR (ruling 2).
- **S-2 (backlog #243).** ADR-0015 item 3 rests on "tag unmoved means no fixed rebuild". The premise fails when (i) a tag is rebuilt without the fix (postgres, now) and (ii) a patch tag is frozen (`redis:8.10.2-alpine`). With a 7-day cooldown longer than the rebuild interval, item 3 rarely applies to `postgres:18-alpine`.
  - An amendment candidate (needs an ADR change and a G3): keep a fixed OS-package exception only when the **current tag head**, scanned in the same run, still carries the finding.
  - One line in #243 at G7.
- **S-3 (backlog #243, if A4).** A follow-up issue to pin the first `18-alpine` rebuild that carries `3.5.9-r1`, once past its cooldown, with a date inside the OpenSSL deadline.

## Proposed exception entries (for Marco's approval)

`D` is Marco's approval date; the examples assume 2026-11-22. All entries pass `exception_errors`:
- exactly the seven keys, all strings;
- the ids match `ID_RE`;
- the images are in `EXCEPTION_IMAGES`;
- `libssl3`, `libcrypto3` and `libc6` match `PACKAGE_RE`;
- `#325` matches `ISSUE_RE`;
- each span is at most 30 days, at most 90 days, and within item 3's 30 days;
- there is no duplicate `(image, package, id)`, because the libc6 entries are replaced in place.

Matching (`evaluate`, :680) is `image == alias`, `package == artifact.name`, and `id` among the vulnerability and related ids.

### R-1: redis, 8 entries (`issue` `#325`, `added` `D`, `expires` `2026-12-02`)

Pairs: each of CVE-2026-95691, CVE-2026-95692, CVE-2026-88823 and CVE-2026-107496, with each of `libssl3` and `libcrypto3`. "Tag unmoved" (item 3) does not matter while the fix state is `unknown`. If Alpine reports a fix, item 3 applies, and the entry ends when the tag moves.

Justification template (`<CVE sentence>` from the table below; `<tag>` and `<digest>` are `8.10.2-alpine` and `9f8d2d789b9a...`, or the 8.10.3 values under D-C):

> `<id>`, `<package>` 3.5.9-r0 (Alpine apk, upstream openssl) in the Redis `<tag>` image (sha256:`<digest>`): Grype rates it High; fix state unknown for Alpine 3.23 (matched by NVD CPE, OpenSSL fixed in 3.5.10). `<CVE sentence>` Reachability, class (b), loaded but the vulnerable path is not reached (G4 evidence on this digest, docker run --network none, recorded in docs/ai/pipeline/325.md): redis-server links libssl and libcrypto because the image is built with TLS support, but TLS 1.2 and 1.3 never use AES-GCM-SIV, and Redis performs no CMS encryption and runs no QUIC client; no binary in the image other than libssl and libcrypto imports CMS_encrypt, CMS_encrypt_ex or the OpenSSL QUIC client, and no Redis binary contains the GCM-SIV cipher name. The stack's redis.conf and Compose service set no tls- option (S-120-12 guard) and load no module. Redis sits on the internal cache network only, with the BFF as its only client under a session-only ACL; it publishes no port, and the stack is not deployed today (deploy gate shut by Keycloak's guava, ADR-0022, D-008). Void if a loaded module or any process in the container uses AES-GCM-SIV, CMS encryption or the OpenSSL QUIC client. Ends when a Redis image with OpenSSL 3.5.9-r1 or later is pinned. Issue #325, approved by Marco `D`; added `D`, expires 2026-12-02. Not renewed past this expiry without a new issue and a fresh G3.

### P-1: postgres, 8 entries (only under A2 or A3; `issue` `#325`, `added` `D`, `expires` `2026-12-02`)

The same pairs. Item 3 applies (`apk`, a span of 30 days or less), so each entry holds only while `18-alpine` resolves to the pinned digest.

> `<id>`, `<package>` 3.5.9-r0 (Alpine apk, upstream openssl) in the Postgres 18-alpine image (sha256:`<digest 12 hex>`..., PostgreSQL 18.7, Alpine 3.24.4): Grype rates it High; Alpine fix 3.5.9-r1 exists, but no official 18-alpine rebuild carries it, so ADR-0015 item 3 applies while the tag resolves to this digest and the entry ends at once when the tag moves. `<CVE sentence>` Reachability, class (b), loaded but the vulnerable path is not reached (G4 evidence on this digest, docker run --network none, recorded in docs/ai/pipeline/325.md): the server links libssl and libcrypto and uses libcrypto for SCRAM digests, HMAC and random numbers on every login, and libssl only for TLS, which the stack keeps off (S-120-12 guard); none of these uses AES-GCM-SIV, CMS encryption or QUIC; pgcrypto, which the repository does not create, maps its cipher names through a fixed table (bf, aes, des, 3des, cast5 in cbc or ecb; PGP in CFB) that cannot select GCM-SIV; no binary in the image other than libssl and libcrypto imports CMS_encrypt or CMS_encrypt_ex; `<libcurl QUIC import: none | libcurl, reached only through libpq-oauth's client OAuth flow, which no Decisya component uses>`. Postgres sits on the internal pg-app and pg-kc networks only and publishes no port; the stack is not deployed today (deploy gate shut by Keycloak's guava, ADR-0022, D-008). Void if an extension or library loaded by the server uses AES-GCM-SIV, CMS encryption or the OpenSSL QUIC client, or if a process in the container runs an HTTP/3 or OAuth client flow. Issue #325, approved by Marco `D`; added `D`, expires 2026-12-02. Not renewed past this expiry without a new issue and a fresh G3.

`<CVE sentence>` per id:
- CVE-2026-95692: "Affected code: AES-GCM-SIV IV handling in EVP_CipherInit_ex (heap out-of-bounds read, IV truncation); only callers that fetch AES-GCM-SIV with an IV length other than 12 bytes."
- CVE-2026-88823: "Affected code: AES-GCM-SIV additional authenticated data, where a zero-length update resets earlier AAD; only callers that use AES-GCM-SIV."
- CVE-2026-107496: "Affected code: CMS_encrypt and CMS_encrypt_ex with an attacker-supplied EC recipient certificate of group order 1 (infinite loop); OpenSSL states TLS and QUIC do not use this path."
- CVE-2026-95691: "Affected code: the OpenSSL QUIC client's CRYPTO frame retransmission handling (use after free); only a QUIC client talking to a malicious QUIC server."

### L-1: libc6, edited in place (`issue` `#325`, `added` `D`, `expires` `D + 30`, e.g. 2026-12-22)

api (bff identical, with "The BFF is on the request path of a LAN-reachable service (Caddy proxies the LAN's app traffic to it)"):

> Ubuntu rates it negligible and will not fix it (fix state: wont-fix); a denial of service in glibc crypt() SHA-crypt with very long keys, which the .NET apps never call. The Api is on the request path of a LAN-reachable service (the BFF calls it over the back-channel network through Caddy), so under G3's D-1 rule (S-120-07) it needs a recorded approval and a 30-day expiry. First accepted 2026-10-05 under #120 (S-120-07); renewed under #190 on 2026-10-24. Renewed under #325 as a fresh 30-day acceptance: re-checked `<date>`, Ubuntu fix state still wont-fix (source: the Ubuntu CVE tracker entry for CVE-2016-20013, "`<status>`" on noble, and the Release / build row of run `<run id>`), libc6 2.39-0ubuntu8.9 in the .NET 10.0.15 aspnet base sha256:cae5b962bfb7... (.github/release/base-images.Dockerfile, #320); approved by Marco `D`, added `D`, expires `D + 30`, 30 days. A further renewal needs a new issue and a fresh G3.

migrator:

> Ubuntu rates it negligible and will not fix it (fix state: wont-fix); a denial of service in glibc crypt() SHA-crypt with very long keys, which the .NET apps never call. The migrator is a one-shot job, not on a request path. First accepted 2026-10-04 under #119; under #325 its expiry is aligned with the api and bff entries: re-checked `<date>`, libc6 2.39-0ubuntu8.9 in the .NET 10.0.15 runtime base sha256:b64d614c3c1c... (#320), fix state still wont-fix (Release / build run `<run id>`); approved by Marco `D`, added `D`, expires `D + 30`.

## Decisions for Marco

- **D-A. Postgres route** (ruling 1).
  - **Recommended: A4.** Pin `b6972e11…` on 2026-11-22/23 with no postgres OpenSSL entry. Amend the Done-when bullet "no blocking postgres row" to exclude the eight OpenSSL rows, and open the follow-up bump (S-3).
    - Risk: `main` is red on eight High rows (class (b), not deployed) until the follow-up.
  - **A2:** pin `ff64c9e3…` and merge 2026-11-26 with the P-1 entries.
    - Risks: Critical zlib and the 38 PG Highs stay about 3 days longer; the libc6 lapse window; the tag may move again first.
  - **A3:** as A2 but on 2026-11-22/23 under a cooldown waiver.
    - Risk: a waiver that buys no fix (not recommended).
- **D-B. Approve R-1, L-1** (and P-1 under A2/A3) in your own words with a date. Your 2026-11-20 "(A)" approved the plan, not these texts.
- **D-C. Redis 8.10.2 or 8.10.3** (ruling 2). Recommended: 8.10.3-alpine in this PR.
  - Risk: one more image to evidence, and a re-approved zlib justification.
- **D-D. Migrator alignment** to `D + 30` (D-1 already says "aligned"; confirm the date).
- **D-E. Only if both item 4 checks are positive:** stop the dev Postgres container until the bump.

## Could not check

- The 38 PostgreSQL 18.6 advisories (pre-authentication or not), and the contents of `grype-pg-ff64c9e3.json` (taken from the orchestrator's summary).
- Whether Aspire 13.5's typed Redis and Postgres resources enable TLS in dev, and the dev containers' port bindings.
- Whether `8.10.2-alpine` is still listed in the official-images `library/redis` file (G4 records it under D-C).
- `.claude/tests` pins of entry issues or dates (outside my search scope).

## Addendum 2026-11-20: Redis bundled modules (D-F (a))

- **Scope.** Only the G4 modules discovery (manifest, "G4 evidence" § 2026-11-20, M-2) and Marco's D-F (a): correct the R-1 and redis zlib texts, keep the PR's scope, and send the module stop to the backlog. Nothing else in this file is re-audited.
- **Sources read (Read and Grep only; I ran nothing):** the manifest; `deploy/redis/redis.conf` :6-23; `deploy/compose/stackctl.py` :111-120 (`REDIS_ACL_POLICY`); `deploy/compose/docker-compose.yaml` :19-23; `deploy/compose/docker-compose.stack.yaml` :70-89; `deploy/compose/stackguards.py` (no module or `loadmodule` rule); `deploy/tests` (no module or `loadmodule` test; `test_stackctl.py` :696-722); `.github/image-scan/exceptions.json` :85-93 (the redis zlib entry); `.github/scripts/image_scan.py` :208-295 (`approval_date`, `exception_errors`; `OWN_BUILD_ALIASES` is `caddy` only); `docs/ai/pipeline/265.md` :72-77; `image-scan-drift-265.md` (M-2, T265-03); `deployable-stack.md` :266-273 (the three classes).
- **Container facts** (module list, entrypoint behaviour, linkage, `scanelf`, `GCM-SIV` counts, libz size and symbols, Grype rows, digest reads) are the orchestrator's G4 record, taken as data.

### Ruling

**The merge decision and the verdict are unchanged.** With the four modules loaded, the entries are still acceptable:
- **The four OpenSSL CVEs: class (b), loaded but the vulnerable path is not reached.**
  - The modules add two more libssl/libcrypto users: `redisearch.so` and `redistimeseries.so`.
  - No module, and no other file except `libcrypto.so.3` and `libssl.so.3` themselves, imports `CMS_encrypt`, `CMS_encrypt_ex`, `OSSL_QUIC_client_method` or `OSSL_QUIC_client_thread_method`. `GCM-SIV` occurs 0 times in `redis-server`, `redis-cli` and each module. So CVE-2026-107496 (CMS), CVE-2026-95691 (QUIC client), CVE-2026-95692 and CVE-2026-88823 (AES-GCM-SIV) have no caller.
  - No module command is reachable from outside. The ACL (`stackctl.py:115-120`, documented in `redis.conf:11-19` and asserted by `deploy/tests`) has the `default` user `off`, and `health` limited to `-@all +ping`. `decisya-bff` has `-@all` plus a fixed list of core session commands. A command that a module registers belongs to no `+` entry, so no enabled user can call `FT.*`, `JSON.*`, `TS.*`, `BF.*`/`CF.*`, or `MODULE`.
  - TLS still plays no part: the stack sets no `tls-` option (S-120-12), and TLS 1.2 and 1.3 do not reach the four paths anyway.
- **zlib CVE-2026-4738: class (a), not loaded.** This is stronger than (b), so it meets the class (b) bar in the D-F wording.
  - No module links libz.
  - `redis-server` needs `libzstd`, `libssl`, `libcrypto` and musl, and `redis-cli` needs `libssl`, `libcrypto` and musl.
  - Only `/sbin/apk` and `libapk.so.3.0.0` link libz, and the stack never runs them.
  - In any case, the shipped `libz.so.1.3.2` (108376 bytes) contains no `inflateBack9` or `inflate_table9`, so the affected contrib code is not in the image.
  - This matches #265's T265-03 ("Low (not loaded)").
- **What was wrong, and what to fix now (inside D-F (a); Low; a few lines in `exceptions.json`, which this PR changes):**
  - R-1 as approved says the stack "load[s] no module".
  - The #265 redis zlib entry says "the stack's redis.conf has no loadmodule" (true, but it implies that no module loads), and it is "Void if … the configuration loads a module". Read literally, that entry is already void.
  - Both texts are replaced by the final texts below.
  - Root cause: #265 G3 M-2 asked for `MODULE LIST` on a running server, or proof that the image ships no module directory. #265 G4 replaced that with `ldd` evidence (`265.md:75`). The `ldd` evidence was enough for zlib, but it never established which modules load.
- **Dev AppHost (record only).** `decisya-redis` runs through `/bin/sh -c` with `--tls-port`. The image entrypoint is bypassed, so no module loads in dev. The texts below make no claim about the dev host.
- **Approval date rule (M-5).**
  - The texts below carry `approved by Marco 2026-11-20` (R-1), `added` 2026-11-20, and "Marco approved the rewrite on 2026-11-20" (zlib). This assumes Marco approves these corrected texts, in his own words, on 2026-11-20.
  - If he approves on a later day `D'`, G4 writes `D'` in each of those places, and in R-1's `added`. `added` is never earlier than the approval. R-1's `expires` stays 2026-12-02, and the zlib entry's `added`/`expires` stay 2026-11-09/2026-12-09.
  - The zlib text keeps **one** `approved by Marco` phrase (the 2026-11-09 one, which matches its `added`). The 2026-11-20 rewrite approval is worded differently on purpose, so that `approval_date()` (`image_scan.py:208-219`) would still find exactly one approval no later than `added` if redis ever came under that rule.

### Final text: R-1, redis × 8 (`issue` `#325`, `added` `2026-11-20`, `expires` `2026-12-02`)

**Pairs.** Each of CVE-2026-95691, CVE-2026-95692, CVE-2026-88823 and CVE-2026-107496, with each of `libssl3` and `libcrypto3`.

**How to fill each entry:**
- `<id>` is the entry's `id`, and `<package>` is its `package`.
- `<CVE sentence>` is the per-id sentence from "`<CVE sentence>` per id" above, written without its quotation marks.
- Every other word is fixed.

> `<id>`, `<package>` 3.5.9-r0 (Alpine apk, upstream openssl) in the Redis 8.10.3-alpine image (sha256:7958cfbb9d15..., Redis 8.10.3, Alpine 3.23.8): Grype rates it High; fix state unknown for Alpine 3.23 (matched by NVD CPE, OpenSSL fixed in 3.5.10). `<CVE sentence>` Reachability, class (b), loaded but the vulnerable path is not reached (G4 evidence on this digest, docker run --network none, recorded in docs/ai/pipeline/325.md): redis-server links libssl and libcrypto because the image is built with TLS support, but TLS 1.2 and 1.3 never use AES-GCM-SIV, and Redis performs no CMS encryption and runs no QUIC client. The stack keeps the image entrypoint, which loads the image's four bundled modules (redisbloom.so, redisearch.so, redistimeseries.so and rejson.so) whenever it starts redis-server; redisearch.so and redistimeseries.so also link libssl and libcrypto, and redisbloom.so and rejson.so do not. No binary or module in the image other than libssl and libcrypto imports CMS_encrypt, CMS_encrypt_ex or the OpenSSL QUIC client, and neither redis-server, redis-cli nor any of the four modules contains the GCM-SIV cipher name. The stack's redis.conf and Compose service set no tls- option (S-120-12 guard). Redis sits on the internal cache network only and publishes no port, with the BFF as its only client; its ACL grants no module command to any enabled user (the default user is off, the health user may only PING, and the BFF has a fixed list of session commands). The stack is not deployed today (deploy gate shut by Keycloak's guava, ADR-0022, D-008). Void if redis-server, redis-cli, a loaded module or any other process in the container imports CMS_encrypt, CMS_encrypt_ex or the OpenSSL QUIC client or uses AES-GCM-SIV, if the stack loads a module other than these four, or if a module command is granted to any enabled ACL user. Ends when a Redis image with OpenSSL 3.5.9-r1 or later is pinned. Issue #325, approved by Marco 2026-11-20; added 2026-11-20, expires 2026-12-02. Not renewed past this expiry without a new issue and a fresh G3.

### Final text: redis zlib CVE-2026-4738, rewritten in place (`issue` `#265`, `added` `2026-11-09`, `expires` `2026-12-09`, all unchanged)

> CVE-2026-4738, zlib 1.3.2-r1 (Alpine apk) in the Redis 8.10.3-alpine image (sha256:7958cfbb9d15..., index pushed 2026-11-11 21:05 UTC, Redis 8.10.3, Alpine 3.23.8): Grype rates it Critical; Alpine fix 1.4-r0; affected code: the advisory (Grype's record) names the frmts/zlib/contrib/infback9 modules, program file inftree9.c, that is zlib's optional contrib inflateBack9 and inflate_table9 code. The tag 8.10.3-alpine resolves to this index digest (read two ways on 2026-11-20: the Docker Hub tags API and the registry Docker-Content-Digest; 8.10-alpine is the same digest), so no fixed official rebuild exists and ADR-0015 item 3 applies while that holds. History: under #265 the pin moved from the index 615b4ced... to 8.10.2-alpine (sha256:9f8d2d789b9a...); under #325 it moved to this digest, the patch tag upstream still rebuilds (8.10.2-alpine was last pushed 2026-10-26). Reachability, class (a), not loaded (G4 evidence on this digest, docker run --network none, recorded in docs/ai/pipeline/325.md; the earlier digest's evidence is in docs/ai/pipeline/265.md): the shipped /usr/lib/libz.so.1.3.2 (108376 bytes) contains no inflateBack9 and no inflate_table9, only zlib's standard inflateBack, so the affected contrib code is not compiled into the image's libz and no caller can reach it. Neither redis-server nor redis-cli links libz.so (redis-server links libzstd, libssl, libcrypto and musl; redis-cli links libssl, libcrypto and musl). The stack keeps the image entrypoint, which loads the image's four bundled modules (redisbloom.so, redisearch.so, redistimeseries.so and rejson.so) whenever it starts redis-server; none of the four links libz. The only files in the image that link libz are /sbin/apk and libapk.so, which the stack never runs (read-only root filesystem, every capability dropped). Redis compresses with its own LZF code, and the stack disables persistence (no RDB, no AOF). Redis sits on the internal cache network only and publishes no port, with the BFF as its only client; its ACL grants the BFF session commands only (no DUMP, RESTORE, MODULE, CONFIG or module command), the default user is off and the health user may only PING. The stack is not deployed today (deploy gate shut by Keycloak's guava, ADR-0022, D-008). Void if redis-server, redis-cli or any loaded module links or maps libz, if the stack loads a module other than these four, if a module command is granted to any enabled ACL user, or if any service runs a program inside the Redis container that uses zlib. Ends when the tag is rebuilt with the fix (Dependabot proposes the digest bump). Issue #265, approved by Marco 2026-11-09; added 2026-11-09, expires 2026-12-09, 30 days. Rewritten in place for this digest under #325; Marco approved the rewrite on 2026-11-20. Not renewed past this expiry without a new issue and a fresh G3.

**Checks the G4 writer and G6 run on these texts:**
- No "dev-only" (`test_stackctl.py:710`).
- No IP literal.
- No "TLS off" claim about the dev host.
- Exactly one `approved by Marco` per entry.
- `#265` and `#325` match `ISSUE_RE`.
- R-1 spans 12 days, and the zlib entry spans 30 days, within item 3's 30.
- The caps follow ruling 5: `#325` ≤ 2026-12-02, and `#265` ≤ 2026-12-09.

### SHOULD (addendum)

- **S-4 (backlog #243, its own issue and its own G3). Stop loading the unused bundled modules in the stack.** Decisya uses Redis only as the BFF ticket store, and no component calls a module command.
  - **Why.** Four modules (two of them more OpenSSL users) are attack surface the stack does not need. Every image exception for redis must then argue about them, and a future module or ACL change would silently widen what a peer can reach.
  - **Resource limits.** Their memory and thread cost under `mem_limit: 128m` and `pids_limit: 64` has not been measured. That is a reason to check it, not a finding.
  - **The change, for its own G3 to choose:** run `redis-server /usr/local/etc/redis/redis.conf` in the stack overlay with an explicit `entrypoint`, which bypasses `docker-entrypoint.sh`. The stack already sets `user: "999:1000"`, so the entrypoint's privilege step is not needed. Or use a module switch the image supports, if its G3 shows one exists.
  - **A stack guard** in `stackguards.py`: the `redis` service has that exact entrypoint and command, and no `--loadmodule`, `loadmodule` or `module load` appears in its command, entrypoint, environment or `redis.conf`.
  - **Red tests** in `deploy/tests`: a Compose file with no `entrypoint` on `redis` (the image's own, which loads modules) is refused, and so are a `--loadmodule` argument and a `loadmodule` line in `redis.conf`.
  - **Ideally**, a stack smoke check that the running server reports no module. Do it without giving any stack ACL user `MODULE`, for example `INFO modules` through the BFF's existing `+info`.
  - **After S-4:** the next redis exception texts drop the module sentences, and their void clauses say "if the stack loads any module".
  - One line in #243 at G7.

### Could not check (addendum)

- Whether the orchestrator's `GCM-SIV` count was case-sensitive. A lower-case `aes-128-gcm-siv` fetch name would not match. This does not change the ruling: no Redis or module code path selects a cipher from client input, and no module command is reachable. G6 may ask for a case-insensitive count if it is cheap.
