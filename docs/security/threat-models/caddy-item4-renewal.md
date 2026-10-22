<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #187 -->
# Threat delta: renew the caddy go-module item-4 exceptions until the self-built Caddy lands (#187)

- Date: 2026-10-22. Reviewer: security-reviewer (G3). Tier: Full (manifest `docs/ai/pipeline/187.md`; G1 and G2 skipped by Marco).
- Scope: the change only. It replaces the 16 `#142` caddy `go-module` entries in `.github/image-scan/exceptions.json` (they expire 2026-10-31) with renewed entries under `#187`, and changes the test that pins the `#142` set. The item-4 code, the deploy guard and the ADR are out of scope because the change must not touch them (M-4).
- Sources: ADR-0015 items 3 and 4 (amendment 2026-10-17); `.github/scripts/image_scan.py` (`is_vendor_binary_entry`, `vendor_binary_approval`, `_vendor_binary_errors`, `exception_errors`, `vendor_binary_entries`, `_keeps_vendor_binary_exception`); `deploy/compose/stackctl.py` (`vendor_binary_problems`); `.claude/tests/test_image_scan.py`; `.claude/tests/test_ci_gates_fail_closed.py` (`test_committed_file_is_valid`); the #142 G3 delta `image-scan-go-http2-exceptions.md` (M-6 to M-9, S-5); `docs/ai/pipeline/183.md` (G4 evidence).

## Verdict

**PASS-WITH-NOTES.** ADR-0015 item 4 already allows the renewal as a new decision: "A later entry for the same finding is a new decision: a new issue and a fresh approval, under the same rules, until the sunset." No policy, code or guard change is needed or allowed. G4 may start, subject to M-1 to M-5. The High exposure (T-01) is mitigated by the deploy guard, which keys on the entries being present and reads no clock, for as long as the stack stays undeployed.

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
  - **No entry for a finding that no longer matches.**
  - **Remove the `#142` entries; do not keep both.** The duplicate check enforces this. Remove the 16 `#142` entries and add the `#187` entries in the same commit, so that no commit leaves Caddy 2.11.7 pinned with no item-4 entry, which would let the guard pass.
  - **Fields.**
    - `issue` `#187`.
    - `added` is the UTC date of the commit that adds the entry. The validator rejects a future `added` in CI, which runs in UTC.
    - `expires` is no more than 14 days after `added`, no more than 14 days after the approval date, and no later than 2026-11-30.
  - **No other entry changes** (#83, #119, #120, #183). No `otelcollector` Go entry is added.
- **M-2 (T-02). Marco's approval anchors the window.**
  - Each justification carries exactly one `approved by Marco <YYYY-MM-DD>` and the text `#187`.
  - **Written by Marco.** Marco writes the phrase himself, as in #183 M-2. An agent writes the entries without it, and the file stays invalid (fail-closed) until he adds it.
  - **The approval date sets the end.** It is on or before `added`, and the 14 days run from it. An approval given on 2026-10-22 caps `expires` at 2026-11-05, whatever `added` says. To cover until about 2026-11-13 (manifest timing), Marco approves and the entries are committed on the merge day, about 2026-10-30.
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
  - **Premise.** On the approval day, G4 records that no Caddy release contains Go >= 1.26.9 (or >= 1.27.2) and x/net >= 0.60.0, and that the pinned caddy digest is unchanged from #183. If a fixed release exists, the premise of item 4 is gone. Then there is no renewal: the route is the bump with its 7-day cooldown, and Marco decides.
  - **Scan before writing.** On the day the entries are written, run a local scan of the pinned digest. Every Go finding that no `#187` entry covers stays a FAIL, unless Marco approves its own entry under the same rules. This includes any advisory from a Go security release after 2026-10-08.
  - **The PR's CI `image-scan` run must show:**
    - each `#187` entry as `excepted (ADR-0015 item 4 ...)`;
    - no FAIL;
    - no stale `#187` entry;
    - no `#142` row.
  - **Done-when.** After merge, a `workflow_dispatch` or scheduled `image-scan` run dated 2026-11-01 or later is green and is recorded with its run id.

## SHOULDs

- **S-1 (fix-now, Low, `exceptions.json`).** The `#142` justifications name the old digest `sha256:d8542f48d34a`. The `#187` justifications name the current pin (`sha256:24ec7b05…`, #183) or no digest, and name #188 as the structural fix.
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
