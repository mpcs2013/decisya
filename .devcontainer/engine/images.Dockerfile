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
# #195: rebuilt upstream 2026-10-26, taken under a one-off cooldown waiver for pin currency; it does not fix CVE-2026-111497 or CVE-2026-92392 (ADR-0015 item 3 covers them until 2026-11-02).
FROM docker.io/library/postgres:18-alpine@sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5 AS postgres
# Digest recorded 2026-10-03 (issue #28, bumped from 26.7.4 after image-scan found fixed Criticals;
# first recorded 2026-09-26 in docs/ai/pipeline/17.md, G4-17-17): the image the throwaway Keycloak
# Testcontainers fixture (issue #17) uses.
FROM quay.io/keycloak/keycloak:26.7.5@sha256:37dbaf6f0722c9ec246335f36e1ef8b2e6cb960f7c27e0d8c615121a3d475a85 AS keycloak
# #183: rebuilt upstream 2026-10-22 (zlib 1.3.2-r1, OpenSSL 3.5.9).
FROM docker.io/library/redis:8.10.2-alpine@sha256:615b4cedaff0c1437a1548acae4222572df8c021eeb7382d64cb8b5e3a13ac7a AS redis
# #225 (ADR-0021 item 6): the self-built, signed Caddy 2.11.7 image, digest from caddy-image.yml run 41180269858 on main 8930453.
FROM ghcr.io/mpcs2013/decisya-caddy:2.11.7-8930453866c94e3f2b65f223c4ee9c8cc9b1318b@sha256:aa8e684446fde828f0d7beb786026a8b8815337dbc91545f809b906437c0e4d9 AS caddy
FROM docker.io/otel/opentelemetry-collector:0.163.2-386@sha256:6af85116b470638e039d957f130990f0de7aaa46e51863e8c133e83590758992 AS otelcollector
