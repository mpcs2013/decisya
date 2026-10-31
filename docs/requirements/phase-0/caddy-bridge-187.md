# Issue #187 – Caddy upstream bridge (G5 traceability)

G1 was skipped for this issue (renewal under ADR-0015 item 4 as written). The rows below map the issue's Done-when and the G3 MUST items (`docs/security/threat-models/caddy-item4-renewal.md`, M-3 as edited in "Delta 2026-10-29", and M-6 to M-10) to the tests that prove them.

Tests, all in `tests/Decisya.Identity.Tests/ContainerImageParityTests.cs` (`[Trait("Category", "Unit")]`):

- T1 `The_exceptions_file_has_exactly_the_20_approved_187_caddy_bridge_entries_and_no_142_183_or_collector_Go_entry` (new, G3 M-3, replaces the removed `.claude` #142 test)
- T2 `The_exceptions_file_has_no_zlib_CVE_2026_85091_entry_and_one_approved_30_day_caddy_curl_CVE_2026_92392_entry` (adapted to `#187` and package `curl`)
- T3 `The_sandbox_image_list_has_a_caddy_alias_matching_ContainerImages` (parity of the bumped pin)
- T4 `The_Caddy_and_collector_references_are_exact_patch_tags_on_docker_io`
- T5 `Every_pinned_digest_is_64_lowercase_hex_characters`
- T6 `The_collector_is_pinned_to_0_163_1_with_its_digest_in_ContainerImages_the_sandbox_list_and_Compose`

## Traceability

| Requirement | Test |
| --- | --- |
| 20 `#187` entries, all image `caddy` (Done-when: renewed entries; M-3) | T1 |
| 17 item-4 entries: 11 `stdlib`, 5 `golang.org/x/net`, 1 `github.com/go-jose/go-jose/v4` (`GHSA-mgcc-cww5-fp2q`) (M-3, approvals A and B) | T1 |
| 3 item-3 entries: `brotli-libs`, `curl`, `libcurl` (M-3, approval C) | T1 |
| Every entry added 2026-10-29, expires on or before 2026-11-12 (14-day item-4 bound, bridge end; M-3) | T1 |
| Exactly one `approved by Marco 2026-10-29` per entry, no `PENDING` (M-2/M-3) | T1 |
| No `#142` or `#183` entry remains (T-09 replacement of the #183 `curl` entry; M-3) | T1 |
| Collector carries no Go exception (replaces the removed test's check; M-3) | T1 |
| Robust when #212's `go-jose/v3` entry is added (selection by issue, not image) | T1 (by construction; entry has issue `#212`) |
| `curl` CVE-2026-92392: single approved `#187` entry, at most 30 days (item 3) | T2 |
| Pin bump: `ContainerImages.cs` and sandbox `images.Dockerfile` carry the same Caddy reference (M-6, parity) | T3, T5 |
| Caddy tag is an exact patch tag on docker.io, digest-pinned | T4, T5 |
| Collector stays at 0.163.1 with digest (so no Go exception needed) | T6 |
| Compose file matches the pin (CI drift check) | manual: the Compose drift check runs in CI; Marco verified the one-line diff on 2026-10-29 |
| `image-scan` passes after 2026-10-31 with the entries (Done-when) | manual: only the CI scan with the pinned Grype and DB proves it (0 blocking, 21 excepted, recorded in the manifest) |
| Entry validator rules (expiry, stale entries, alias and package rules) (M-1/M-4/M-5) | manual: enforced by `image_scan.exception_errors` and `deploy/tests`, run in the main session (523 OK) |
| Deploy guard refuses `stackctl up` with item-4 entries (Done-when) | manual: covered by the existing `deploy/tests` stackctl guard suite, outside this lane |
| Binary identical to the old pin, provenance, reachability of curl/libcurl/brotli, adapted Caddyfile has no `acme_server` or JOSE handler (M-6 to M-9) | manual: evidence taken by `docker run --network none` and recorded in the manifest; no automated test can prove it. The handler allow-list test (S-4) goes to #83, fixed in #188's second PR |
| Removal of the bridge by #188's second PR (M-10) | manual: T1 and T2 must be changed or dropped in that PR (noted in the manifest) |

## Run

`dotnet build tests/Decisya.Identity.Tests -warnaserror`: 0 warnings, 0 errors. `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"`: 248 total, 248 passed, 0 failed, 0 skipped.

<!-- gate: G5 | verdict: PASS | issue: #187 -->
