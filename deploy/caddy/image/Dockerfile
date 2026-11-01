# Self-built Caddy edge image (issue #188, ADR-0021, docs/architecture/self-built-caddy.md).
# Unlike the never-built pin files, this Dockerfile IS built, by .github/workflows/caddy-image.yml only.
#
# Rules this file keeps (each one is asserted by .github/scripts/caddy_image.py `inputs` and by the build):
#   - no parser directive on the first line: BuildKit's built-in frontend is used, never a frontend pulled by
#     tag (G3 M-1 c). `RUN --network=none` is part of that frontend.
#   - three FROM lines, each <registry>/<image>:<tag>@sha256:<64 hex>; Dependabot (docker, /deploy/caddy/image)
#     bumps them. The golang line moves by Dependabot only inside the current Go minor (semver-minor and major
#     are ignored in dependabot.yml); a security point release inside the cooldown follows the T2 rule in
#     docs/runbooks/caddy-image.md, golang line only, by hand (G3 M-4).
#   - the builder copies go.mod, go.sum and main.go by name: no COPY of the directory, no ADD (G3 M-1 b).
#   - the final stage is COPY only: no RUN (ADR-0021 item 4).
#   - nothing here holds a secret, and the build receives none.
#
# Pins resolved 2026-10-22, each digest read two ways (Docker Hub tags API and Docker-Content-Digest; for gcr.io the
# header equals the SHA-256 of the manifest body). golang 1.26.10 is a security release (crypto/x509, 2026-10-19),
# pushed 2026-10-21; busybox 1.37.0-musl pushed 2026-09-23 (1.38.0 was dropped: strict scan found CVE-2026-38753, -38754 and -38755, High, no fix, recorded against 1.38.0 exactly; Marco approved 1.37.0 on 2026-10-23). A digest of 64 zeros would be an unresolved pin:
# `caddy_image.py inputs` fails on it.
# Caddy v2.11.7: tag commit 72dd0fb067f6d7826c7f79907670ba4a713bfe37 (module proxy .info Origin.Hash, tag time
# 2026-10-03T05:06:18Z), checked against git ls-remote (G2 D1). Its go.mod has `go 1.26.0` and golang.org/x/net v0.59.0.

FROM docker.io/library/golang:1.26.10-bookworm@sha256:632dff7351799a5a9954a48dc06670ade72ee44e8c351cdc0e5a57af2fea21af AS builder

# The whole Go environment is fixed here (ADR-0021 item 2, G3 M-1 a). GOFLAGS is exactly -mod=readonly. GOPROXY has
# no `direct`, so every module comes through the proxy and is checked against go.sum and the checksum database.
# GOWORK=off keeps a go.work file out of the build. The GONO* and GOPRIVATE variables stay empty on purpose.
ENV CGO_ENABLED=0 \
    GOOS=linux \
    GOARCH=amd64 \
    GOAMD64=v1 \
    GOTOOLCHAIN=local \
    GOFLAGS=-mod=readonly \
    GOWORK=off \
    GOPROXY=https://proxy.golang.org \
    GOSUMDB=sum.golang.org \
    GOPRIVATE= \
    GONOPROXY= \
    GONOSUMDB= \
    GOINSECURE=

WORKDIR /src
COPY go.mod go.sum main.go ./

# Module download in its own step: the only step that needs the network. Compiling downloads and runs nothing.
RUN go mod download && go mod verify
RUN go mod tidy -diff

# Compile with no network. Tags and linker flags are Caddy's own release configuration (.goreleaser.yml at the
# release tag): -trimpath, tags nobadger nomysql nopgx, ldflags -s -w. -buildid= is empty on purpose, and
# -buildvcs=false, so the binary depends only on the toolchain, the build list and these flags.
# Version binding (G3 M-1 f): the toolchain, the binary's build info and go.mod must name the same versions.
# GOLANG_VERSION is set by the Docker Official golang image; `caddy_image.py inspect` ties the built binary's Go version to the FROM tag.
RUN --network=none set -eu; \
    test -n "${GOLANG_VERSION}"; \
    test "$(go env GOVERSION)" = "go${GOLANG_VERSION}"; \
    go build -trimpath -buildvcs=false -tags nobadger,nomysql,nopgx -ldflags "-s -w -buildid=" -o /out/caddy .; \
    info="$(go version -m /out/caddy)"; \
    caddy_version="$(go list -m -f '{{.Version}}' github.com/caddyserver/caddy/v2)"; \
    printf '%s\n' "${info}" | head -n 1 | grep -qx "/out/caddy: go${GOLANG_VERSION}"; \
    printf '%s\n' "${info}" | grep -q "^[[:space:]]dep[[:space:]]github.com/caddyserver/caddy/v2[[:space:]]${caddy_version}[[:space:]]"; \
    printf '%s\n' "${info}" | grep -q "^[[:space:]]build[[:space:]]-trimpath=true$"; \
    printf '%s\n' "${info}" | grep -q "^[[:space:]]build[[:space:]]CGO_ENABLED=0$"; \
    printf '%s\n' "${info}" | grep -q "^[[:space:]]build[[:space:]]GOAMD64=v1$"; \
    printf '%s\n' "${info}" | grep -q "^[[:space:]]build[[:space:]]-tags=nobadger,nomysql,nopgx$"

# BusyBox: one static binary and the links the stack uses (image contract in docs/architecture/self-built-caddy.md).
# Links only name applets; every compiled applet is still reachable as `busybox <applet>` (G3 S-3 c).
FROM docker.io/library/busybox:1.37.0-musl@sha256:5cec3fc171c87218698e85a52af7087de727372aae264a787b8112901a5b0092 AS busybox
RUN set -eu; \
    mkdir -p /out/bin; \
    cp /bin/busybox /out/bin/busybox; \
    for applet in '[' cat chmod chown echo find grep nc printf rm sh test tr wc wget; do ln -s busybox "/out/bin/${applet}"; done

# Plain root distroless static base (not the debug, nonroot or debug-nonroot variants): user root, as the stack
# runs it today (ADR-0021 M7). No libc, OpenSSL, zlib, curl or package manager.
FROM gcr.io/distroless/static-debian13:latest@sha256:448810357cc8e67b7208988abdba0f5ec3d3aebee0a5b13eb624979792cffc0f AS runtime

ARG CADDY_VERSION
ARG BUILD_REVISION
ARG BASE_NAME
ARG BASE_DIGEST
LABEL org.opencontainers.image.source="https://github.com/mpcs2013/decisya" \
      org.opencontainers.image.revision="${BUILD_REVISION}" \
      org.opencontainers.image.version="${CADDY_VERSION}" \
      org.opencontainers.image.base.name="${BASE_NAME}" \
      org.opencontainers.image.base.digest="${BASE_DIGEST}"

COPY --from=builder /out/caddy /usr/bin/caddy
COPY --from=busybox /out/bin/ /bin/

# Stock Caddy paths, as in the upstream image: the compose overlay mounts the data volume and a tmpfs here.
# No ENTRYPOINT: the stack runs `caddy adapt` and the key-ring one-shots with the command overridden.
ENV XDG_CONFIG_HOME=/config \
    XDG_DATA_HOME=/data
WORKDIR /srv
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
