# Caddy edge image: build inputs, publish, verify, pin, bump

- Owner: devops · Last verified: 2026-10-22 (G4 of #188, PR-1). **No command below has run against GitHub or Docker yet.** The agent that wrote it could run only `python .claude/scripts/lint.py`, `actionlint` and `dotnet build`. Every other command is marked **(unverified)** until the main session or Marco has run it once; the first PR run of `caddy-image.yml` and the first push run after merge are the runs that clear them.
- When to use: generating `go.sum` and `modules.txt`, reading the pre-push results of a `caddy-image` run, switching the package to public, verifying an image, pinning a published digest in the stack, or bumping the Go builder, BusyBox, distroless, Caddy or a Go module.
- Design: ADR-0021, `docs/architecture/self-built-caddy.md`, threat model `docs/security/threat-models/self-built-caddy.md` (M-1 to M-5, S-1 to S-5). Release path it is modelled on: `docs/runbooks/release.md`.
- Column 1 is Visual Studio 2026, VS Code or the GitHub web UI (Firefox). Column 2 is the CLI (the `gh` CLI, or a terminal: *View → Terminal* in VS 2026, the PowerShell terminal in VS Code). GitHub pages are opened in Firefox. Commands are written for a POSIX shell; the PowerShell form is given where it differs.

## What a valid signature proves

A valid `cosign verify` with the printed command proves one thing: the image digest was signed by `.github/workflows/caddy-image.yml` on `main`, at the commit in the tag, for a `push` run, in the repository `mpcs2013/decisya`. It does **not** prove that the Go modules, the toolchain or the base images were benign: they are signed as built. It does not prove the image is free of vulnerabilities (the weekly `image-scan` watches that).

The image is only valid for a consumer when **all** of these hold: the digest came from the `publish` job output of a push run for the commit named in the tag; that run's `verify` job was green; `cosign verify` with the exact identity passes. A `dryrun-<12 hex>` image carries the same certificate identity and is told apart only by the trigger, so **always pass `--certificate-github-workflow-trigger push` and `--certificate-github-workflow-sha`**. `verify-command` prints exactly that and takes the commit from the tag. Do not retype it.

## What the image holds, and what it does not do

- Two static binaries on `gcr.io/distroless/static-debian13` (plain root variant): `/usr/bin/caddy` and `/bin/busybox`, plus links in `/bin` for exactly these applets: `[`, `cat`, `chmod`, `chown`, `echo`, `find`, `grep`, `nc`, `printf`, `rm`, `sh`, `test`, `tr`, `wc`, `wget`. Nothing else is executable. No libc, OpenSSL, zlib, curl or package manager.
- **Two allowances for what the pinned distroless base ships (Marco, 2026-10-23, option a; G6 confirms against G3 M-1):**
  - Four regular files from `gcr.io/distroless/static-debian13` carry an exec bit: `usr/lib/os-release`, `etc/ssl/certs/ca-certificates.crt`, `usr/share/doc/ca-certificates/copyright` and `etc/update-motd.d/10-uname`. They are data files outside every `PATH` directory. `caddy_image.py` (`BASE_EXEC_FILES`) allows exactly these four besides the two binaries; any other executable, or a different set after a base bump, fails the build. Setuid, setgid and file-capability checks are unchanged.
  - The image Env carries `SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt` from the base. `caddy_image.py` (`BASE_ENV`) requires exactly that key with exactly that value; another value or any other extra key fails. After a base bump, re-check both sets and change them only by a reviewed diff.
- **There is no `sleep` applet.** Scripts run in this image must not rely on it.
- It runs as root, as the upstream image does today; the stack drops every capability and mounts the root read-only (ADR-0021 M7).
- **BusyBox `wget` does not validate server certificates.** The stack's smoke client passes `--no-check-certificate` on internal networks. Never use this `wget` for a fetch where the server's identity matters (G3 S-3 b).
- **Every compiled BusyBox applet is reachable as `busybox <applet>`, whatever the links** (G3 S-3 c). The links limit only names on `PATH`.

## Prerequisites

| Visual Studio 2026 / VS Code / GitHub UI (Firefox) | CLI |
| --- | --- |
| Docker running (the build, the checks and the go.sum generation all run in containers) | `docker --version` **(unverified)** |
| A `cosign` binary, or Docker: the pinned cosign image in `.github/release/tools.Dockerfile` | `cosign version` **(unverified)**, or `docker --version` |
| Python 3 (for the command printer) | `python --version` |
| **No Go on the host, no Go language server on this folder (G3 M-5, S-5).** The VS Code Go extension must stay disabled for this workspace: it would download modules and offer tool installs with the host's Go settings. VS Code: *Extensions → Go → Disable (Workspace)*. Never run `go run`, `go generate`, `go test`, `go install` or `go get`, and never use xcaddy or cgo, on the host or in a builder container. | `code --list-extensions` lists no `golang.go` as enabled for the workspace **(unverified)** |

## Build inputs

`deploy/caddy/image/` holds the whole input set and nothing else is allowed there (no `vendor/`, `go.work`, `go.work.sum` or other `.go` file):

| File | What |
| --- | --- |
| `Dockerfile` | Three `FROM` lines by `tag@sha256` (Go builder, BusyBox, distroless), the fixed Go environment, the no-network compile step with the version checks, a `COPY`-only final stage. Digests of 64 zeros are unresolved placeholders; `caddy_image.py inputs` fails on them |
| `main.go` | Caddy's own `cmd/caddy/main.go`: the two imports `.../v2/cmd` and `.../v2/modules/standard` |
| `go.mod` | One direct requirement (Caddy at its release tag) and annotated overrides: `// indirect; override <GO or CVE id>[,<id>] #<issue>` |
| `go.sum` | Generated, never hand-edited (next section) |
| `modules.txt` | The expected `caddy list-modules` output, generated once from the upstream release of the same version (next section) |

## Differences from Caddy's own release build

Pins of 2026-10-22: builder `golang:1.26.10-bookworm` (a security release, crypto/x509), `busybox:1.37.0-musl@sha256:5cec3fc171c87218698e85a52af7087de727372aae264a787b8112901a5b0092`, `distroless/static-debian13:latest`. BusyBox 1.38.0-musl was dropped on 2026-10-23: the strict scan found CVE-2026-38753, CVE-2026-38754 and CVE-2026-38755 (High, no fix, recorded against 1.38.0). 1.37.0-musl has only CVE-2025-60876 (Medium) and two Lows, which do not block; revisit with a fixed BusyBox release via Dependabot. Caddy v2.11.7 is tag commit `72dd0fb067f6d7826c7f79907670ba4a713bfe37` (module proxy `.info` `Origin.Hash`, G2 D1). Upstream's `.goreleaser.yml` builds with `CGO_ENABLED=0`, `-trimpath -mod=readonly`, ldflags `-s -w` and tags `nobadger,nomysql,nopgx`; so do we. Documented differences: we add `-buildid=` and `-buildvcs=false` (the binary depends only on toolchain, build list and flags), we build with Go 1.26.10 instead of upstream's 1.26.8, and `golang.org/x/net` is v0.60.0 instead of v0.59.0 (override, GO-2026-6603 to 6613 and 6617).

## Generate or refresh `go.mod`, `go.sum` and `modules.txt`

Run Go **only** in the pinned builder container (G3 M-5 a): the `golang` reference is the `FROM ... AS builder` line of the Dockerfile, by digest. The only bind mount is this folder. Never mount the Docker socket, the home directory or the repository root.

| # | Step | Visual Studio 2026 / VS Code / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Complete `go.mod` and write `go.sum`. `go mod tidy` adds the indirect requirements, keeps the override line and checks every module against `sum.golang.org`. It is the only step that writes the files. | — (terminal only) | `docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e GOCACHE=/tmp/gocache -e CGO_ENABLED=0 -e GOTOOLCHAIN=local -e GOFLAGS=-mod=readonly -e GOWORK=off -e GOPROXY=https://proxy.golang.org -e GOSUMDB=sum.golang.org -v "$PWD/deploy/caddy/image:/src" -w /src <builder reference> go mod tidy` **(unverified)**. PowerShell: drop `--user` and mount `"${PWD}/deploy/caddy/image:/src"` |
| 2 | Read the diff before the first commit (G3 M-5 d): the module list against Caddy's `go.mod` at the tag, the override line, and that no file outside the planned paths appeared. | *Git Changes* window, then *View → Terminal* | `git diff -- deploy/caddy/image` and `git status --porcelain --ignored` **(unverified)** |
| 3 | Record Caddy's own `go` line at the tag and make sure `go.mod`'s line is not lower (G3 M-1 d). The build job checks it too. | — (terminal only) | `docker run --rm -e GOTOOLCHAIN=local -e GOFLAGS=-mod=readonly -e GOPROXY=https://proxy.golang.org -e GOSUMDB=sum.golang.org -e GOWORK=off -v "$PWD/deploy/caddy/image:/src:ro" -w /src <builder reference> go list -m -f '{{.GoVersion}}' github.com/caddyserver/caddy/v2` **(unverified)** |
| 4 | Generate `modules.txt` from the **upstream** image of the same Caddy version, never from a binary downloaded to the host (G3 M-5 c). Read the image digest two ways first (registry API and `Docker-Content-Digest`). | — (terminal only) | `docker run --rm --network none --read-only --cap-drop ALL docker.io/library/caddy:<x.y.z>-alpine@sha256:<digest> caddy list-modules > deploy/caddy/image/modules.txt` **(unverified)** |

A Caddy bump that changes the module set changes `modules.txt` in the same PR; the build job fails on any difference.

## What the workflow does

| Event | Jobs | Notes |
| --- | --- | --- |
| Pull request on the build paths | `build` only | `contents: read`. Nothing is uploaded, pushed or signed |
| Push to `main` on the build paths | `build`, `publish`, `verify` | `publish` has the only `packages: write` and `id-token: write`; it runs no Go, no `docker build` and no third-party action |
| Manual dispatch (no inputs, `main` only) | the same three | A dry run: tag `dryrun-<12 hex>`. It proves the signing path and the negative cases; it is never pinnable |

The tag is `<caddy version>-<40-hex build commit>`. There is no `latest`.

| # | Step | Visual Studio 2026 / VS Code / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Dry run on `main` (after any change to this workflow). | *Actions → Caddy image → Run workflow → Branch: main → Run workflow* | `gh workflow run caddy-image.yml --ref main` **(unverified)** |
| 2 | Find the run. | *Actions → Caddy image*, newest run | `gh run list --workflow caddy-image.yml --limit 3` **(unverified)** |
| 3 | Watch it. | the run page | `gh run watch <run-id>` **(unverified)** |

## Read the pre-push results

Open the `build` job log and read the block between the stop markers of each step. A pull request runs the same checks as a push; only the push runs publish.

| Look for | Meaning |
| --- | --- |
| `inputs: all static input checks passed` | the Dockerfile rules, `go.mod`, `main.go` and the directory listing are as ADR-0021 says |
| `buildlist: every difference from the upstream build list is an annotated override or raised by one`, and the "Caddy build list against upstream" table in the step summary | no un-annotated deviation; no stale override (a stale one fails the job: remove it) |
| `canary: gitleaks reported the seeded token in layer 0 and passed the whiteout layer 1` | the secret gate can fail. If absent, do not publish |
| `gitleaks caddy layer <n>: clean` for every layer, and the image-config scan | no secret in any layer or in the config |
| `binary: /usr/bin/caddy sha256 ...` | the binary hash (compare with a host build, G3 S-2; if they differ, report it, do not explain it away) |
| `binary: go<x.y.z>, ... dependencies` | the Go version and module list in the binary's build info; compare with the upstream release binary (G2 D1) |
| `caddy list-modules: ... equal to modules.txt: True` | the module set is the expected one |
| `grype caddy: ... 0 blocking` | no High or Critical finding, with no exception possible (strict D-2) |
| `sbom decisya-caddy-<tag>.cdx.json: <n> components` | the SBOM exists and names the Caddy module, the Go stdlib and BusyBox at the pinned version, so none of their CVEs are invisible |
| `PROBLEM:` lines | a failed check. Nothing was pushed |

## Package visibility (public, irreversible)

The first push by `GITHUB_TOKEN` may create `ghcr.io/mpcs2013/decisya-caddy` public, as the release images did (`release.md`). **The build job's pre-push results are the only control before publication**: never weaken or skip them, and never push by hand. Marco switches the package to public only after reading the first run's pre-push results; the manifest records the date and that he read them (G3 M-2 f).

| # | Step | Visual Studio 2026 / VS Code / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Read the first run's pre-push results (table above). | *Actions → the run → build* | `gh run view <run-id> --log` **(unverified)** |
| 2 | Marco only: check or set the package visibility. | *github.com/mpcs2013 → Packages → decisya-caddy → Package settings → Danger Zone → Change visibility* | — (no CLI verb; agents never call this API) |
| 3 | Check anonymous pull by digest on a machine without GHCR credentials. | — (terminal only) | `docker logout ghcr.io` then `docker pull ghcr.io/mpcs2013/decisya-caddy@sha256:<digest>` **(unverified)** |

Delete `dryrun-` versions later from the package page (*Package settings → Manage versions*).

## Verify an image

Take the digest from the `publish` job output of the push run (*Actions → the run → publish → "Load, push and check the pushed config digest"*), never from a tag lookup. The reference is `ghcr.io/mpcs2013/decisya-caddy:<x.y.z>-<40 hex>@sha256:<digest>`.

| # | Step | Visual Studio 2026 / VS Code / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Print the command (local cosign). | — (terminal only) | `python .github/scripts/caddy_image.py verify-command --reference ghcr.io/mpcs2013/decisya-caddy:<x.y.z>-<40 hex>@sha256:<digest> --runner cosign` **(unverified)** |
| 2 | Or print it for the pinned cosign image. | — (terminal only) | `python .github/scripts/caddy_image.py verify-command --reference <the same> --runner docker` **(unverified)** |
| 3 | Run the printed command. Shape (no regular expressions): `cosign verify --certificate-identity https://github.com/mpcs2013/decisya/.github/workflows/caddy-image.yml@refs/heads/main --certificate-oidc-issuer https://token.actions.githubusercontent.com --certificate-github-workflow-repository mpcs2013/decisya --certificate-github-workflow-trigger push --certificate-github-workflow-sha <the 40 hex of the tag> --certificate-github-workflow-ref refs/heads/main ghcr.io/mpcs2013/decisya-caddy@sha256:<digest>` | — (terminal only) | the printed command **(unverified)** |
| 4 | The SBOM attestation: `cosign verify-attestation --type cyclonedx` with the same flags. | — (terminal only) | `cosign verify-attestation --type cyclonedx <the same flags> <ref>` **(unverified)** |

Expected failures, to show the check can fail: the same image under the `release.yml` identity (an identity mismatch), a `dryrun-` image with `--certificate-github-workflow-trigger push`, and the distroless base (`no signatures found`, or a mismatch when Google's own signature is found). The `verify` job proves all three on every run. If a negative case reports `got 'other'`, read the printed cosign output: the pattern in `release_images.py` (`MISMATCH_RE`, `MISSING_RE`) may need a reviewed edit, never one that accepts a network error.

## Pin a published digest in the stack (first done in #225, and every later bump)

The pin is a literal digest in `src/Decisya.AppHost/ContainerImages.cs` (`CaddyRegistry`, `CaddyImage`, `CaddyTag`, `CaddySha256`), the same line in `.devcontainer/engine/images.Dockerfile`, and the regenerated `deploy/compose/docker-compose.yaml`. The digest exists only after the publish run, so the pin is its own PR, commit type `fix(deploy)`, so release-please cuts a release whose tag carries the new pin (G3 M-2 e).

| # | Step | Visual Studio 2026 / VS Code / GitHub UI (Firefox) | CLI |
| --- | --- | --- | --- |
| 1 | Check that the run is a `push` run for the commit named in the tag, that its `verify` job is green, and note its run id and the digest. | *Actions → Caddy image → the run* | `gh run view <run-id> --json event,headSha,conclusion` **(unverified)** |
| 2 | Run *Verify an image* locally. | as above | as above |
| 3 | Edit the three files; put the run id and the verify result in the `ContainerImages.cs` comment and in the manifest. Run `ContainerImageParityTests` and the drift check. | *Test Explorer → ContainerImageParityTests* | `dotnet test --filter-not-trait "Category=Integration" --filter-not-trait "Category=AppHost"` **(unverified)** |
| 4 | Never pin a `dryrun-` tag, a branch-run image, an image signed by `release.yml`, or a digest read from a tag. The parity test's tag regex rejects the first. | — | — |

## Bump an input

Dependabot opens the build-input PRs (exempt from the issue rule). Marco merges them. The PR build runs `caddy-image.yml` on the paths, so read it before merging. After the merge, the push run publishes automatically; then the pin PR follows (above). Two PRs per update is the price of a digest that exists only after the publish run (ADR-0021).

| Input | Who bumps | Rule |
| --- | --- | --- |
| `golang` builder (digest or patch) | Dependabot `docker` on `/deploy/caddy/image` | 7-day cooldown. Dependabot ignores semver-minor and semver-major: a Go minor moves by hand together with a Caddy bump (the `go` line of Caddy's `go.mod` is re-checked). A Go security point release inside the cooldown: see T2 below |
| BusyBox, distroless | Dependabot `docker` | 7-day cooldown. A BusyBox bump changes the pinned version the SBOM check expects. Whether Dependabot bumps the digest of the `latest`-tagged distroless line is unproven: record it on the first run; fallback: re-resolve the digest in each pin PR |
| Caddy release | Dependabot `gomod`, `github.com/caddyserver/caddy/v2` only | 7-day cooldown. The PR changes `go.mod` and `go.sum`; if the upstream `main.go` or the module set changed, the same PR updates `main.go` and `modules.txt`; if the new tag's Go minor differs, bump the builder by hand |
| Another Go module (an advisory) | Hand override, or Dependabot security updates | Add `// indirect; override <GO or CVE id> #<issue>` on the `go.mod` line in the same PR (a version Caddy's `master` already merged is preferred). Remove it when upstream catches up; the build fails on a stale override |
| Go stdlib fix | the builder pin only, never an override | |

### T2: a Go security point release inside the cooldown (Marco's decision M5, conditions G3 M-4)

All of these must hold; otherwise the cooldown applies:

1. Only the `golang` `FROM` line of the Dockerfile moves, to a patch release in the current minor and the same variant. The PR changes nothing else. Never for a Go minor, BusyBox, distroless, Caddy, Go modules, the release, scanner or tool pins, or actions.
2. The Go team's own release announcement for exactly that version calls it a security release and lists the GO or CVE ids. A pre-announcement, a third-party report or an agent summary is not enough.
3. The Docker Official image for exactly that tag exists; its digest is read two ways (registry API and `Docker-Content-Digest`); the build log shows the announced `go<x.y.z>`; the PR's scan shows the targeted stdlib findings gone.
4. It is a hand PR under an issue (not Dependabot-exempt). The manifest records the announcement link, the ids, both digest reads, the image's push time and Marco's approval, written by Marco or quoted from his own message with its date. An agent or orchestrator message is never that approval. A comment on the `FROM` line names the T2 rule and the issue.
5. Prefer to wait 24 hours after the image was pushed, unless the advisory reports exploitation in the wild (G3 S-6; Marco decides, the manifest records it).

If Marco rejects any condition, T2 falls back to a one-off waiver (T1).

## First-run checks (record in the manifest)

| Check | How |
| --- | --- |
| Whether the runner's Docker produces an archive that `docker save` / `docker load` / `docker push` keep as one manifest with the same config digest | the `push` step passes only if the pushed config digest equals the scanned archive's |
| Whether BusyBox's own TLS completes a handshake with Caddy's internal-CA leaf (`wget`) | `deploy/tests/test_caddy_image_tools.py` (`WgetTls`, Docker; runs in `deploy-guards`) and `deploy/tests/stack_smoke.py` on a Linux host (#225). If either fails, Marco chooses between adding `ssl_client` and OpenSSL back and moving the smoke client to another pinned image |
| Whether Dependabot's `gomod` `allow` list (Caddy only) also suppresses security updates for indirect modules (G3 S-1) | *Insights → Dependency graph → Dependabot* and the first security alert on `deploy/caddy/image/go.mod`. If it does, the hand override plus the weekly `image-scan` are the module-advisory path |
| Whether Dependabot bumps the distroless digest | the first weekly run |

## Verify

- `python .github/scripts/caddy_image.py inputs` ends `inputs: all static input checks passed` **(unverified)**.
- A pull request touching `deploy/caddy/image/**` shows a green `build` job with the log lines of *Read the pre-push results*.
- A push run ends with `verify` green: `cosign verify OK`, `cosign verify-attestation OK` and `negative case 1`, `2` (and `3` on a dry run) "failed as required".
- Your own `cosign verify` of the digest passes with the printed command.

## Rollback

- A failed or wrong publish: do not pin it. Nothing in the stack references a digest until the pin PR merges, so the stack is unaffected. Delete the package versions (*Package settings → Manage versions*), and treat any version published after a failed secret check as exposed: rotate what the finding names first.
- A bad build-input PR that already merged and published: revert it (`git revert`), let the next push run publish a fixed image, and pin that one. The previous pin stays valid until then.
- A bad pin PR: revert it; the release tag keeps the old pin.
