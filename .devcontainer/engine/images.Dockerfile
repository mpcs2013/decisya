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
# caddy 2.11.7: #183 rebuilt upstream 2026-10-21 (zlib 1.3.2-r1, OpenSSL 3.5.9), merged inside the cooldown under the one-off waiver of 2026-10-22 (#185); first pinned under the one-off waiver of 2026-10-06 (#120).
# #187: rebuilt upstream 2026-10-22, the bridge; fixes nothing (same Caddy 2.11.7 binary); item 3 covers brotli, curl and libcurl until 2026-11-12.
FROM docker.io/library/caddy:2.11.7-alpine@sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f AS caddy
FROM docker.io/otel/opentelemetry-collector:0.163.1@sha256:f0e23036dce3db3f9cb80d907c75a0d034cd32e93511a1943482d46129b9ae9c AS otelcollector
