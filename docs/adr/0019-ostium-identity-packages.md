# 0019. Consume the identity code as private Ostium packages

- Status: Accepted (Marco, 2026-10-15)
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
   - Decisya CI and Dependabot restore with `GITHUB_TOKEN` (`packages: read`) through per-package Actions access. No PAT.
   - A workstation restores with a `read:packages` credential. The steps are documented in #164.
2. **Freeze.**
   - Frozen paths: `src/Decisya.Bff`, `src/Decisya.Api/Authentication`, `deploy/keycloak`, and these test files:
     - the Keycloak test fixtures `tests/Decisya.Identity.Tests/KeycloakRealmFixture.cs`, `tests/Decisya.Api.Tests/Authentication/KeycloakApiFixture.cs` and `tests/Decisya.Bff.Tests/KeycloakBffFixture.cs`;
     - the realm guard `tests/Decisya.Identity.Tests/RealmGuard.cs` and its users `tests/Decisya.Identity.Tests/RealmGuardTests.cs`, `tests/Decisya.Identity.Tests/RealmExportFileTests.cs` and `tests/Decisya.Identity.Tests/ScanExclusionsTests.cs`.
   - Start: commit `79eb3d2`. End: when Decisya's CI restores its first Ostium package (planned in #164).
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
4. **Evidence chain.**
   - Ostium release tags are signed.
   - Tags are verified against Marco's signing key, pinned in Decisya by fingerprint (an allowed-signers file from #164), not against GitHub's Verified badge. The key is not stored in GitHub or in Ostium CI.
   - The SHA-512 of each `.nupkg` is recorded in the signed tag's message, or in a checksums file signed with the same key. For 0.1.1 this is done before #164.
   - Each package records its source commit (SourceLink repository metadata), and that commit is matched to the signed tag.
   - Each package has a CycloneDX SBOM, attached to the Ostium release at that tag.
   - Decisya restores in locked mode with NuGet lock files, so each package's content hash is pinned.
   - Every `Ostium.*` version change, Dependabot's included and 0.1.1 at #164 first, repeats the check before merge: the tag is signed with the pinned key, the SourceLink commit equals the tag commit, and the lock-file SHA-512 equals the signed value. Marco records the result in the PR. Ostium bumps are never auto-merged.
5. **Lock files.** Decisya uses central package management with transitive pinning, but no lock files, today. Lock files for every project and locked-mode restore in CI arrive with the first switch issue, #164, which already has them in scope. They are not a separate tooling issue.
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
