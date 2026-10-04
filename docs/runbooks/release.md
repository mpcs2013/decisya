# Release: release-please, GHCR images, SBOMs, cosign

- Owner: devops · Last verified: 2026-10-04 (G4 of #119, .NET SDK from `global.json`). Nothing here has run against GitHub yet: the workflow is proven only after merge (dry run, then `v0.1.0`). Every command that agents cannot run (`gh`, Docker, `cosign`, `python .github/scripts/release_images.py` subcommands that need CI) is marked **(unverified)** until Marco has run it once.
- When to use: cutting a release, running the dry run, reading the pre-push results, switching the GHCR packages to public, verifying an image, recovering a failed release run, or bumping a base image or tool.
- Design: ADR-0017, `docs/architecture/release-supply-chain.md`. Threat model: `docs/security/threat-models/release-supply-chain.md`. Pins and the CI gates: `docs/runbooks/ci-security-gates.md`.
- Column 1 is Visual Studio 2026 or the GitHub web UI (Firefox). Column 2 is the CLI (the `gh` CLI, or a terminal: *View → Terminal* in VS 2026, the PowerShell terminal in VS Code). GitHub pages are opened in Firefox.

## What a valid signature proves

A valid `cosign verify` with the command below proves one thing: the image digest was signed by `.github/workflows/release.yml` on `main`, at the commit you named, for a `push` run, in the repository `mpcs2013/decisya`. It does **not** prove that the dependencies (NuGet, npm, the base image) were benign: a malicious dependency in the build is signed as produced. It does not prove the image is free of vulnerabilities either.

Consumers trust **digests and `cosign verify`**, never tags, release text, `images.txt` alone or `latest` (there is no `latest`). A dry-run image (`sha-<12 hex>` tag) has the same certificate identity as a release and is told apart only by the trigger and the commit, so **always pass `--certificate-github-workflow-trigger push` and `--certificate-github-workflow-sha`**. The script below prints exactly that. Do not retype the command.

Signatures and the workflow identity are recorded in the public Rekor log, for ever, whatever the package visibility. Nothing in them is secret.

## Prerequisites

| Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- |
| Repository setting (Marco only): *Settings → Actions → General → Workflow permissions → "Allow GitHub Actions to create and approve pull requests"* must be ticked. release-please cannot open its PR without it (S-119-04). Re-evaluate it if `required_approving_review_count` ever rises above 0. | Read the setting: `gh api repos/mpcs2013/decisya/actions/permissions/workflow` and expect `can_approve_pull_request_reviews: true` **(unverified; Marco runs it, agents never call this API)** |
| Repository setting (Marco only, S-119-08): *Settings → Actions → General → Fork pull request workflows → "Require approval for all external contributors"* | `gh api repos/mpcs2013/decisya/actions/permissions/fork-pr-contributor-approval` **(unverified; Marco runs it)** |
| A `cosign` binary, or Docker: the pinned cosign image in `.github/release/tools.Dockerfile` | `cosign version` **(unverified)**, or `docker --version` |
| Python 3 (for the command printer) | `python --version` |

## Pins

Every pin was resolved on 2026-10-04 and is checked by `.claude/tests/test_ci_pins.py`. To bump one, follow *Check or bump a pinned action* in `ci-security-gates.md`. Dependabot proposes the image bumps through the `/.github/release` entry.

| Where | Pins |
| --- | --- |
| `.github/release/base-images.Dockerfile` | `aspnet` and `runtime` `10.0-noble-chiseled-extra`, multi-arch index digests |
| `.github/release/tools.Dockerfile` | syft `v1.52.0`, cosign `v3.1.3`, gitleaks `v8.30.1` (parity with `ci.yml` and pre-commit), index digests |
| `.github/workflows/release.yml` | release-please-action `v5.0.0`, upload-artifact `v7.0.1`, download-artifact `v8.0.1` (commit SHAs) |

## Cut a release

| # | Step | Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Merge feature PRs with Conventional Commits. release-please opens or updates one PR `chore(main): release X.Y.Z` on branch `release-please--branches--main--components--decisya` after each push to `main`. It is opened by `GITHUB_TOKEN`, so no checks run on it yet. | *Pull requests* tab, open the release PR | `gh pr list --head release-please--branches--main--components--decisya` |
| 2 | Read the PR: version, changelog text and the three files it changes (`CHANGELOG.md`, `version.txt`, `.release-please-manifest.json`). A `Release-As:` footer or a `feat!:` picks the version, so check it is the one you expect. | *Files changed* | `gh pr diff <n>` **(unverified)** |
| 3 | **Close and reopen** the PR so the required checks run. | PR page → *Close pull request*, then *Reopen pull request* | `gh pr close <n>` then `gh pr reopen <n>` **(unverified)** |
| 4 | Wait for the six required checks. `claude-config` skips the pipeline gates for this PR only when the author is `github-actions[bot]`, the branch is exactly `release-please--branches--main--components--decisya` and the diff is only the three release files. A human push of any other file to that branch fails the gates. Commitlint still runs. | *Checks* tab | `gh pr checks <n>` |
| 5 | Merge the PR (Marco only). The merge push starts `release.yml`: `release-please` creates the tag and the GitHub release, `build` scans, `publish` pushes and signs, `verify` proves, `release-assets` uploads. | *Merge pull request* | `gh pr merge <n> --squash` **(unverified)** |
| 6 | Watch the run. `release-assets` runs only after `verify` is green. | *Actions → Release* | `gh run list --workflow release.yml --limit 3` then `gh run watch <run-id>` |

The GitHub release is public a few minutes before its assets and signatures exist. Do not deploy from it before the `verify` job is green.

## Dry run on `main` (right after #119 merges, and after any change to the release path)

Signing cannot be proven before merge: pull requests never get `id-token: write`, and a dispatch needs the workflow on `main`. The dry run pushes `sha-<12 hex>` tags only. It creates no release and does not run release-please. It proves the signing and verification path, including three negative cases (an unsigned base image, a wrong identity, and its own image taken for a release).

| # | Step | Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Run it on `main`. | *Actions → Release → Run workflow → Branch: main → Run workflow* | `gh workflow run release.yml --ref main` **(unverified)** |
| 2 | Find the run. | *Actions → Release*, newest run | `gh run list --workflow release.yml --limit 1` |
| 3 | Read the **build** job's pre-push results (next section). The packages are public from the first push, so these checks are the gate. | see below | see below |
| 4 | The **verify** job log shows `cosign verify OK` and `cosign verify-attestation OK` for `api`, `bff` and `migrator`, then `negative case 1`, `2` and `3` "failed as required". | *Actions → the run → verify* | `gh run view <run-id> --log` **(unverified)** |

The negative-case classifier matches cosign's error text (`no signatures found` for case 1, an identity or certificate mismatch for cases 2 and 3). It could not be tested offline against the pinned cosign. If a negative case reports `got 'other'`, read the printed cosign output: an expected failure with another wording means the pattern in `release_images.py` needs a reviewed edit (never loosen it to accept network errors). The first dry run may therefore need one iteration.

## Read the pre-push results

The `build` job of a pull request, the dry run and every release run all execute these checks before anything is pushed. Open the job log and read the block between the stop markers of each step (Firefox: *Actions → the run → build → "Pre-push checks and SBOMs"*; CLI: `gh run view <run-id> --log` **(unverified)**).

| Look for | Meaning |
| --- | --- |
| `canary: gitleaks reported the seeded token in layer 0 and passed the whiteout layer 1` | the secret gate can fail. If it is absent, the gate is not trusted: do not publish |
| `gitleaks <image> layer <n>: clean` for every layer of `api`, `bff`, `migrator`, and the config scan | no secret in any layer or in the image config |
| `skipped link ...` lines | absolute symlinks of the base (for example `usr/lib/ssl/certs`). Only links may be skipped; a rejected regular file fails the job |
| `grype <image>: ... 0 blocking` | no unexcepted High or Critical CVE (ADR-0015 policy) |
| `sbom decisya-...cdx.json: <n> components` (four files) | the SBOMs exist |
| `PROBLEM:` lines | a failed check. Nothing was pushed |

Record the URL of the green pull-request `build` run on the final head SHA in the issue's G4 evidence (G4-119-02).

## Package visibility (public from the first push)

**Observed on 2026-10-04 (#119, second dry run):** the first `GITHUB_TOKEN` push created `decisya-api`, `decisya-bff` and `decisya-migrator` **public**. They took the public repository's visibility, so the design's "private first push" did not happen. A public package cannot be made private again.

So **the `build` job's pre-push checks are the only control before publication**: the canary, the per-layer gitleaks scan, the config checks and Grype. No human looks first. Never weaken or skip them, and never push an image by hand. Marco decided on public images (#119, with conditions 1 to 5), and the first push met those conditions: the dry run's pre-push results were all green before `publish` ran.

| # | Step | Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | After each dry run or release, read its pre-push results (above). If a check ever failed after a push, treat the published versions as exposed: rotate whatever the finding names, then delete the versions. | *Actions → the run → build* | `gh run view <run-id> --log` |
| 2 | Check anonymous pull by digest on a machine without GHCR credentials. | — (terminal only) | `docker logout ghcr.io` then `docker pull ghcr.io/mpcs2013/decisya-api@sha256:<digest>` **(unverified)** |

Delete dry-run versions later from the package page (*Package settings → Manage versions*). They are `sha-<12 hex>` tags.

## Verify an image

Take the digests and the release commit from the release's `images.txt` and from the tag (`git rev-parse v<X.Y.Z>^{commit}`). The script prints the exact command: always `push`, always the commit.

| # | Step | Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Print the command (with a local cosign binary). | — (terminal only) | `python .github/scripts/release_images.py verify-command --image api --digest sha256:<digest> --sha <release commit> --runner cosign` |
| 2 | Or print it for the pinned cosign image. | — (terminal only) | `python .github/scripts/release_images.py verify-command --image api --digest sha256:<digest> --sha <release commit> --runner docker` |
| 3 | Run the printed command. It has this shape (flags in this order, no regular expressions): `cosign verify ghcr.io/mpcs2013/decisya-api@sha256:<digest> --certificate-identity https://github.com/mpcs2013/decisya/.github/workflows/release.yml@refs/heads/main --certificate-oidc-issuer https://token.actions.githubusercontent.com --certificate-github-workflow-repository mpcs2013/decisya --certificate-github-workflow-trigger push --certificate-github-workflow-sha <release commit> --certificate-github-workflow-ref refs/heads/main` | — (terminal only) | the printed command **(unverified)** |
| 4 | Attestations (the SBOM): add `--type cyclonedx` and use `cosign verify-attestation` with the same flags. | — (terminal only) | `cosign verify-attestation --type cyclonedx <the same flags> <ref>` **(unverified)** |

The release also carries `VERIFY.txt` with the three commands, `images.txt` (one `ghcr.io/...@sha256:...` per image), `SHA256SUMS`, and four CycloneDX SBOMs (`decisya-api`, `decisya-bff`, `decisya-migrator`, `decisya-bff-spa`). The BFF image has two attestations: the image SBOM and the SPA lockfile SBOM.

Expected failures, to show the check can fail: an image with another digest or a base image (`cosign verify` prints `no signatures found`), and the same image with `--certificate-identity ...ci.yml@refs/heads/main` (an identity mismatch).

## First release: `v0.1.0`

| # | Step | Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | After the dry run is green and the packages are public, merge release-please's PR as in *Cut a release*. The first version is `0.1.0` because `bump-minor-pre-major` is on and the manifest starts at `0.0.0`. | as above | as above |
| 2 | Confirm the release has the four SBOMs, `images.txt`, `VERIFY.txt` and `SHA256SUMS`, and that the three images carry the tag `0.1.0`. | *Releases → v0.1.0* | `gh release view v0.1.0` |
| 3 | Run *Verify an image* locally for each image. That proves Done-when 2 from outside CI. | as above | as above |
| 4 | Close issue #119 (the PR said `Refs #119`). | *Issues → #119 → Close* | `gh issue close 119` **(unverified)** |

## If the run fails the release cross-check

`release_images.py` fails before any build step when release-please's `sha` is not the pushed commit, its `version` is not the manifest's `"."` value, or `tag_name` is not `v` + `version`. The usual cause is a race: a newer push replaced a pending run, and a later run created the release. **Never edit the outputs or loosen the check.**

| # | Step | Visual Studio 2026 / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Delete the GitHub release and the tag it created. | *Releases → the release → Delete*, then *Tags → Delete* | `gh release delete v<X.Y.Z> --cleanup-tag` **(unverified)** |
| 2 | Re-run per the release-please documentation: push or merge a new commit to `main`, and release-please opens the release PR again. | | |

A failure in `build` (a `PROBLEM:` line) pushes nothing: fix the cause and push again. A failure in `publish` after the push leaves images without signatures; verify fails for them, and no assets are uploaded. Delete those versions from the package page and re-run.

## After a security bump

Dependabot opens the `/.github/release` bumps with the prefix `fix`, so a base-image CVE fix cuts a patch release. If a security change must ship without such a commit, merge a `fix:` commit or use a `Release-As: X.Y.Z` footer on the commit message.

For a Dependabot PR on `.github/release/tools.Dockerfile`, read the upstream release notes first, **cosign in particular**: it runs in the job that holds `id-token: write`, and no pull-request run ever executes it, so a bad cosign shows up only at the next release (S-119-08). A gitleaks bump also needs `GITLEAKS_VERSION` in `ci.yml` and the pre-commit `# frozen:` version moved in the same PR (`test_gitleaks_parity.py`).

A base-image bump is checked against x86-64-v2 (the NAS CPU, ADR-0016 F6): Ubuntu amd64 bases are fine; RHEL/UBI 10 bases need x86-64-v3 and are refused by `DECISYA0006`.

## Verify (this runbook worked)

- The dry run's `verify` job is green and prints three `negative case ... failed as required` lines.
- `images.txt` digests match `docker manifest inspect` of the tags, and `cosign verify` from your own terminal succeeds with the printed command and fails with `ci.yml` as the identity.
- `gh run list --workflow release.yml` shows the run for the merge commit of the release PR.

## Rollback

Nothing in `main` changes by running this procedure. To undo a release: delete the release and its tag (*If the run fails the release cross-check*), delete the image versions from the package pages, and revert the release PR's merge commit through a PR. A package that was made public stays public. Rekor entries are permanent.
