# 0017. Release images: SDK container publishing on pinned chiseled bases, release-please, syft SBOMs, keyless cosign

- Status: Accepted (Marco, 2026-10-04, #119 G2)
- Date: 2026-10-04
- Deciders: Marco
- Tags: security, tooling, hosting, release

## Context and problem statement

Phase 0 runs on Marco's home NAS (#124, ADR-0016, Proposed). The NAS has an Intel Celeron J3455, which is x86-64-v2 (SSE4.2, no AVX). It pulls the app images from GHCR as **public** packages by digest. Today nothing builds a release. There is no version, no image, no SBOM and no signature. There are no Dockerfiles. The app runs only under the AppHost, as host processes.

Issue #119 needs these properties:
- a release tag produces signed images in GHCR and a GitHub release with SBOMs;
- `cosign verify` with this repository's OIDC identity succeeds on each image and fails on an unsigned one.

The manifest adds five conditions for public packages:
1. no secrets or environment settings in images;
2. the image layers are scanned for secrets before push;
3. consumers pull by digest;
4. images are signed;
5. condition 1 holds before the first push, because a public package cannot be made private again.

Forces:
- **Supply chain (ADR-0014, ADR-0015):** every tool CI runs is pinned by SHA or digest and kept current by Dependabot. Trivy stays rejected.
- **Token exposure:** any step in a job with `id-token: write` can mint a signing identity. Any step in a job with `packages: write` can overwrite packages.
- **Solo-developer time:** one version and one release for the whole app. No hand steps beyond merging the release PR.
- **CPU:** nothing may need more than x86-64-v2.

## Decision drivers

- Smallest possible build surface: no build arguments, no hand-written image instructions.
- Signing and publishing run in a job that executes no repository build code and no npm or NuGet package code.
- Tools run as digest-pinned images, the same way as ADR-0015. No new third-party action holds a write token, except release-please, which the issue names.
- One source of truth for each pin, bumped by Dependabot.

## Considered options

### Image build

1. **.NET SDK container publishing (`dotnet publish -t:PublishContainer`), no Dockerfile, written to an OCI archive; settings central in `Directory.Build.props`/`.targets`** (chosen).
2. **Dockerfiles with BuildKit, one per project.** Rejected for the .NET apps:
   - each Dockerfile adds a multi-stage SDK image to pin;
   - it adds a build context, which needs a `.dockerignore` and can leak files;
   - it adds `ARG`/`ENV` surface, which is exactly what condition 1 guards;
   - the SDK writes the same layers without any of that.

   Dockerfiles stay the method for non-.NET images, such as #120's Caddy if it needs one.
3. **`aspire publish` to Compose plus images.** Rejected for now. Deployment shape is #120's decision, and Aspire's publisher does not cover signing or SBOMs.

### Release orchestration

1. **release-please (`googleapis/release-please-action`, SHA-pinned), `release-type: simple`, one version for the whole app, in the same workflow as the build, gated on `release_created`** (chosen). A release created with `GITHUB_TOKEN` does not trigger other workflows. Keeping everything in one workflow avoids a PAT or a GitHub App secret.
2. **A `release: published` or tag-push trigger in a second workflow.** Rejected: it needs a PAT or an App token to fire.
3. **Per-component versions.** Rejected. The migrator's schema and the API must match, and the BFF and SPA ship together. One version describes the deployable unit.

### SBOM

1. **syft (Anchore) as a digest-pinned image, CycloneDX JSON, over the OCI archive and the SPA lockfile** (chosen). Anchore is already trusted for Grype (ADR-0015). No new npm or NuGet package is needed.
2. **CycloneDX .NET and npm tools.** Rejected: they add two packages to the dependency graph, and they miss OS packages.

### Signing

1. **cosign keyless (Sigstore, GitHub OIDC), run as a digest-pinned image; sign and attest by digest; verify with an exact identity** (chosen).
2. **`sigstore/cosign-installer` action.** Viable fallback. Not chosen: a third-party action would run in the job that holds `id-token: write`.
3. **Key-pair cosign.** Rejected: the key would be a long-lived secret.

## Decision outcome

The chosen options above, with these rules.

1. **Images.** Three images: `ghcr.io/mpcs2013/decisya-api`, `decisya-bff` and `decisya-migrator`. Each is `linux/amd64` only and framework-dependent. No ReadyToRun, no Native AOT and no instruction-set options. The Web SPA has no image of its own. Vite builds it into the BFF's `wwwroot` (ADR-0008, `docs/architecture/spa-shell.md` D5), and the BFF image serves it.
2. **Bases.** The bases are `mcr.microsoft.com/dotnet/aspnet` (Api, Bff) and `mcr.microsoft.com/dotnet/runtime` (Migrator), Ubuntu 24.04 chiseled `-extra` variants. ICU and tzdata are needed because `InvariantGlobalization=false`.
   - They are distroless, have no shell and run as the non-root `app` user (UID 1654).
   - Ubuntu amd64 needs only x86-64-v1, so these bases satisfy ADR-0016's F6 check. RHEL/UBI 10 needs x86-64-v3, so a UBI or RHEL 10 base is not allowed.
   - Each base is pinned `tag@sha256` in one never-built file, `.github/release/base-images.Dockerfile`. Dependabot bumps it (ADR-0014 item 3). The release script passes it as `ContainerBaseImage`. An MSBuild check refuses any base without a digest.
3. **Tools.** syft, cosign and gitleaks are pinned `tag@sha256` in `.github/release/tools.Dockerfile`. Grype keeps its ADR-0015 pin. Each tool runs with `docker run`, receives only the mounts it needs, and gets `--network none` where it needs no network. The gitleaks version equals `GITLEAKS_VERSION` and the pre-commit `frozen:` version.
4. **Before push** (in a job with `contents: read` only):
   - every layer of every image archive is unpacked separately and scanned with gitleaks, so a file deleted in a later layer is still seen;
   - the image config is checked: non-root numeric user, environment keys from an allow-list only, no forbidden files (`appsettings.*.json` other than `appsettings.json`, `.env*`, `secrets.json`, keys and certificates outside the base's CA store), and the BFF has `wwwroot/index.html`;
   - Grype scans each archive with the ADR-0015 policy.

   Any failure stops the release before anything reaches GHCR.
5. **Push, sign, attest.** One job holds `packages: write` and `id-token: write`. It runs no checkout of build code, no `dotnet`, no `npm` and no third-party action. It does these steps in order:
   - pushes the scanned archive;
   - checks that the pushed config digest equals the scanned one;
   - signs each image **by digest**;
   - attests each image's CycloneDX SBOM.

   Tags are the SemVer version (releases) or `sha-<12 hex>` (dispatch dry runs). There is no `latest`.
6. **Verify.** The verification identity is exact; a regular expression is never used:
   - `--certificate-identity https://github.com/mpcs2013/decisya/.github/workflows/release.yml@refs/heads/main`
   - `--certificate-oidc-issuer https://token.actions.githubusercontent.com`
   - for releases, also `--certificate-github-workflow-trigger push` and `--certificate-github-workflow-sha <the release commit>`.

   The workflow's verify job also proves the negative case. Verifying an unsigned image (the digest-pinned base) must fail, and verifying our image under the wrong identity (`ci.yml`) must fail.
7. **Release assets.** These are uploaded only after verify passes: the CycloneDX SBOMs (one per image plus the SPA lockfile SBOM), `images.txt` (one `ghcr.io/…@sha256:…` per image) and `SHA256SUMS`.
8. **ADR-0015 amendment.** The weekly `image-scan` also scans the two base references from `base-images.Dockerfile` (aliases `aspnet` and `runtime`), so a CVE published after a release is reported weekly. Exceptions accept the new aliases, plus `api`, `bff` and `migrator` for the release-time archive scan.

### Consequences

- Good:
  - No Dockerfile, no build argument and no build context for the .NET apps, so condition 1 is checked on the actual image content rather than trusted to a convention.
  - The job that can sign or publish never runs package code. A compromised npm or NuGet dependency can falsify the build output (which is then signed), but it cannot mint a signature for something else or overwrite another package.
  - Every pin stays reviewable and Dependabot-bumped. Consumers verify with one exact command.
- Bad:
  - **The release PR from `GITHUB_TOKEN` does not trigger `pull_request` checks.** Marco closes and reopens it before merging so the required checks run, and the `claude-config` gate step needs a narrow exemption for that branch (see the architecture note).
  - The GitHub release is public a few minutes before its assets and signatures exist.
  - Signing is proven only after merge: `workflow_dispatch` must be on the default branch, and PRs never get `id-token: write`.
  - Signatures and the identity are recorded in the public Rekor log. That is acceptable for a public repository.
  - Keyless verification depends on Sigstore's public infrastructure being reachable at deploy time.
- Enforced by:
  - `.claude/tests/test_release_workflow.py` (permissions per job, triggers, no `pull_request_target`, no third-party action in the signing job, exact identity);
  - `.claude/tests/test_release_images.py` (layer unpacking, config and file checks, pin parsing, verify argv);
  - `.claude/tests/test_ci_pins.py` and `test_gitleaks_parity.py` (extended to the new pin files);
  - the MSBuild check in `Directory.Build.targets`;
  - the release workflow's verify job.
