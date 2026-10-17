<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #143 -->
# Threat delta: renew the #120 OpenSSL/zlib image-scan exceptions to 2026-11-03 (#143)

- Date: 2026-10-10. Reviewer: security-reviewer (G3).
- Scope: the planned change only. In `.github/image-scan/exceptions.json`, the 25 entries with `issue` `#120` and `expires` 2026-10-20 get `expires` 2026-11-03. `added` stays 2026-10-06, and each justification gains the 2026-10-10 re-check and Marco's approval. There is no code, policy, test or workflow change. Unchanged areas are not re-audited.
- Facts come from `docs/ai/pipeline/143.md`. The rules come from ADR-0015 with its 2026-10-06 amendment, `.github/scripts/image_scan.py`, `.claude/tests/test_image_scan.py`, the `$comment` of `exceptions.json` and `docs/runbooks/ci-security-gates.md`. The plan is checked against the #142 note, `image-scan-go-http2-exceptions.md` (on #142's branch), in particular F-1 and M-5.

## Verdict

**PASS-WITH-NOTES.** The renewal is data-only, and the validator and ADR-0015 accept it as planned, including the Caddy zlib entry under the narrow D-2 rule. Merge depends on M-1 to M-5. F-1 is a merge-path condition that Marco decides. It is not a flaw in the change.

## Does the renewal satisfy ADR-0015 and the validator?

| Rule | Where | All 25 entries after the change | Holds |
| --- | --- | --- | --- |
| `expires` at most 90 days after `added`; `added` not in the future | `validate_exceptions`, `MAX_EXCEPTION_DAYS`; ADR item 4 | 2026-10-06 to 2026-11-03 = 28 days | yes |
| A fixed finding is kept only if it is an OS package, lasts at most 30 days and its tag is still at the pinned digest | `_keeps_fixed_exception`, `MAX_FIXED_EXCEPTION_DAYS`; amendment item 3 | Caddy `zlib` (CVE-2026-85091, fix 1.3.2-r1): `apk`, 28 days, and on 2026-10-10 `caddy:2.11.7-alpine` was still at `sha256:d8542f48d34a…`. The scan resolves the tag again on every run and fails closed. The OpenSSL entries get the same 28-day margin in case Alpine ships a fix before 2026-11-03. | yes |
| Request-path component (Caddy): Marco's recorded approval, a reachability argument, at most 30 days | `$comment`, #120 S-120-07 | 28 days from `added`; reachability class (a) "not loaded" unchanged; approval needed (M-3) | yes, if M-3 holds |
| Exact match, no duplicate (image, package, id) | `PACKAGE_RE`, `seen` | The entries are edited in place, so there is no new key | yes |
| Active through the expiry day | `evaluate`: `today <= expires_date` | Applied through 2026-11-03 (UTC). Reported as `expired (not applied)` from 2026-11-04. | yes |

`test_committed_file_is_valid` runs the validator on the committed file, so G4 and G5 see a schema break at once.

## Renewal or a new entry?

Editing the entry in place is the right form. A second entry with the same (image, package, id) is rejected as a duplicate, so the only alternative is to replace the entry. Keeping `added` at 2026-10-06 is the safer of the two ways to replace it:

- **Keeping `added` (the plan).** The 30-day bounds (D-2 item 3 and the Caddy request-path rule) count from the first acceptance. These entries can therefore not be stretched past 2026-11-05 without a fresh, visible decision.
- **The runbook's form.** `added` = today and `issue` = the renewing issue would restart that clock with every renewal.

The plan departs from the runbook's renewal paragraph (`ci-security-gates.md`, "Add or renew an exception"). The PR body must say so (M-2). Bringing the runbook into line is backlog (S-1).

Keeping `issue` `#120` is right. The issue names the accepted risk, and #143 only extends its life. The Done-when also counts the entries as "#120 entries". The justification names #143 (M-3).

## Trust boundaries

There is no new boundary. The change shifts what the CI-policy boundary (`exceptions.json` to the required `image-scan` check to merge) lets through by 14 days for findings that were already accepted.

## Threats

