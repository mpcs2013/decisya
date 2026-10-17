# 0019. Consume the identity code as private Ostium packages

- Status: Accepted (Marco, 2026-10-15); amendment 2026-10-17 (#164) Accepted (Marco, 2026-10-17)
- Date: 2026-10-15
- Deciders: Marco
- Tags: security, supply chain, identity

## Context and problem statement

Decisya's identity code is the Api's token validation (ADR-0002, ADR-0003), the BFF (ADR-0003), the Keycloak test fixture and the realm guard tests. A second application needs the same mechanisms. Marco decided on 2026-10-11 that this code **moves**, not copies, to Ostium: a private repository of Marco's that publishes `Ostium.<Block>` NuGet packages, shared by Decisya and the second application. Ostium takes the code from Decisya commit `79eb3d2`, with its tests and a complete handover. Ostium published its first version, 0.1.1, on 2026-10-15.

Ostium separates Decisya-only logic (tenancy glue, Decisya names, admin paths, the `acr` value) into options or hooks, with no behaviour change. App-owned runtime names stay per app as options: cookie names, Redis prefixes, Data Protection names, realm and client ids, metric names and claim names. After the switch Decisya keeps no reusable identity mechanism. Its thin hosts, its realm content, its tenancy glue and its admin-MFA policy stay in Decisya. Each project's hardened production realm (#121) stays in that project and is never shared through Ostium.

Forces:
- **Security:** identity is the most sensitive code in the system. Moving it into a dependency must not open a window in which a fix lands in one copy and not the other, and must not weaken what Decisya can prove about the code it runs.
- **Supply chain:** Decisya pins what it runs (ADR-0014) and signs its own images (ADR-0017). A package from a private feed needs a comparable chain. GitHub Pro offers no artifact attestations for private repositories, and NuGet author signing needs a paid certificate, which is not bought.
- **Openness:** Decisya is public, Ostium is private. Packages published from a public repository stay public.
- **Solo-developer time:** one copy of each mechanism to maintain, test and review; no PAT to rotate in CI (a workstation uses a `read:packages` credential).

## Decision drivers

- One copy of each identity mechanism; no drift.
- No behaviour change at any switch, proven by the full identity suite.
- Every package Decisya restores is pinned by version and by content hash, and traceable to a signed source tag.
- A security finding is never deferred by the move.

## Considered options

1. **Move the code to private Ostium packages, switch block by block, freeze the Decisya copy until the first switch** (chosen).
2. **Keep a Decisya copy alongside Ostium.** Rejected: two copies drift, and every fix has to be made twice.
3. **Start Ostium as a public repository.** Rejected: packages published from a public repository stay public.
4. **Wait before starting the kit.** Rejected: the second application would get no shared blocks until much later.
5. **Artifact attestations or signed packages for the evidence chain.** Not available: attestations need a GitHub plan above Pro for private repositories, and author signing needs a certificate that is not bought.

## Decision outcome

Chosen option: **1**, because it leaves one copy of each mechanism, and the evidence chain below gives Decisya a pinned, traceable package without a paid service.

1. **Feed and restore.**
   - The feed is the private GitHub Packages feed of the `mpcs2013` account.
   - Package source mapping sends `Ostium.*` to that source only.
   - Versions are exact pins in `Directory.Packages.props` (central package management, as today).
   - Decisya CI and Dependabot restore with `GITHUB_TOKEN` (`packages: read`) through per-package Actions access. No PAT. *(2026-10-17, #164: whether Dependabot can use it for this feed is checked in #164; see the amendment, item A6.)*
   - A workstation restores with a `read:packages` credential. The steps are documented in #164.
2. **Freeze.**
   - Frozen paths: `src/Decisya.Bff`, `src/Decisya.Api/Authentication`, `deploy/keycloak`, and these test files:
     - the Keycloak test fixtures `tests/Decisya.Identity.Tests/KeycloakRealmFixture.cs`, `tests/Decisya.Api.Tests/Authentication/KeycloakApiFixture.cs` and `tests/Decisya.Bff.Tests/KeycloakBffFixture.cs`;
     - the realm guard `tests/Decisya.Identity.Tests/RealmGuard.cs` and its users `tests/Decisya.Identity.Tests/RealmGuardTests.cs`, `tests/Decisya.Identity.Tests/RealmExportFileTests.cs` and `tests/Decisya.Identity.Tests/ScanExclusionsTests.cs`.
   - Start: commit `79eb3d2`. End: when Decisya's CI restores its first Ostium package (planned in #164). *(2026-10-17, #164: the freeze ends for the switched block, `src/Decisya.Api/Authentication` and its tests, when #164 merges. The blocks not yet switched (the BFF, the Keycloak test fixtures, the realm guard) stay under the rule below: they are switched, not patched, so in practice they stay frozen until their own switch issue.)*
   - Not frozen: documents (ADRs, runbooks) and container image updates.
   - A High or Medium finding in frozen code is reported to Marco at once. Generic code is fixed in Ostium. App-owned code (realm content, tenancy glue, admin-MFA policy, the thin hosts and their tests) is fixed in Decisya under its own issue, as an exception to the freeze approved by Marco.
   - After the freeze ends: a security fix in Ostium for a block that Decisya has not switched yet makes switching that block Decisya's next identity issue. The old Decisya code is not patched.
   - For a switched block, an Ostium security release is bumped by hand at once, not left to Dependabot's cooldown.
   - No deployment, the NAS bring-up (#132) included, runs a block with an open High or Medium finding. Before each deployment, Marco checks Ostium's open High and Medium findings against the pinned versions.
3. **Switch order.** One Decisya issue per block, in this order:
   1. token validation, #164: `Ostium.Authentication.Jwt` and `Ostium.Backchannel` 0.1.1, with `Ostium.Authentication.Jwt.Testing` for tests;
   2. the Keycloak test fixture;
   3. the realm guard invariants;
   4. the BFF.

   Each switch passes the full identity suite against the packages (Integration with Keycloak, the realm guards, the architecture tests), with no behaviour change. Tests of generic behaviour move to Ostium with the code. Decisya keeps the tests for its own content (tenancy, seed users). The first switch changes CI, so it lands after #142.
4. **Evidence chain.** *(2026-10-17, #164: made concrete by the amendment below, items A1 to A4. Where the two differ, the amendment applies.)*
   - Ostium release tags are signed.
   - Tags are verified against Marco's signing key, pinned in Decisya by fingerprint (an allowed-signers file from #164), not against GitHub's Verified badge. The key is not stored in GitHub or in Ostium CI.
   - The SHA-512 of each `.nupkg` is recorded in the signed tag's message, or in a checksums file signed with the same key. For 0.1.1 this is done before #164.
   - Each package records its source commit (SourceLink repository metadata), and that commit is matched to the signed tag.
   - Each package carries its CycloneDX SBOM inside the package (`sbom/bom.cdx.json`), so the signed SHA-512 covers it.
   - Decisya restores in locked mode with NuGet lock files, so each package's content hash is pinned.
   - Every `Ostium.*` version change, Dependabot's included and 0.1.1 at #164 first, repeats the check before merge: the tag is signed with the pinned key, the SourceLink commit equals the tag commit, and the lock-file SHA-512 equals the signed value. Marco records the result in the PR. Ostium bumps are never auto-merged.
5. **Lock files.** Decisya uses central package management with transitive pinning, but no lock files, today. Lock files for every project and locked-mode restore in CI arrive with the first switch issue, #164, which already has them in scope. They are not a separate tooling issue. *(2026-10-17, #164: the pre-push check restores in locked mode too, and `NuGet.config` starts with `<clear/>`; amendment item A6. This closes FU-59-1.)*
6. **Versioning.** Ostium stays at 0.x, and breaking changes are allowed, until the second application has consumed and validated the shared blocks. 0.1.1 is the first published version.

### Consequences

- Good:
  - One copy of each identity mechanism, fixed once and used by both applications.
  - Decisya's identity code shrinks to app-owned content: thin hosts, realm content, tenancy glue, admin-MFA policy, and the tests for them.
  - Every Ostium package Decisya runs is pinned by version and by content hash. A changed package fails the locked restore.
  - Lock files also pin every other NuGet package's content hash, not only Ostium's.
- Bad:
  - **Accepted residual risk:** no party outside Marco vouches for the packages. The tag signature, the signed hashes, the SourceLink commit and the SBOM all come from Marco's GitHub account and Marco's signing key. A compromise of both the account and the signing key can produce a consistent but malicious chain.
  - Outsiders can read Decisya (public) but cannot build it, because the restore needs access to the private feed.
  - The freeze blocks identity changes in Decisya until #164 lands, and #164 waits for #142.
  - Until each block is switched, a security fix for its generic code reaches Decisya only through its switch issue.
  - Every Ostium bump needs a manual check and Marco's merge; none is auto-merged.
  - 0.x allows breaking changes, so a version bump can need code changes in Decisya.
  - Some evidence for the Phase 0 exit audit (Ostium's ASVS V6/V7 test report for the pinned version) lives in a private repository. It is attached to the signed release tag and referenced by tag and commit, not reproduced, and outside readers cannot inspect it (`docs/compliance/standards-register.md`).
  - Lock files add churn to every package update PR.
- Enforced by:
  - from #164: package source mapping, exact pins and locked-mode restore in CI; the allowed-signers file with the pinned key fingerprint; the tag, commit and hash check, recorded in the PR for every `Ostium.*` version change; the full identity suite against the packages; the architecture tests, with the rules that name `Decisya.Api.Authentication` retargeted in #164;
  - each later switch issue: the same suite for its block.
  - Not enforced by a test: the freeze. It holds by review until #164.

Related: [ADR-0002](0002-keycloak-identity-provider.md), [ADR-0003](0003-bff-session-pattern.md), [ADR-0011](0011-host-development-default.md) (host development; the workstation credential sits in Marco's user profile), [ADR-0014](0014-ci-supply-chain-pinning.md) (pinning), [ADR-0017](0017-release-images-sdk-containers-cosign.md) (SBOMs and cosign for Decisya's own images).

## Amendment 2026-10-17 (issue #164): the evidence chain as delivered for 0.1.1, key compromise, the ASVS report gap, FU-59-1

- Status: **Accepted (Marco, 2026-10-17)** (G2 of #164). The decision above stays Accepted. This amendment makes decision 4 concrete with what Ostium delivered (exchange D-004, O-006, O-010), and adds key-compromise handling, the ASVS report gap and the restore hardening of FU-59-1. It changes no other decision.
- Sources (data, read in the exchange): D-004 (terms, closed 2026-10-15), O-006 (0.1.1 hashes), O-010 (checksums file, signer line, check script, 2026-10-16). Marco checked the signer fingerprint against the key on his own machine on 2026-10-16 (manifest of #164).

### Context

Decision 4 allowed the SHA-512 values in either the signed tag message or a signed checksums file. Ostium signs the tag before its publish run builds the packages, and tags are immutable. So the values can only be in a checksums file, signed after the publish run. SSH signatures are bound to a namespace, so the line Decisya pins must allow a second namespace. D-004 also widened the ASVS evidence scope, and Ostium has no test report for 0.1.1.

### Decision (makes point 4 concrete, adds A5 and A6)

- **A1. Signed checksums, not the tag message.**
  - Each Ostium release has one checksums file on Ostium's `main`: `docs/releases/<version>/ostium-<version>.checksums`. It is committed through a reviewed Ostium PR.
  - It holds one line per package: `<package id> <version> <SHA-512, base64, as in packages.lock.json>`. From the first release with a test report, it also holds one line `file <report file name> <SHA-512, base64>` (A4).
  - It is signed with the release key in the SSH namespace `ostium-checksums`. The signature `ostium-<version>.checksums.sig` sits next to it.
  - Tags stay signed in the namespace `git`, with the same key.
  - For 0.1.1, the file's three values equal the values in O-006. Tag `v0.1.1`, commit `da7afc6be0097c45ec2ab043d74fd8b993a4399d`.
- **A2. The pinned signer line.**
  - Path: `.github/allowed-signers/ostium`. It holds exactly one line, ends in LF, and has no comment line. A second signer (for example Norma) gets its own file, never a second line here.
  - Content: `release@ostium namespaces="git,ostium-checksums" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAILkewFGdwh1FFykHc8gI0B6/hzjHppYG1CUOR20orqgX ostium-release-tags`
  - Fingerprint `SHA256:rRaNoF/hJgpjYhrZ61pZ7jnRdOnQYYvDo/eEZpWCctI` (ED25519).
  - `.gitattributes` keeps the file byte-exact (LF, no conversion), because the check script refuses any other content and reads the file from the committed `HEAD`.
  - The line changes only through a Decisya PR in which Marco has checked the new fingerprint against the key on his own machine, never only against the exchange.
- **A3. The check, for every `Ostium.*` version change.**
  - After a locked restore of the PR's head, Marco runs Ostium's check script from his own Ostium clone against Decisya's clone.
  - It verifies the pinned line and its fingerprint; the tag signature (namespace `git`) and that the tagged commit is on Ostium's `main`; the checksums signature (namespace `ostium-checksums`); every `Ostium.*` hash in every committed `packages.lock.json`; every restored `.nupkg`; and each package's SourceLink commit against the tag commit.
  - Marco pastes the printed block into the PR. A PR is never merged on `RESULT: FAIL`, or without the block.
  - Marco runs the script only on his own branches and on Dependabot's: the restore runs the branch's build code on the machine that holds the release key. A version change in any other PR is redone on Marco's own branch first.
  - Decisya CI does not run this check: it cannot read Ostium's repository. CI enforces the hashes through locked restore (decision 5) and a static test that pins the expected values of the current version.
- **A4. The ASVS report gap.**
  - Scope of the evidence: ASVS 5.0 V6, V7, V9 and V10 (D-004 widened "V6/V7").
  - Form: one Markdown report per Ostium release, generated from the publish run's test results at the tagged commit, committed next to the checksums file and listed in it, so the signature covers it. Each proving test carries an `Asvs` trait.
  - **0.1.1 has no report.** Its publish run kept no test results, and a report built later would not come from the release run. This is a known gap, not a waiver.
  - Before the Phase 0 exit audit, Decisya moves to the first Ostium release that has a report. If Decisya still runs 0.1.1 at that audit, the audit records the gap as open.
  - Until then, the behaviour of Decisya's host is proven by Decisya's own host-level tests against the package (#164), not by Ostium's report.
- **A5. Key change and suspected key compromise.**
  - *Planned key change:* Ostium announces it in the exchange, to both consumers, before the first release signed with the new key. Decisya replaces the line under A2 in its own PR, after Marco's fingerprint check, in the same PR as the first version signed with the new key.
  - *Suspected compromise:* Ostium announces it at once, with the versions signed since the last release Marco made himself, and for each whether its checksums file was re-signed with a new key or must not be used. Decisya then:
    1. merges no `Ostium.*` change and runs no deployment until steps 2 and 3 are done (decision 2's deployment rule);
    2. keeps versions checked before the last known-good release: their hashes stay pinned in the lock files and were checked against a signature made before the compromise;
    3. for a pinned version signed after it: moves back to the last known-good version in a PR, or, if Ostium re-signed that version with a new key, replaces the line (A2) and repeats the check (A3) in that PR;
    4. records the event and the outcome in #83.
  - Removing the old line means no signature made with the old key verifies any more. That is intended.
- **A6. Restore hardening (closes FU-59-1).**
  - `NuGet.config` at the repository root starts with `<clear/>` and declares two sources: nuget.org and the `mpcs2013` GitHub Packages feed.
  - Package source mapping sends `Ostium.*` to the feed only, and every other id to nuget.org only.
  - The audit source is nuget.org only.
  - It holds no credential.
  - Every project restores with a lock file. Restore is locked in CI and in the pre-push check. Every explicit restore passes `--locked-mode`, and no job that restores uses a NuGet package cache (#164 G3 M-1).
  - Build-policy errors in `Directory.Build.targets` stop a project from switching lock files off, from adding a restore source outside `NuGet.config`, or from switching locked mode off while CI's locked-restore variable is set. The dependency canary is the one explicit exemption.
  - *Dependabot:* the `registries` entry for the feed, and whether Dependabot can use the repository's `GITHUB_TOKEN` for it with no PAT, are checked and recorded in #164 (G4). Dependabot's support for NuGet lock files is checked there as well. If neither works without a PAT, Decisya adds no PAT. Dependabot then ignores `Ostium.*`, and Marco decides whether the NuGet updates it cannot make are an accepted gap, or whether this decision changes to a Dependabot secret.

### Consequences

- Good: every input of the chain is now fixed. That means the exact file, the namespace, the pinned line with its path and the check that Marco runs. A changed package fails CI (locked restore and the pinned hashes) before Marco's check runs.
- Good: a key compromise has a written path that keeps already-checked versions usable and stops new ones.
- Bad: the ASVS evidence for the Phase 0 exit depends on an Ostium release that does not exist yet (A4).
- Bad: Decisya CI cannot verify the signatures itself, because the check needs Ostium's private repository. CI proves the hashes; only Marco's PR record proves the signatures.
- Bad *(corrected after #164 G3, S-1)*: a fork PR may be able to restore the compiled packages. GitHub's documentation warns that forks may get access; it does not guarantee they are refused. Marco accepted this residual in exchange A-003 point 4: the compiled packages could reach a fork owner, and they ship in the public release images anyway, while Ostium's source stays private. The repository setting "Require approval for all external contributors" is recommended for fork workflows (`docs/runbooks/ci-security-gates.md`). A version change in an outside contributor's PR is redone on Marco's own branch (A3).
- Bad: the optional agent sandbox (ADR-0010) cannot restore Decisya from #164 on: its egress allow-list has no GitHub Packages host, and the sandbox has no feed credential. Allowing both is a sandbox threat-model change, out of #164's scope (#83).
- Enforced by: a static test over `NuGet.config`, `Directory.Packages.props`, `.github/allowed-signers/ostium` and every `packages.lock.json`; locked restore in CI and the pre-push check; the build-policy errors; Marco's `RESULT` block in the PR (by review, not by a test).
