# Image allow-list for the agent-sandbox container-engine sidecar (issue #41;
# docs/architecture/agent-sandbox-docker-sidecar.md, "Images: no registry on the allow-list").
#
# NEVER BUILT. `sandbox.py up --with-docker` parses this file with a strict regex and, for each
# `FROM` line, pulls the image on the HOST (full internet, the same trust path as the base images
# in ../Dockerfile and ./Dockerfile), then streams it into the sidecar via `podman load`. No
# registry is ever reachable from the sidecar or from any nested container (T-41-14).
#
# One line per allowed test image. Every entry MUST be pinned by `@sha256:` and followed by
# `AS <alias>`. Any other non-comment, non-blank line makes `sandbox.py` refuse to start the
# engine. Content: Postgres and Keycloak (#17) -- the minimum for each issue's Done-when. Add
# any further image only in the issue that first needs it, with a one-line justification in
# that PR body (CLAUDE.md working agreement).
#
# renovate/dependabot: tracked by the docker ecosystem entry for /.devcontainer/engine in
# .github/dependabot.yml if it can resolve digests from a file named "images.Dockerfile" that is
# never built; otherwise a documented monthly manual bump (docs/runbooks/agent-sandbox.md).
# Digest recorded 2026-09-24 (G4 evidence, docs/ai/pipeline/41.md): the image the throwaway
# Testcontainers smoke test in the Done-when uses.
# #265: one-off cooldown waiver recorded by Marco on 2026-11-09 (#265, ADR-0014 item 4; ADR-0015 2026-10-06 amendment item 3, "No cooldown bypass"): docker.io/library/postgres:18-alpine@sha256:123b17998ccaf47e53b502e8c6a1e79afd8f579c6f3eec83c563da026f957395, pushed 2026-11-06 01:09:38 UTC, was pinned before its 7-day cooldown ended (2026-11-13 01:09 UTC), because #265 must merge before the go-jose v3 exception lapses on 2026-11-12. The rebuild fixes libcurl (8.23.0-r0: CVE-2026-92392 and the other libcurl findings) and brotli-libs (CVE-2026-111497). It did NOT fix zlib CVE-2026-4738, which ADR-0015 item 3 covered while the tag resolved to that digest (superseded by the #325 pin below; the zlib exception is removed). Precedents #142, #185, #195. It sets no precedent: every later Postgres bump waits out the cooldown or needs its own recorded waiver.
# #325 (D-A = A4): the earlier 18-alpine rebuild, pushed 2026-11-15 04:08 UTC (cooldown ended 2026-11-22 04:08 UTC, no waiver); PostgreSQL 18.7 on Alpine 3.24.4; it clears 51 of the 70 High/Critical rows of the earlier pin (zlib, krb5-libs, libxslt and the 38 PostgreSQL 18.6 rows; the #265 zlib exception is removed). On the 2026-11-22 Grype database it still carries 19 High rows and NO exception covers any of them: G3's four OpenSSL 3.5.9-r0 ids on libssl3 and libcrypto3 (8 rows, owned by the follow-up bump S-3 to the first rebuild with OpenSSL 3.5.9-r1, deadline 2026-12-18), plus five newer OpenSSL ids (CVE-2026-107493, CVE-2026-107494, CVE-2026-121482, CVE-2026-124709, CVE-2026-84786) on both packages (10 rows) and libxml2 CVE-2026-86144 (1 row), those 11 owned by issue #347. The current tag head (18.7, pushed 2026-11-21) carries the same rows and is not a fix. Earlier pin (#265): 123b17998ccaf47e53b502e8c6a1e79afd8f579c6f3eec83c563da026f957395
FROM docker.io/library/postgres:18-alpine@sha256:b6972e1115a4ff8eba5d7f7d19184926d1e16d7d1136ae1e1a7ebf286da0f015 AS postgres
# Digest recorded 2026-10-03 (issue #28, bumped from 26.7.4 after image-scan found fixed Criticals;
# first recorded 2026-09-26 in docs/ai/pipeline/17.md, G4-17-17): the image the throwaway Keycloak
# Testcontainers fixture (issue #17) uses.
# #349: 26.8.2 (build 26.8.2-3, Ostium baseline O-047), index pushed 2026-11-19 07:42 UTC; cooldown waived by Marco 2026-11-22 for this index digest only. Earlier pin (#268): 26.8.1, sha256:3f3c5f8e03c6...
FROM quay.io/keycloak/keycloak:26.8.2@sha256:7e66a96e4739239070aeaf72e4c5decf0f7f682cfc725b9f7ea2b336eaba38b3 AS keycloak
# #325 (D-C): 8.10.3-alpine, index pushed 2026-11-11 21:05 UTC (cooldown ended 2026-11-18), read two ways 2026-11-20; carries zlib CVE-2026-4738 (#265 exception) and OpenSSL 3.5.9-r0 (four ids, #325 exceptions R-1). It also carries five newer OpenSSL ids (CVE-2026-107493, CVE-2026-107494, CVE-2026-121482, CVE-2026-124709, CVE-2026-84786) on libssl3 and libcrypto3 (10 rows), NOT excepted and owned by issue #347. Earlier pin (#265): 9f8d2d789b9a...
FROM docker.io/library/redis:8.10.3-alpine@sha256:7958cfbb9d152f87dadfbc89f1b0b15938f0b81ff8982feb327476f4911935c3 AS redis
# #225 (ADR-0021 item 6), re-pinned in #295: the self-built, signed Caddy 2.11.7 image built with the x/net and compress overrides (#265), digest from caddy-image.yml push run 42698263604 on main 02966b4 (the #265 merge). Must equal ContainerImages.cs and the Compose file.
FROM ghcr.io/mpcs2013/decisya-caddy:2.11.7-02966b4890d2d7098c9d5651151463b70506bc84@sha256:a9e0accec488a48fd3316ae6b6d53424c1324d532669887dfd20b8d47336a777 AS caddy
FROM docker.io/otel/opentelemetry-collector:0.163.1@sha256:f0e23036dce3db3f9cb80d907c75a0d034cd32e93511a1943482d46129b9ae9c AS otelcollector
