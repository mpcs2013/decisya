<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #187 -->
# Threat delta: renew the caddy go-module item-4 exceptions until the self-built Caddy lands (#187)

- Date: 2026-10-22. Reviewer: security-reviewer (G3). Tier: Full (manifest `docs/ai/pipeline/187.md`; G1 and G2 skipped by Marco).
- Scope: the change only. It replaces the 16 `#142` caddy `go-module` entries in `.github/image-scan/exceptions.json` (they expire 2026-10-31) with renewed entries under `#187`, and changes the test that pins the `#142` set. The item-4 code, the deploy guard and the ADR are out of scope because the change must not touch them (M-4).
- Sources: ADR-0015 items 3 and 4 (amendment 2026-10-17); `.github/scripts/image_scan.py` (`is_vendor_binary_entry`, `vendor_binary_approval`, `_vendor_binary_errors`, `exception_errors`, `vendor_binary_entries`, `_keeps_vendor_binary_exception`); `deploy/compose/stackctl.py` (`vendor_binary_problems`); `.claude/tests/test_image_scan.py`; `.claude/tests/test_ci_gates_fail_closed.py` (`test_committed_file_is_valid`); the #142 G3 delta `image-scan-go-http2-exceptions.md` (M-6 to M-9, S-5); `docs/ai/pipeline/183.md` (G4 evidence).

## Verdict

**PASS-WITH-NOTES.** ADR-0015 item 4 already allows the renewal as a new decision: "A later entry for the same finding is a new decision: a new issue and a fresh approval, under the same rules, until the sunset." No policy, code or guard change is needed or allowed. G4 may start, subject to M-1 to M-5. The High exposure (T-01) is mitigated by the deploy guard, which keys on the entries being present and reads no clock, for as long as the stack stays undeployed.

**Delta 2026-10-29 (the upstream-Caddy bridge): verdict unchanged, PASS-WITH-NOTES.** Marco widened the scope to a bridge on 2026-10-29. The bridge has three parts: the pin bump to the tag head `eb4d35b7452d…`, item-3 entries for `brotli-libs`, `libcurl` and `curl`, and an item-4 entry for go-jose v4. It needs no ADR, code, guard or ruleset change. Nothing in it is unacceptable on the LAN edge:
- **brotli/libcurl/curl** are class (a) on the head. The static Caddy binary loads none of them, and no process in the stack runs curl.
- **The Go set and go-jose v4** stay behind the deploy guard, which keys on presence.

G4 is held to M-1 to M-5 as adjusted below, plus M-6 to M-10. Every bridge entry ends by 2026-11-12. There is no BLOCK and no High or Medium flaw in unchanged code to escalate.

## What the code already enforces (no change)

- **Item-4 entries.** Every entry with `image` `caddy` and `package` `stdlib` or a path containing `/` is an item-4 entry. Validation rejects such an entry, and with it the whole file, in any of these cases:
  - the span is more than 14 days;
  - the justification does not contain the `issue` value;
  - it does not contain exactly one `approved by Marco <real date>`;
  - the approval date is after `added`;
  - `expires` is more than 14 days after the approval date;
  - `expires` is after 2026-11-30.
- **Every entry.** Validation also rejects:
  - an `added` in the future (UTC);
  - a second entry with the same `(image, package, id)`. So the `#142` and `#187` entries for the same finding cannot both be in the file.
- **`evaluate()`** checks the bounds again and stops applying item 4 from 2026-12-01.
- **The deploy guard.** `stackctl up` refuses while any item-4 entry is present, expired or not. It makes zero `Runner` calls, has no flag and reads no environment variable.

