# 0021. Self-built Caddy edge image: Caddy from source on a pinned Go toolchain, a distroless base with static BusyBox, built and signed by its own workflow

- Status: Accepted (Marco, 2026-10-22: M1–M7 as proposed)
- Date: 2026-10-22
- Deciders: Marco
- Tags: security, hosting, tooling, release
- Issue: #188. Architecture note: [self-built-caddy.md](../architecture/self-built-caddy.md).
- Amends:
  - [ADR-0015](0015-container-image-cve-scan-grype.md): §2 (the `caddy` target becomes our own image), item 3 (never for our own image), item 4 (retired);
  - [ADR-0017](0017-release-images-sdk-containers-cosign.md): one non-.NET image built from a Dockerfile, a second signing workflow and identity;
  - [ADR-0018](0018-deployable-stack-compose-secrets-transport.md), outcome 1: `caddy` stays a literal digest from `ContainerImages.cs`, but it is no longer a third-party image;
  - [ADR-0014](0014-ci-supply-chain-pinning.md), item 4, only if Marco chooses option T2 below.
- On acceptance, ADR-0014 (if T2), ADR-0015, ADR-0017 and ADR-0018 each get a dated pointer line to this ADR. Their decisions are not otherwise rewritten.
- 2026-10-30 (#222): [ADR-0022](0022-image-scan-pr-delta-gate.md) (Accepted 2026-10-30) revises item 8's last bullet ("The weekly `image-scan` keeps scanning the pinned `caddy`") for the pull-request path only. A pull request that changes the `caddy` pin fails only on a High or Critical finding that is new against the base. The scheduled scan of the pinned `caddy` stays (daily with #223), and `stackctl` refuses to start the stack without a fresh full scan. The strict build scan in `caddy-image.yml` (item 5) is unchanged. This ADR applies unchanged until the ADR-0022 implementation PR (#222) merges.

## Context and problem statement

The stack's edge is `docker.io/library/caddy:2.11.7-alpine` (ADR-0018, `deployable-stack.md` D2). That image is Caddy's release binary on an Alpine userland. This causes two problems.

1. **Go fixes reach the edge only with a Caddy release.**
   - GO-2026-6603 to 6613 and GO-2026-6617 were published on 2026-10-08. They are fixed in Go 1.26.9 / 1.27.2 and in `golang.org/x/net` 0.60.0.
   - Caddy's latest release is still v2.11.7 (2026-10-03), built with go1.26.8 and x/net v0.59.0.
   - The images lane survives on ADR-0015 item 4: 14-day exceptions (#142, then #187), a deploy guard that refuses while any such entry is present, and a hard sunset on 2026-11-30. After the sunset, strict D-2 returns and the `.claude` tripwire turns red.
2. **The Alpine userland brings High findings in packages Caddy never uses:** zlib, OpenSSL and curl (#120, #143, #183). Each one needs an exception or an upstream rebuild. #183 also needed a cooldown waiver.

Ruled out for now (issue #188): replacing Caddy (nginx, Traefik, a .NET/YARP edge), and waiting for upstream.

What the stack needs from the image is small and fixed; the architecture note lists it as the "Image contract":
- the `caddy` binary with the stock command and data paths;
- BusyBox `sh`, `nc`, `wget` with HTTPS, `cat`, and the file applets the key-ring one-shots use.

Forces:
- **Security:** Caddy is the only LAN-facing process, and it sits on the pre-authentication request path. Time-to-fix for its Go and module advisories is the metric that matters.
- **Supply chain:** this is a new image we own. Its build inputs must be pinned and reviewable like every other pin (ADR-0014), and its publishing and signing must keep ADR-0017's separation.
- **Solo-developer time:** Dependabot does the watching, and an update costs at most two small PRs.
- **Deadline:** in place before 2026-11-30.
- **CPU:** nothing may need more than x86-64-v2 (ADR-0016 F6).

## Decision drivers

- Go stdlib and module fixes reach the edge on our schedule, not on Caddy's release cadence.
- No package in the image that the stack does not use.
- Every build input is pinned by digest or checksum and bumped by Dependabot. Nothing unpinned executes.
- Publishing and signing keep ADR-0017's separation, and consumers verify with one exact identity.
- The stack contract stays as it is: the same Caddyfile, `caddy adapt`, key-ring one-shots, healthcheck and guards.

## Considered options

### A. How Caddy is built

1. **A pinned main module** (chosen). `deploy/caddy/image/main.go` is Caddy's own `cmd/caddy/main.go`: it imports `github.com/caddyserver/caddy/v2/cmd` and `.../modules/standard`.
   - `go.mod` has one direct requirement, `github.com/caddyserver/caddy/v2 vX.Y.Z`.
   - `go.sum` pins the checksum of every module. The checksums are verified against `sum.golang.org` when the file is written.
   - This is the shape xcaddy builds, without xcaddy. The Caddy source comes from the module proxy, and its hash sits in a public transparency log, so a moved git tag cannot change it.
2. **Clone the Caddy repository at the release tag and build `cmd/caddy`.** Rejected. A git tag is mutable, so it needs a separate commit-SHA pin and check. No checksum database covers it, and Dependabot can track it only as a submodule.
3. **xcaddy.** Rejected. It is one more tool to pin, and it runs `go get` and writes `go.mod` at build time. The build list would then be decided in CI instead of reviewed in a PR.

### B. Runtime base

1. **`gcr.io/distroless/static-debian13` plus a static BusyBox binary from `docker.io/library/busybox:<x.y.z>-musl`. Links only for the applets in the image contract** (chosen).
   - What remains: Debian `base-files`, `netbase`, `tzdata` and `ca-certificates` (as files), the static Caddy binary and the static BusyBox binary.
   - What goes: no libc, OpenSSL, zlib, curl or package manager.
   - Grype still sees a distro (Debian), so the provenance checks stay strict, and `BINARY_ONLY_ALIASES` is unchanged.
   - BusyBox `wget` uses BusyBox's own TLS code, so no OpenSSL is needed (G4 verifies).
2. **Alpine, reduced** (apk-tools, curl and the libraries removed in a build stage, then flattened). Rejected:
   - BusyBox `wget` needs `ssl_client`, so OpenSSL stays (zlib too, if apk-tools stays), and so does the finding class of #120, #143 and #183;
   - Alpine package fixes arrive through `apk upgrade` at build time (unpinned) or through a point release.
3. **The BusyBox image as the base, plus Caddy.** Viable. Rejected: there is no distro, so the scan would need `caddy` in `BINARY_ONLY_ALIASES` (a provenance relaxation with its own G3), and there is no CA bundle.
4. **Distroless with Caddy only.** The one-shots and the smoke client would move to a separate pinned BusyBox image, and a small probe binary of our own would serve the healthcheck. This is the strongest option, because no shell is left in the edge container. Rejected for #188: it needs a new scan alias, a new parity entry, `stackctl` and smoke changes, and code of our own, all before 2026-11-30. It goes to #83 as the next hardening step.

### C. Where it is built, scanned, SBOM'd and signed

1. **Its own workflow, `.github/workflows/caddy-image.yml`, on the ADR-0017 pattern** (chosen):
   - a build job with read-only permissions;
   - a publish/sign job that runs no build code;
   - a verify job with an exact identity.
2. **A fourth image in `release.yml`.** Rejected:
   - the digest would exist only after an app release, so it could not be in `ContainerImages.cs`;
   - that breaks ADR-0018's literal pin, the `caddy` scan alias, the parity test, the `caddy adapt` guard in `deploy-guards` and the `stackctl` one-shots;
   - every Caddy or Go bump would become an app release.
3. **Build in `ci.yml`.** Rejected: `ci.yml` runs package code, and ADR-0017 keeps publishing and signing out of such jobs.

### T. Go security releases and the 7-day cooldown (Marco decides)

1. **T1: the cooldown always applies** (status quo). A security point release inside the cooldown needs a one-off waiver, as for #142, #183 and #185.
2. **T2: a narrow standing rule for the builder line only** (recommended). The rule:
   - When the Go team announces a patch release as a security release, the `golang` builder pin may move inside the cooldown, once the Docker Official image for exactly that version exists.
   - The PR records the announcement, the image digest read two ways (registry API and `Docker-Content-Digest`), and Marco's approval.
   - Caddy, BusyBox, distroless and the module pins keep the cooldown. Module advisories already skip it through Dependabot security updates.

   Why: the point of #188 is time-to-fix. With T1, a fixed stdlib finding keeps `image-scan` red (strict D-2, below) for the whole cooldown plus two merges.

### E. ADR-0015 item 4 after the switch

1. **Retire it in the PR that re-points `caddy`** (recommended). That removes the constants, the validation and evaluation branches, the `stackctl` deploy guard, the tripwire and their tests, together with the entries.
2. **Leave it inert until the tripwire forces removal on 2026-12-01.** Re-pointing the `caddy` reference already makes it inert (S-120-15 pattern). Rejected: dead security code with a known red date, which needs the same G3 later.

## Decision outcome

Chosen: **A1, B1, C1, E1**, and **T1 or T2** as Marco decides (T2 recommended). The rules below are numbered for the tests and the note.

1. **Build inputs.** They live in `deploy/caddy/image/`, not under `.github/`, because Dependabot could not open PRs for `docker` entries under `.github/` (recorded in #83, 2026-10-17). The files:
   - `Dockerfile` (built, unlike the never-built pin files). It has three `FROM` lines, each `<registry>/<image>:<tag>@sha256:<64 hex>`:
     - the `golang:<x.y.z>-<variant>` builder, on the Go minor the current Caddy release was built with (1.26 today);
     - `busybox:<x.y.z>-musl`;
     - `distroless/static-debian13`, or the current Debian release distroless publishes.
   - `main.go`, `go.mod`, `go.sum`.
   - `modules.txt`, the expected `caddy list-modules` output.
2. **Build rules.**
   - Go environment, set in the Dockerfile and asserted by a test:
     - `CGO_ENABLED=0`, `GOOS=linux`, `GOARCH=amd64`, `GOAMD64=v1`;
     - `GOTOOLCHAIN=local` and `GOFLAGS=-mod=readonly`;
     - `GOPROXY=https://proxy.golang.org` (no `direct` fallback) and `GOSUMDB=sum.golang.org`;
     - `GOPRIVATE`, `GONOPROXY`, `GONOSUMDB` and `GOINSECURE` empty.
   - Before the build: `go mod download`, `go mod verify` and `go mod tidy -diff` must pass.
   - The build step runs with no network (`RUN --network=none`):

     ```
     go build -trimpath -buildvcs=false -tags <release tags> -ldflags "-s -w -buildid= <release flags>" -o /out/caddy .
     ```

     `<release tags>` and `<release flags>` are the build tags and the other linker flags in Caddy's release build configuration at that tag. `-buildid=` is deliberately empty.
   - `go.mod` has no `replace`, `exclude`, `retract` or `tool` directive. A `toolchain` line is allowed only if it does not exceed the builder.
   - With cgo off and no `go generate`, the build compiles dependency code but executes none of it. The only code that runs is the pinned toolchain and the pinned base images.
   - Each build compares `caddy list-modules` with `modules.txt` and fails on any difference. A Caddy bump that changes the module set updates the file in the same PR.
3. **Dependency overrides.**
   - The only allowed deviation from Caddy's own build list is raising a module to a version that fixes a published advisory, while no Caddy release carries that fix.
   - An override is recorded on its `go.mod` line as `// indirect; override <GO-id or CVE id> #<issue>`. The PR names the advisory. A version Caddy's master branch has already merged is preferred.
   - Each build also resolves the upstream build list (a scratch module requiring only the same Caddy version) and prints the difference in the step summary.
   - It fails when an annotated override is no longer above the upstream version (stale: remove it).
   - Stdlib fixes come from the toolchain pin, never from an override.
   - Initial override: `golang.org/x/net` v0.60.0, for GO-2026-6603 to 6613 and 6617, unless G4's scan shows a release already carries it.
4. **Image.**
   - The final stage is `COPY` only (no `RUN`). It contains `/usr/bin/caddy`, `/bin/busybox`, and links for exactly the applets in the image contract.
   - Configuration:
     - environment: only `PATH` from the base, plus `XDG_CONFIG_HOME=/config` and `XDG_DATA_HOME=/data`;
     - no `ENTRYPOINT`;
     - `CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]`;
     - `WORKDIR /srv`;
     - no `VOLUME`, no file capabilities and no setuid file.
   - User: root (0), exactly as today. B-2 stands unchanged: root with every capability dropped, a read-only root filesystem and one volume. Non-root is decision M7.
   - OCI labels: source, revision (the build commit), version (the Caddy version), and the base name and digest.
   - `linux/amd64` only.
   - **Amendment (Marco, 2026-10-23; #188 G4 evidence).** The pinned distroless base brings two things this item did not list. Both are allowed exactly as written; anything else still fails, and both are re-checked on every base bump:
     - an exec bit on four regular data files of the base: `usr/lib/os-release`, `etc/ssl/certs/ca-certificates.crt`, `usr/share/doc/ca-certificates/copyright` and `etc/update-motd.d/10-uname` (a small upstream shell script that nothing in the stack runs);
     - `SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt` in the environment, with exactly that value.
     - Layout fact, not an allowance: the base is merged-usr (`/bin -> usr/bin`), so BusyBox is the file `/usr/bin/busybox` and `/bin/busybox` resolves through the link. BusyBox has no `sleep` applet.
5. **Workflow `caddy-image.yml`.**
   - Triggers, exactly:
     - `pull_request` on the build paths: build and pre-push checks only;
     - `push` to `main` on the same paths: build, publish, verify;
     - `workflow_dispatch` with no inputs: a dry run, tagged `dryrun-<12 hex>` and never pinnable.
   - **build** (`contents: read`):
     - the image archive and the Go checks of items 2 and 3;
     - config and file checks (item 4, plus ADR-0017 conditions 1 and 2);
     - per-layer gitleaks;
     - Grype with strict D-2: `evaluate()` without `ref` and without `tag_state`, the release path;
     - a syft CycloneDX SBOM;
     - the artifact is uploaded except on pull requests.
   - **publish** (`packages: write`, `id-token: write`). It runs no Go, no `docker build` and no third-party action, from a sparse checkout of pins and scripts. It loads the archive, pushes it, checks the pushed config digest, then signs and attests by digest.
   - **verify:**
     - identity exactly `https://github.com/mpcs2013/decisya/.github/workflows/caddy-image.yml@refs/heads/main`, the GitHub OIDC issuer, `--certificate-github-workflow-trigger push` and `--certificate-github-workflow-sha <build commit>`;
     - negative cases: the unsigned distroless base fails, and our image under the `release.yml` identity fails.
   - The tools are the existing syft, cosign, gitleaks and Grype pins.
   - The package is `ghcr.io/mpcs2013/decisya-caddy`, public under ADR-0017 conditions 1 to 5. Marco switches the visibility after reading the first run's pre-push results.
   - The tag is `<caddy version>-<40-hex build commit>`. There is no `latest`.
6. **Pin and deploy.**
   - `ContainerImages.cs`:
     - `CaddyRegistry = "ghcr.io"`, `CaddyImage = "mpcs2013/decisya-caddy"`;
     - `CaddyTag = "<x.y.z>-<40 hex>"`, `CaddySha256` = the pushed manifest digest;
     - a comment naming the workflow run and the verify result.
   - `images.Dockerfile` carries the same line, and `docker-compose.yaml` is regenerated. The alias stays `caddy`.
   - The pin PR follows the publish run, because the digest exists only after the merge. It uses the commit type `fix(deploy)`, so release-please cuts a release whose tag carries the new pin. Deploys run from a clone at the release tag.
   - Before `stackctl up`, the runbook verifies the pinned reference with this identity. `caddy_image.py verify-command` prints the command; it is never retyped.
7. **Scan (ADR-0015).**
   - `caddy` stays a scan alias, and its reference is now our image.
   - New code constant `OWN_BUILD_ALIASES = {"caddy": "ghcr.io/mpcs2013/decisya-caddy"}`. While the reference matches, `run()` resolves no tag state and calls `evaluate()` without `ref`, so strict D-2 applies to every package type, as for the release images.
     - Why: our tag never moves, so item 3's same-tag check would read `unchanged` forever (the T120-19 class).
     - Like `BINARY_ONLY_ALIASES`, the constant is code, not configuration, and growing it needs a G3.
   - A `caddy` exception remains possible only for a finding without a fix: at most 90 days, with Marco's recorded approval (D7 rule of `deployable-stack.md`).
   - Item 4 is retired (E1). The #142/#187 `caddy` Go entries and the #183 `curl` entry are removed in the same PR.
8. **Tracking and who bumps.**
   - Go toolchain, BusyBox and distroless: Dependabot `docker` on `/deploy/caddy/image`, weekly, 7-day cooldown, prefix `chore`. It ignores `golang` semver-major and semver-minor updates; a Go minor moves by hand together with a Caddy bump.
   - Caddy releases: Dependabot `gomod` on `/deploy/caddy/image`, weekly, 7-day cooldown, prefix `chore`. Version updates are allowed for `github.com/caddyserver/caddy/v2` only.
   - Module advisories: Dependabot security updates on `go.mod` (no cooldown; a repository setting), or a hand override under item 3.
   - The pin itself: the `/.devcontainer/engine` entry ignores `mpcs2013/decisya-caddy`. The pin moves by hand after a publish run, like Keycloak (#105).
   - Who:
     - Dependabot opens the build-input PRs, which are exempt from the issue rule, and Marco merges them;
     - the publish run is automatic;
     - the pin PR is a normal issue PR: platform-dev for `ContainerImages.cs`, devops for `images.Dockerfile` and Compose, as in #183.
   - The weekly `image-scan` keeps scanning the pinned `caddy`. A new finding turns it red until a rebuilt image is pinned.

### Consequences

- Good:
  - Go and module fixes reach the edge as soon as a fixed toolchain or module exists. No item-4-style exception is needed, and none is possible.
  - The OpenSSL, zlib, curl, apk-tools and musl findings are gone from the edge. The image holds two static binaries on a distroless base.
  - The build list is reviewed in PRs (`go.mod`/`go.sum`), and every source module is checksum-verified against a public transparency log.
  - Signing keeps ADR-0017's separation, under an identity that cannot be confused with the app release identity.
  - The stack contract and every existing guard stay unchanged. Only the reference moves.
- Bad:
  - **Two PRs per update** (build input, then pin). Time-to-fix is the cooldown (or T2) plus two merges.
  - **We own an image:** a workflow, a public package, a second signing identity and a runbook section.
  - Our binary is not byte-identical to Caddy's release. An override creates a module combination that Caddy did not release. Mitigations: prefer versions Caddy's master has merged; the `modules.txt` check; the `caddy adapt` guards; the trust integration tests; the smoke.
  - **BusyBox stays in the edge container**, so an attacker who gets code execution still finds a shell, as today. B4 is deferred to #83.
  - BusyBox findings come from Grype's binary/CPE matching, which may be noisier than Alpine's security database. An unfixed High needs a 90-day exception with Marco's approval.
  - distroless publishes no version tags. Whether Dependabot bumps a digest-pinned `latest` is unproven; G4 checks on the first run. Fallback: the base digest is re-resolved in every pin PR.
  - Strict D-2 on our image: a fixed finding blocks the images lane until the rebuilt image is pinned.
  - New upstreams to trust: `proxy.golang.org` and `sum.golang.org`, the Docker Official `golang` and `busybox` images, and `gcr.io` distroless.
- Enforced by (the architecture note maps each one to its owner):
  - `.claude/tests/test_caddy_image_workflow.py` (new): triggers, permissions per job, no third-party action in publish, exact identity, no identity regexp, negative cases present;
    - Note (Marco, 2026-10-23, test-location decision option a, recorded in `docs/ai/pipeline/188.md`): the PR-1 tests live in `deploy/tests/` as `test_caddy_image_workflow.py`, `test_caddy_image_inputs.py` and `test_caddy_image_script.py`. devops writes them, and they run in `deploy-guards`. `test_ci_pins.py` stays a main-session `.claude` test;
  - `deploy/tests/test_caddy_image_inputs.py` (new):
    - the three `FROM` lines are `tag@sha256`;
    - the Go environment of item 2, and no `replace`/`exclude`/`retract`/`tool` in `go.mod`;
    - one direct requirement, and the override comment format;
    - `main.go` imports exactly the two Caddy packages;
    - the final stage is `COPY`-only, with the applet link set equal to the image contract;
  - `deploy/tests/test_caddy_image_tools.py` (new, Docker): in the pinned image, `nc -z` succeeds against a `caddy respond` listener and fails on a closed port, and the three key-ring scripts run on a scratch volume;
  - `.claude/tests/test_ci_pins.py` (the Dockerfile's `FROM` lines);
  - `.claude/tests/test_image_scan.py`: the exact `OWN_BUILD_ALIASES`; strict D-2 for a fixed `deb`, `binary` and `go-module` finding in our `caddy` with `tag_state` `unchanged`; no item-4 symbol is left;
  - `ContainerImageParityTests`: the `ghcr.io` registry, the `mpcs2013/decisya-caddy` image, a tag matching `^\d+\.\d+\.\d+-[0-9a-f]{40}$`, parity with `images.Dockerfile` and Compose;
  - `deploy-guards` (drift, Compose guards, `caddy adapt` in the pinned image), `image-scan`, the Api and BFF `CaddyRootTrustTests`, and the stack smoke on a Linux host;
  - the `caddy-image.yml` verify job.

### Decisions for Marco

| # | Decision | Recommendation |
| --- | --- | --- |
| M1 | Runtime base (B) | B1: distroless static plus static BusyBox; B4 to #83 |
| M2 | Build source (A) | A1: pinned main module |
| M3 | Pipeline (C) | C1: own workflow `caddy-image.yml` |
| M4 | Item 4 (E) | E1: retire it in the pin PR |
| M5 | Cooldown for Go security point releases (T) | T2: narrow standing rule for the builder line |
| M6 | Delivery | Two PRs under #188: PR-1 (`Refs #188`, build and publish) and PR-2 (`Closes #188`, pin, scan rule, item-4 retirement), as with #119's post-merge proof. The alternative is a second issue for PR-2. |
| M7 | Caddy user | Keep root without capabilities in #188 (like-for-like). Non-root (closes the B-2 SHOULD) to #83 |