| Id | Element | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T-01 | `exceptions.json` | T | The edit reaches further than the 25 entries: another entry is extended (the #83 entries ending 2026-11-02, the libc6 entries) or #142's Go entries ride along | High if it happens | M-1, M-5; the validator's exact matching | V15.1.1, V15.2.1 | Mitigated if the MUSTs hold |
| T-02 | `added` field | T | `added` is reset, and chained 30-day renewals keep a request-path or fixed-OS exception alive with no limit | Medium | M-2 | V15.2.1 | Mitigated if M-2 holds |
| T-03 | Justification text | R | Stale or false wording: "14 days"; "when Alpine ships a fix" on the zlib entry that is now fixed; an approval that Marco did not give | Medium | M-3 | V15.1.1 | Mitigated if M-3 holds |
| T-04 | Premise "no rebuilt image" | T | A tag moves between 2026-10-10 and the merge. The code catches this only for fixed findings (zlib). A not-fixed OpenSSL entry would be renewed even though a rebuilt image could be bumped instead. | Low | M-4 | V15.2.1 | Mitigated if M-4 holds |
| T-05 | Required `image-scan` check | T | The PR is red for rows outside its scope (F-1), and the red is cleared by bypassing or weakening the gate | High if done | M-5; F-1 | V15.1.1 | Open: Marco decides (F-1) |
| T-06 | OpenSSL 3.5.8 / zlib in Postgres, Redis and Caddy | I, D | Exploitation while the stack is not deployed | Low | See "Risk while undeployed" | V15.2.1 | Accepted |

## Findings

### F-1 (fix-now, Medium, merge path; no code change)

`exceptions.json` is in the `images` lane, so this PR runs the full five-image scan. The #142 note (F-1) records that the 2026-10-08 Go HTTP/2 advisories in `caddy` and `otelcollector` are fixed upstream. D-2 therefore voids any exception for them, and a renewal cannot cover them (manifest, out of scope). If those rows still FAIL, this PR's required check is red for reasons outside #143. "No expired #120 entry" then holds, but the check is not green.

- It must not be made green under #143. That rules out skipping the step, removing `image-scan` from the ruleset, or touching `image_scan.py`, its tests or `ci.yml` (#142 M-5).
- Marco decides the path; none is chosen here:
  - **(a) Land the Caddy and collector bumps first** (#142, way forward 2), then re-scan. The new Caddy digest may already carry fixed OpenSSL and zlib. Any Caddy entry the scan then reports as stale is dropped, not renewed (M-4).
  - **(b) Merge #143 red** with a recorded decision that every FAIL row belongs to #142. That is Marco's own ruleset action, never an agent's.
- Either way, the merge must happen by 2026-10-20.

## MUSTs

- **M-1. Exact entries only.** The change touches exactly the 25 entries with `issue` `#120` and `expires` `2026-10-20`:
  - `caddy`: libssl3 x4, libcrypto3 x4, zlib x1;
  - `postgres`: libssl3 x4, libcrypto3 x4;
  - `redis`: libssl3 x4, libcrypto3 x4.

  In each of them, only `expires` and `justification` change. `id`, `image`, `package`, `issue` and `added` stay byte-identical. Every other entry and the `$comment` stay byte-identical, and no entry is added.
- **M-2. Dates.** `added` stays `2026-10-06` and `expires` is `2026-11-03` in all 25 entries (28 days, under both 30-day bounds). The PR body states that `added` is kept on purpose, so the 30-day bounds count from first acceptance (unlike the runbook's renewal paragraph). A further renewal of these entries needs a new issue and a fresh G3.
- **M-3. Honest justification.** Each justification keeps its reachability class and argument. Then:
  - Replace "14 days" with the real span, for example "expires 2026-11-03, 28 days after added".
  - Append: "Renewed under #143: re-checked 2026-10-10, `<image>:<tag>` still at the pinned digest `sha256:<12 hex>…`, no rebuilt image; Marco approved the renewal on `<date>`."
  - Use Marco's approval only if Marco himself gave it, and only with the date he gave it. G6 checks that it is recorded in the manifest or on the issue. An agent's or orchestrator's message is not that approval.
  - In the Caddy `zlib` entry, replace "bump the digest when Alpine ships a fix" with: "Alpine fix 1.3.2-r1 exists; kept only under ADR-0015 amendment item 3 (OS package, at most 30 days, tag unchanged); ends at once when the tag moves."
  - Keep every justification at least 20 characters. Write the escape-sensitive text as plain ASCII.
- **M-4. Re-check before merge.** The PR's final `image-scan` run is the re-check:
  - Its log shows `tag state unchanged` for `postgres`, `redis` and `caddy`.
  - The Caddy `zlib` row reads "excepted (no rebuilt image: …)".
  - None of the 25 entries is FAIL or `expired (not applied)`.
  - If a tag has moved, that image's entries are not renewed: bump the image instead, which needs Marco's scope decision.
  - Any of the 25 that the run reports as `stale` is removed, not renewed.
  - `test_committed_file_is_valid` passes.
- **M-5. No other change.** `git diff origin/main...HEAD` touches only `.github/image-scan/exceptions.json` and the gate documents. There is no change to `image_scan.py`, the tests, `ci.yml`, the ruleset or `ContainerImages.cs`, and no #142 entry. This keeps #143 consistent with #142 M-2: no renewal under #142; renewals go through their own issue, as here.

## Risk while undeployed

The stack is not deployed (#132 and #29 are not live), so the deployed-context reachability arguments are not yet exercised. They are unchanged:

- Caddy, class (a): the static Go binary never loads Alpine libssl, libcrypto or libz.
- Postgres libssl, class (b): `ssl` is off (S-120-12).
- Redis libssl, class (b): no `tls-*` option (S-120-12).
- Postgres and Redis libcrypto, class (c): reached only by internal peers that already hold the credential.

Today the images run only on Marco's host, through the AppHost and Testcontainers with synthetic data, and in CI. The residual risk for 14 more days is **Low**. If go-live happens before 2026-11-03, the arguments are the deployed-context ones the entries already state, and #142 M-4 independently keeps Caddy from being started while the Go findings stand.

## SHOULDs

- **S-1 (backlog #83).** Amend `docs/runbooks/ci-security-gates.md`, "Add or renew an exception":
  - When a 30-day bound applies (request-path component or fixed OS package), keep the original `added` and `issue` and record the renewal in the justification.
  - Bring the image list in the rules paragraph up to date: it still names only `postgres`, `keycloak` and `redis`.
- **S-2.** Watch the Alpine OpenSSL release and the official Postgres, Redis and Caddy rebuilds. Bump each image once its rebuild has cleared the 7-day cooldown, before 2026-11-03, and remove the entries the scan then reports as stale.