## Threats (delta)

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 id | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T-01 | LAN to caddy 2.11.7 (Go 1.26.8, x/net 0.59.0) | D, T | The renewed advisories stay on the edge's request path for one more window. Most are HTTP/2 server denial of service. 6605 and 6613 are HTTP/1 connection desynchronisation (request-smuggling class), 6607 is crypto/tls ECH, 6608 and 6609 are parsing limits, and 6604 is Windows-only | High if deployed | Stack not deployed (#132, #29). Deploy guard keyed on presence (M-4). 14-day bound and 2026-11-30 sunset in code. #188 is the structural fix | V15.1.1, V15.2.1 | Mitigated while the guard refuses; accepted for the bridge window (see "Residual risk") |
| T-02 | `exceptions.json` approval | R, T | An approval that Marco did not write, or a pre-dated approval, stretches the window. Reusing the 2026-10-17 phrase cannot extend past 2026-10-31 (`expires <= approval + 14`) | Medium | M-2. The validator's approval-date bounds | V15.1.1 | Open, fix-now (G4, Marco) |
| T-03 | `.claude/tests/test_image_scan.py` | T | The pinning test is deleted rather than replaced, or it validates at a frozen date and breaks on any later entry (the #183 lesson), or it weakens an exact-value test | Medium | M-3 | V15.2.1 | Open, fix-now (G4/G5) |
| T-04 | `stackctl up` guard and item-4 code | E | The renewal PR also loosens the guard, the predicate, the bound or the sunset | Medium | M-4 | V15.2.1 | Open, fix-now (G4); G6 checks |
| T-05 | Coverage set | T | A Go finding that no longer matches is renewed (a dead exception), or a new Go advisory is covered without its own approval | Medium | M-1, M-5. Matching is on exact image, package and id | V15.2.1 | Open, fix-now (G4) |
| T-06 | Renewal chain | E | Renewal after renewal makes item 4 permanent | Low | Sunset constant and tripwire in code. Each renewal needs a new issue and a fresh approval. Moving the sunset needs an ADR amendment and G3 | V15.1.1 | Mitigated |
| T-07 | Fixed Caddy release published during the window | D | Same as #142 T-09 | Low | Dependabot, the 14-day bound, the guard, and the premise check in M-5 | V15.2.1 | Accepted (ADR, Marco 2026-10-17) |

## MUSTs (fix-now; G4 implements them, G6 checks them)

- **M-1 (T-05). The renewal rule (data).**
  - **One new entry per still-matching finding.**
    - Write one entry per `(id, "caddy", package)` that the pinned caddy digest's scan reports on the day the entries are written.
    - `package` is exactly `stdlib` or `golang.org/x/net`. `id` is the CVE or GHSA id the scan reports, and the GO id goes in the justification.
    - Today that is the 16 (11 `stdlib`, 5 `golang.org/x/net`; GO-2026-6603 to 6613 except 6606, plus 6617).
    - *(Delta 2026-10-29.)* The scan is now the scan of the new pin `eb4d35b7452d…` (M-6), not of `24ec7b05…`. The item-4 set grows by one entry: `github.com/go-jose/go-jose/v4` GHSA-mgcc-cww5-fp2q (M-8). That makes 17 `#187` item-4 entries: 11 `stdlib`, 5 `golang.org/x/net` and 1 go-jose v4. The `package` rule above is widened for that one entry only.
  - **No entry for a finding that no longer matches.**
  - **Remove the `#142` entries; do not keep both.** The duplicate check enforces this. Remove the 16 `#142` entries and add the `#187` entries in the same commit, so that no commit leaves Caddy 2.11.7 pinned with no item-4 entry, which would let the guard pass.
  - **Fields.**
    - `issue` `#187`.
    - `added` is the UTC date of the commit that adds the entry. The validator rejects a future `added` in CI, which runs in UTC.
    - `expires` is no more than 14 days after `added`, no more than 14 days after the approval date, and no later than 2026-11-30.
  - **No other entry changes** (#83, #119, #120, #183). No `otelcollector` Go entry is added. *(Delta 2026-10-29: two exceptions. The three item-3 entries of M-7 are added, and the `#183` `curl` entry is replaced by a `#187` entry under M-7. The `#190` and `#195` entries and #212's go-jose v3 entry are not touched.)*
- **M-2 (T-02). Marco's approval anchors the window.**
  - Each justification carries exactly one `approved by Marco <YYYY-MM-DD>` and the text `#187`.
  - **Written by Marco.** Marco writes the phrase himself, as in #183 M-2. An agent writes the entries without it, and the file stays invalid (fail-closed) until he adds it.
  - **The approval date sets the end.** It is on or before `added`, and the 14 days run from it. An approval given on 2026-10-22 caps `expires` at 2026-11-05, whatever `added` says. To cover until about 2026-11-13 (manifest timing), Marco approves and the entries are committed on the merge day, about 2026-10-30. *(Delta 2026-10-29: the bridge ends by 2026-11-12 at the latest, whatever the approval date allows. Marco approves after reading this delta, in his own words, per entry group (M-8).)*
  - **No pre-dating.** No approval or `added` date may be later than the day it is written.
  - **Justification content.** Each one keeps the #142 M-9 statements:
    - reachable once deployed;
    - the risk rests on time and on the deploy guard;
    - the entry is void once Caddy runs anywhere reachable;
    - "Windows-only" for 6604;
    - 6605 and 6613 are not called denial of service.
- **M-3 (T-03). Tests keep enforcing item 4 for the new set, without a frozen date.**
  - **Replace the old test.** `test_the_committed_142_entries_are_caddy_item_4_entries_only` is replaced, not deleted, by a test over the live file. It asserts:
    - no entry with `issue` `#142` remains;
    - the item-4 entries (`is_vendor_binary_entry`) are exactly the `(id, package)` set that G4 recorded. Each has image `caddy`, a CVE or GHSA id, `issue` `#187`, and a package of `stdlib` or `golang.org/x/net`, with the stdlib and x/net counts asserted;
    - *(Delta 2026-10-29.)* The test selects the `#187` entries, not every item-4 entry in the file. Once #212 merges into this branch, its go-jose v3 entry is also an item-4 entry, and this test must neither assert it nor break on it. It asserts the following:
      - the `#187` item-4 entries are exactly 17: 11 `stdlib`, 5 `golang.org/x/net` and 1 `github.com/go-jose/go-jose/v4`;
      - the `#187` non-item-4 entries are exactly `brotli-libs`/CVE-2026-111497, `libcurl`/CVE-2026-92392 and `curl`/CVE-2026-92392;
      - no `#183` entry remains;
      - every `#187` entry has `expires <= 2026-11-12`.
    - `vendor_binary_approval(...)` is not `None` and is on or before `added`;
    - `expires <= min(added, approval) + 14 days` and `expires <= VENDOR_BINARY_SUNSET`;
    - no `PENDING` text;
    - no `otelcollector` Go entry (kept from the old test).
  - **Validate only these entries, at a date taken from the data.** Call `exception_errors({"exceptions": item4_entries}, max(added of those entries))` and expect `[]`. The test does not use a fixed calendar date, so it stays green after expiry and when later entries are added. Whole-file validation at the real date stays in `test_committed_file_is_valid`.
  - **Unchanged.** The exact-value test (alias map, 14, 2026-11-30, `ENTRY_KEYS`, the banned-source list), the sunset tripwire, the validator and `evaluate()` red tests, and the fixture-based `deploy/tests` (`vendor_entry` defaults to `#142` fixture data, which is correct). Changing any of them is a BLOCK at G6.
  - **Routing.** `.claude/**` has no agent lane, so Marco edits this test, as in #183. The issue runs on the host (CLAUDE.md).
- **M-4 (T-01, T-04). The deploy guard still refuses, and nothing loosens it.**
  - The diff does not touch any of these:
    - `.github/scripts/image_scan.py`;
    - `.github/scripts/release_images.py`;
    - `deploy/compose/stackctl.py` and `stackguards.py`;
    - `deploy/tests/**`;
    - ADR-0015 item 4.
  - No override flag, environment variable or workflow input is added.
  - **G4 evidence.** Record the guard's refusal against the branch's own clone. The message names `caddy`, the `#187` ids and the new expiry, and the run makes zero `Runner` calls.
- **M-5 (T-05, T-07). The set, the premise and the runs, recorded.**
  - **Premise.** On the approval day, G4 records that no Caddy release contains Go >= 1.26.9 (or >= 1.27.2) and x/net >= 0.60.0, and that the pinned caddy digest is unchanged from #183 *(Delta 2026-10-29: the digest is now the head pinned under M-6, and the premise also covers go-jose: no Caddy release carries `go-jose/v4` >= 4.1.6)*. If a fixed release exists, the premise of item 4 is gone. Then there is no renewal: the route is the bump with its 7-day cooldown, and Marco decides.
  - **Scan before writing.** On the day the entries are written, run a local scan of the pinned digest. Every Go finding that no `#187` entry covers stays a FAIL, unless Marco approves its own entry under the same rules. This includes any advisory from a Go security release after 2026-10-08.
  - **The PR's CI `image-scan` run must show:**
    - each `#187` entry as `excepted (ADR-0015 item 4 ...)`;
    - no FAIL;
    - no stale `#187` entry;
    - no `#142` row.
  - **Done-when.** After merge, a `workflow_dispatch` or scheduled `image-scan` run dated 2026-11-01 or later is green and is recorded with its run id.

## SHOULDs

- **S-1 (fix-now, Low, `exceptions.json`).** The `#142` justifications name the old digest `sha256:d8542f48d34a`. The `#187` justifications name the current pin (`sha256:24ec7b05…`, #183) or no digest, and name #188 as the structural fix. *(Delta 2026-10-29: the pin they name is the new head `sha256:eb4d35b7452d…`.)*
- **S-2 (process).** A second renewal can only run to 2026-11-30. It needs a new issue, a fresh approval and this same rule set. Moving the sunset or the bound needs an ADR amendment and a full G3, and this delta does not pre-approve one.
- **S-3 (backlog, already on #83 as #142 S-5).** The guard covers `stackctl up`, not a raw `docker compose up`. Unchanged; it is not re-raised.

## Residual risk (the bridge window)

- **Acceptable for the bridge window: yes.**
  - The findings are High if Caddy were reachable. The stack is not deployed (#132, #29), and `stackctl up` refuses for as long as any item-4 entry is present. The guard reads no clock, so neither expiry nor a host clock setting lifts it.
  - The renewal changes only the CI decision. It lets `image-scan` and the images lane pass for at most 14 more days per approval.
  - The worst case is the ADR's accepted overlap: an unseen fixed Caddy release while an entry is active (T-07).
- **If #188 is not done by 2026-11-30:**
  - From 2026-12-01 item 4 is inert in code, and every fixed caddy `go-module` finding gets strict D-2 again.
  - The sunset tripwire turns the `.claude` suite red on every PR.
  - No entry can be renewed past 2026-11-30, because the validator rejects it.
  - The images lane and every scheduled scan stay red until a fixed Caddy image is pinned: the self-built one or a vendor release.
  - The deploy guard keeps refusing for as long as the expired entries are present.
  - Going further needs an ADR amendment and G3. The default is option 1 of the amendment: strict D-2, red until fixed.

## Has the Go advisory set grown since #142?

- **No, as of 2026-10-22.**
  - The #183 G4 evidence includes a local scan with Grype v0.120.0 of the current caddy digest `sha256:24ec7b05…`. It shows exit 0 and no FAIL, with all 16 `#142` caddy Go entries still excepted and none stale.
  - CI run `39804587759` on PR #184 has no FAIL, and its only stale entries are the 3 pre-existing libc6 ones.
  - So the set is the same 16 (11 `stdlib`, 5 `golang.org/x/net`).
- **It can grow before the merge day** (about 2026-10-30), for example through a Go security release. M-5 catches that: a new Go advisory is not covered by any existing entry, because matching is on exact image, package and id. It stays a FAIL until Marco approves an entry of its own.
- *(Delta 2026-10-29.)* The head scan of 2026-10-29 still shows the same 16 Go stdlib and x/net rows. It also shows go-jose v4 (fixed in 4.1.6) and three `apk` rows. The delta below covers all of them.

## Delta 2026-10-29: the upstream-Caddy bridge

- **Date and reviewer.** 2026-10-29, security-reviewer (G3, delta).
- **Trigger.** Marco's scope change of 2026-10-29 ("Bridge via #187: yes", manifest `docs/ai/pipeline/187.md`). #187 becomes the bridge for the pinned upstream Caddy until #188's second PR switches to the self-built image.
- **Sources read for this ruling:**
  - the manifest: the scope change, the stacking, the Facts (head digest, head scan rows, the 2026-10-29 reachability one-shot) and the bounds;
  - ADR-0015: D-2, item 3 of the 2026-10-06 amendment, and item 4 and its sunset in the 2026-10-17 amendment;
  - `image_scan.py`: `_keeps_fixed_exception`, `_keeps_vendor_binary_exception`, `is_vendor_binary_entry`, `_vendor_binary_errors` and `evaluate`;
  - `exceptions.json`: the `#142`, `#183`, `#190` and `#195` entries;
  - `test_stackctl.py:691-706`;
  - the Caddy pin and comment in `ContainerImages.cs`;
  - `deploy/caddy/Caddyfile`;
  - the caddy service in `docker-compose.yaml` and `docker-compose.stack.yaml`;
  - `deploy/tests/test_caddyfile.py`, the edge-rule cases;
  - `deployable-stack.md` (S-120-07 and the class table of the 2026-10-06 delta);
  - `image-scan-drift-195.md`.
- **Not on this branch.** #212's G3 (`caddy-image-go-jose-212.md`) and its review (`reviews/212.md`) are not on this branch. Their go-jose v3 class (b) argument and N-1 (the vendor image's v4 row) are taken from the orchestrator's summary as data. G6 on the combined head reads both files and checks that M-8 agrees with them.
- **Supersedes #195's G3 for the combined head only.** Two points of `image-scan-drift-195.md` are superseded by Marco's 2026-10-29 scope change, and nothing else in it is:
  - M-4: "#187's renewals must not reach the combined head";
  - M-5, second bullet: "no `caddy` row for CVE-2026-111497 and no `caddy` `curl`/`libcurl` FAIL; if either appears, stop".

  Those rows now appear as `excepted` under #187's item-3 entries (M-9).

### Rulings

1. **Pin bump to the head `sha256:eb4d35b7452d…`: accepted (M-6).**
   - **No waiver is needed.** The digest was pushed 2026-10-22T17:52:48Z, so its 7-day cooldown cleared 2026-10-29T17:52Z.
   - **It fixes nothing.** The head carries the same Caddy 2.11.7 binary and the same unfixed `brotli-libs`, `curl` and `libcurl`.
   - **What it buys.** It makes item 3's premise true: the tag resolves to the pinned digest, and that digest is the newest official build. The reasoning is the same as #195 ruling 2 for Postgres. What would be gaming: pinning anything other than the head, or keeping an entry after the tag moves (fail-closed in code).
   - **If the tag moves again before the commit or before the combined run, stop.** A moved tag is a new decision, and possibly a better one (a rebuild with the fixes).
2. **Item-3 entries `brotli-libs` CVE-2026-111497 (Critical) and `libcurl` CVE-2026-92392 (High): accepted, class (a), until 2026-11-12 at the latest (M-7).**
   - **The head evidence** (manifest Facts, `--network none` one-shot, 2026-10-29):
     - `brotli-libs` is needed only by `libcurl`, and `libcurl` only by `curl`;
     - the only ELF files that need libcurl or brotli are `/usr/bin/curl` and the libraries themselves;
     - `/usr/bin/caddy` is static (`ldd`: not a dynamic program), so the edge process loads neither library;
     - the Caddyfile has no `encode` directive, and stock Caddy's encoders are pure Go in any case;
     - the stack's healthcheck is BusyBox `nc -z`;
     - nothing under `/etc/caddy` mentions curl.
   - **Reachability.** The vulnerable code runs only if a process inside the Caddy container or image executes `curl`. No network request to Caddy can start one, so neither library is on the LAN request path (S-120-07).
   - **The `#183` `curl` entry.** It is valid again in code once the tag resolves to the pin (`apk`, 30 days, tag unchanged), but it is **not kept**. Its text no longer matches the head (T-09):
     - it was evidenced on `24ec7b05…`;
     - it says fix state "unknown", but the fix is `8.23.0-r0`;
     - it says libcurl "is not reported separately", but the head scan reports it;
     - it ends on 2026-11-21, past the bridge.

     It is replaced by a `#187` `curl` entry with the same evidence, the same end date and Marco's fresh approval. That is one record for one digest.
   - **The expiry.** Each entry ends by 2026-11-12. Item 3 allows 30 days, and `test_stackctl.py:703-706` caps `caddy` entries at 30 days; both hold.
3. **Item-4 entries: accepted, until 2026-11-12 at the latest (M-8).**
   - **go-jose v4.**
     - The finding: `github.com/go-jose/go-jose/v4` v4.1.5, GHSA-mgcc-cww5-fp2q, fixed in 4.1.6. No Caddy release carries the fix.
     - It is a `caddy` `go-module` finding with a `/` in the package, so it is an item-4 entry: the 14-day bound, Marco's approval, the sunset, and the deploy guard refuses while it is present.
     - Class (b), as #212's G3 rules for v3. The module is linked into the static binary. The vulnerable path is parsing attacker-supplied JOSE, which needs the `acme_server` handler (or an OIDC/JWT handler, which stock Caddy lacks). The Caddyfile configures neither. `tls internal` signs Caddy's own CSRs locally, and the ACME issuer is refused by `test_caddyfile.py` ("acme issuer").
     - If the advisory names an API that `tls internal` reaches with network input, the entry becomes "treated as reachable", like the Go set. It is still acceptable then, because the guard refuses while it is present.
   - **The 16 Go stdlib/x/net findings.** The original #187 scope. M-1 to M-5 apply unchanged, except that the digest named is the head.
4. **The combined stack (`main` ← #195 ← #187 ← #212): accepted under M-9.**
   - There is no bypass anywhere. The ruleset protects `main` only, so the gates (G6 per layer) do the protecting on the base branches.
   - The commit that reaches `main` has one green `image-scan` run on its exact SHA.
   - The deploy guard keeps refusing on the combined head: 17 `#187` item-4 entries, plus #212's v3 entry.
   - #188's second PR removes the bridge (M-10).
5. **Nothing here is unacceptable on the LAN edge. No BLOCK.**
   - brotli, libcurl and curl are class (a): there is no network path to them.
   - The Go set and go-jose v4 are reachable or class (b). They are bounded by the 14 days and the sunset, and the deploy guard keys on presence, so they cannot reach a running stack.
   - This holds only while every voiding condition in M-7 holds.

### Threats (delta)

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 id | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T-08 | Official image to pin (supply chain) | T | The pin is a non-head or not-yet-cooled digest. Or the image is not the official Caddy build, or carries a different Caddy binary or new findings that no entry reviewed | Medium | M-6: the digest read two ways at commit time, the cooldown, provenance, binary build info, and a head scan with exactly the manifest rows | V15.2.1 | Open, fix-now (G4) |
| T-09 | `#183` `curl` entry on a new digest | R | An approval and an argument given for `24ec7b05…` silently carry over to `eb4d35b7…`, and the text is now partly false (T120-12 class) | Low | M-7: replace it with a `#187` entry on the head evidence, with a fresh approval, ending by 2026-11-12 | V15.1.1 | Open, fix-now (a few lines in a changed file) |
| T-10 | Caddy container: brotli/libcurl/curl | T, I, D | A curl run inside the edge container (a healthcheck, a one-shot, or an operator `exec`) fetches from a hostile server. That process would sit in the edge container, next to Caddy's `/data` (the internal CA key) | Low in context (class a) | M-7 voiding conditions. Healthcheck `nc -z`; the existing one-shot ban on curl; item 3 ends at a tag move | V15.2.1 | Accepted to 2026-11-12 |
| T-11 | go-jose v4 in the edge binary | T, D | Hostile JOSE input reaches go-jose v4 | Low (class b), High if an `acme_server` were added | M-8 class (b) and its voiding condition. The deploy guard refuses while the entry is present | V15.2.1 | Accepted to 2026-11-12 |
| T-12 | Stacked merges under a deadline | T, E | A red or unreviewed layer merges into its base (no ruleset there), or a commit lands after the green run, or the required check is bypassed on `main` | Medium if done | M-9 | V15.1.1 | Open, fix-now (orchestrator, Marco) |

### MUSTs (delta; fix-now; G4 implements them, G6 checks them)

- **M-6 (T-08). The pin bump.**
  - **The digest at commit time.** Read the index digest of `docker.io/library/caddy:2.11.7-alpine` two ways: the Docker Hub tags API `digest`, and the registry `Docker-Content-Digest` or `buildx imagetools inspect`.
    - Both must equal `sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f`.
    - Record the push time, 2026-10-22T17:52:48Z.
    - The commit's UTC time is after 2026-10-29T17:52Z. There is no waiver.
    - If the tag has moved, stop and report.
  - **Provenance.**
    - The reference is in the official-images namespace, and the index includes `linux/amd64`.
    - Record the source and revision annotations, or the attestation's source. If one names a source other than the official Caddy image build, stop. If both are absent, record that.
    - In a `--network none` one-shot, record `caddy version` and the binary's build info: Caddy v2.11.7, go1.26.8, x/net v0.59.0, go-jose v3.0.5 and v4.1.5.
    - Record the sha256 of `/usr/bin/caddy` in the old pin and in the head.
  - **The scan.**
    - Use the pinned Grype v0.120.0 on the head with that day's DB, and record the DB build time.
    - The High/Critical rows must be **exactly** the manifest's Facts rows.
    - A new row is a FAIL. Stop and report; there is no entry without Marco's approval and a G3 ruling.
    - A missing row gets no entry, so that no exception is dead on arrival.
  - **Files.**
    - `ContainerImages.cs`: `CaddySha256`, and the comment says "bumped after the cooldown on 2026-10-29, no waiver; fixes nothing (same Caddy 2.11.7 binary; brotli-libs, curl and libcurl unfixed); bridge #187 until #188's second PR".
    - `images.Dockerfile`, kept equal by the parity test.
    - `deploy/compose/docker-compose.yaml`, line 77 only, regenerated by the publish and never edited by hand.
- **M-7 (T-09, T-10). Three item-3 entries, `image` `caddy`, `issue` `#187`.**
  - **The entries:** `brotli-libs`/CVE-2026-111497, `libcurl`/CVE-2026-92392 and `curl`/CVE-2026-92392. Use the id Grype reports in `vulnerability.id`.
  - **Remove the `#183` `curl` entry in the same commit.** The duplicate check forbids keeping both.
  - **Dates.**
    - `added` is the UTC commit date, not before Marco's approval.
    - `expires` is no later than 2026-11-12.
  - **Each justification states:**
    - the finding (version, severity, Alpine fix version);
    - that the head `eb4d35b7452d…` (the latest official rebuild) does not carry the fix, so item 3 applies while the tag resolves to it;
    - the head evidence of ruling 2: `apk info -r`, the ELF scan, the static `caddy` binary, the `nc -z` healthcheck, nothing under `/etc/caddy`;
    - class (a): no network request to Caddy can execute curl or load libcurl or brotli;
    - the stack is not deployed (#132, #29);
    - exactly one `approved by Marco YYYY-MM-DD` and `issue #187`;
    - "ends when the tag is rebuilt with the fix or when #188's second PR switches the image; bump the digest then; not renewed past 2026-11-12 without a new issue and a fresh G3".
  - **Voiding conditions, named in each justification.** The entry is void if any of these happens:
    - a Compose `healthcheck`, `command` or `entrypoint` for Caddy runs curl;
    - a smoke-test, runbook or stackctl one-shot runs `curl` from the Caddy image or container;
    - **an operator runs `docker exec` with curl in the Caddy container.** Operators use BusyBox `wget` or `nc` there;
    - Caddy loads libcurl or brotli (a cgo build);
    - a Caddy module that executes external programs is added.
  - **Banned wording:** "dev-only", and "not loaded" without "by the Caddy binary".
- **M-8 (T-11). The go-jose v4 item-4 entry, and Marco's approvals.**
  - **The entry.**
    - Fields: `id` as Grype reports it (GHSA-mgcc-cww5-fp2q or its CVE, with the GHSA in the text), `image` `caddy`, `package` `github.com/go-jose/go-jose/v4`, `issue` `#187`.
    - Its bounds are M-1's: at most 14 days from `added` and from the approval, and no later than 2026-11-12.
    - The justification states:
      - the version and the fix version (4.1.5 and 4.1.6);
      - that no Caddy release carries it (checked on the approval day, M-5);
      - class (b): `acme_server` is not configured, there is no OIDC/JWT handler, `tls internal` parses no network JOSE, and the ACME issuer is refused by the Caddyfile guard;
      - the voiding condition: any `acme_server`, `pki` endpoint or JOSE-parsing handler in the Caddyfile;
      - that the deploy guard refuses while it is present.
  - **Evidence for G4.** Record the advisory's affected API, and that the adapted JSON of the stack Caddyfile has no `acme_server` handler.
  - **Approvals, in Marco's own words, given after he reads this delta, one statement per group.** Agents write the entries without the phrase, and the file stays invalid until Marco adds it (M-2). The three groups:
    - **(A) Renewal.** The 16 Go stdlib/x/net entries, reachable once deployed, until 2026-11-12, under the deploy guard.
    - **(B) go-jose v4.** Class (b), until 2026-11-12, under the deploy guard.
    - **(C) Item 3.** `brotli-libs`, `libcurl` and `curl`: class (a) on the LAN edge (S-120-07), until 2026-11-12, with the voiding conditions above. These are not item-4 entries, so the deploy guard does not cover them on its own.

    Each statement is quoted with its date in the manifest and in the PR body.
- **M-9 (T-12). The combined stack.**
  - **Merging.** Each layer merges into its base only after its own G6 PASS or PASS-WITH-NOTES, by a merge commit. There is no force-push over reviewed commits.
  - **The PR to `main`.**
    - It merges only with a green required `image-scan` on its exact head SHA.
    - Nothing is pushed after that run. A push means a re-run.
    - There is no admin bypass, no ruleset change and no "merge without requirements".
    - A red layer is not merged on the promise of a later one.
  - **The green run on the combined head** (run id recorded by the orchestrator, on or before 2026-10-31 and in any case before the #195 cap of 2026-11-02). It shows:
    - `caddy` at `eb4d35b7452d…` with tag state `unchanged`;
    - the three M-7 rows `excepted (no rebuilt image: … still at the pinned digest)`;
    - the 16 Go rows and the go-jose v4 row `excepted (ADR-0015 item 4: …; expires <= 2026-11-12)`;
    - the go-jose v3 row excepted by #212's entry;
    - the Postgres and Keycloak rows as in #195 M-5;
    - no `FAIL`;
    - no `#142` or `#183` row;
    - no stale `#187` entry. A stale one means the set was mis-recorded: stop.
  - **The deploy guard.** On the combined head, record G4's guard evidence (M-4): `stackctl up` refuses, the message names `caddy`, the `#187` ids (go-jose v4 included) and the expiry, and the run makes zero `Runner` calls.
  - **The PR body to `main`** lists every new entry with Marco's approval and its date, and the no-waiver bump date.
- **M-10. The bridge ends.**
  - **#188's second PR** (the switch to the self-built image) removes every `#187` entry: 17 item-4 and 3 item-3. They go in the same commit as the switch, so that no commit pins the upstream digest with its findings unexcepted, and none leaves bridge entries stale.
  - Its own G3/G6 rules on #212's go-jose v3 entry. That entry is an item-4 entry, so the guard keeps refusing while it is present.
  - **If #188's second PR has not merged by 2026-11-12,** the scan turns red on 2026-11-13. Any further bridge needs a new issue, a fresh G3 and fresh approvals, and cannot pass 2026-11-30 (S-2).

### SHOULDs (delta)

- **S-4 (backlog #83, unless #212's G3 already requires it).** Add a handler allow-list to the Caddyfile guard (`deploy/tests/test_caddyfile.py`), so that an `acme_server` (or any JOSE-parsing handler) fails a test instead of only voiding M-8's argument. It is out of scope here, because M-4 keeps `deploy/tests/**` untouched.
