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
FROM docker.io/library/postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873 AS postgres
# Digest recorded 2026-10-03 (issue #28, bumped from 26.7.4 after image-scan found fixed Criticals;
# first recorded 2026-09-26 in docs/ai/pipeline/17.md, G4-17-17): the image the throwaway Keycloak
# Testcontainers fixture (issue #17) uses.
FROM quay.io/keycloak/keycloak:26.7.5@sha256:37dbaf6f0722c9ec246335f36e1ef8b2e6cb960f7c27e0d8c615121a3d475a85 AS keycloak
FROM docker.io/library/redis:8.10.2-alpine@sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0 AS redis
# caddy 2.11.7: pinned under Marco's one-off 7-day cooldown waiver of 2026-10-06 (issue #120, PR #136).
FROM docker.io/library/caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f AS caddy
FROM docker.io/otel/opentelemetry-collector:0.161.0@sha256:b6d2b9a85b1029d05b5ad913150c1f014eed4ae99be81a1813ca5ade4a191913 AS otelcollector
