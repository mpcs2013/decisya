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
# #265: one-off cooldown waiver recorded by Marco on 2026-11-09 (#265, ADR-0014 item 4; ADR-0015 2026-10-06 amendment item 3, "No cooldown bypass"): docker.io/library/postgres:18-alpine@sha256:123b17998ccaf47e53b502e8c6a1e79afd8f579c6f3eec83c563da026f957395, pushed 2026-11-06 01:09:38 UTC, is pinned before its 7-day cooldown ends (2026-11-13 01:09 UTC), because #265 must merge before the go-jose v3 exception lapses on 2026-11-12. The rebuild fixes libcurl (8.23.0-r0: CVE-2026-92392 and the other libcurl findings) and brotli-libs (CVE-2026-111497). It does NOT fix zlib CVE-2026-4738, which ADR-0015 item 3 covers until 2026-12-09 while the tag resolves to this digest. Precedents #142, #185, #195. It sets no precedent: every later Postgres bump waits out the cooldown or needs its own recorded waiver.
FROM docker.io/library/postgres:18-alpine@sha256:123b17998ccaf47e53b502e8c6a1e79afd8f579c6f3eec83c563da026f957395 AS postgres
# Digest recorded 2026-10-03 (issue #28, bumped from 26.7.4 after image-scan found fixed Criticals;
# first recorded 2026-09-26 in docs/ai/pipeline/17.md, G4-17-17): the image the throwaway Keycloak
# Testcontainers fixture (issue #17) uses.
FROM quay.io/keycloak/keycloak:26.8.1@sha256:3f3c5f8e03c614774b4bd9173b88391fefa4153bfc010d8012d59ad825377332 AS keycloak
# #265 (D-3): the tag moved; index pushed 2026-10-26 15:05 UTC (cooldown ended 2026-11-02); still carries zlib CVE-2026-4738 (excepted under #265).
FROM docker.io/library/redis:8.10.2-alpine@sha256:9f8d2d789b9a1d1e3f283f979aa9cbf393373806472e1ccd460e2f9c869b833b AS redis
# #225 (ADR-0021 item 6): the self-built, signed Caddy 2.11.7 image, digest from caddy-image.yml run 41180269858 on main 8930453.
FROM ghcr.io/mpcs2013/decisya-caddy:2.11.7-8930453866c94e3f2b65f223c4ee9c8cc9b1318b@sha256:aa8e684446fde828f0d7beb786026a8b8815337dbc91545f809b906437c0e4d9 AS caddy
FROM docker.io/otel/opentelemetry-collector:0.163.1@sha256:f0e23036dce3db3f9cb80d907c75a0d034cd32e93511a1943482d46129b9ae9c AS otelcollector
