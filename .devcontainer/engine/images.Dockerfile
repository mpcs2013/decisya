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
# engine. Content: Postgres only (Marco's decision, O-4, docs/ai/pipeline/41.md) -- the minimum
# for the issue's Done-when. Add Redis or Keycloak only in the issue that first needs them, with
# a one-line justification in that PR body (CLAUDE.md working agreement).
#
# renovate/dependabot: tracked by the docker ecosystem entry for /.devcontainer/engine in
# .github/dependabot.yml if it can resolve digests from a file named "images.Dockerfile" that is
# never built; otherwise a documented monthly manual bump (docs/runbooks/agent-sandbox.md).
# Digest recorded 2026-09-24 (G4 evidence, docs/ai/pipeline/41.md): the image the throwaway
# Testcontainers smoke test in the Done-when uses.
FROM docker.io/library/postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873 AS postgres
