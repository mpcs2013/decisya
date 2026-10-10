# The OWASP ZAP scanner (ADR-0019, issue #123). This file is NEVER built. It exists so that Dependabot's
# docker ecosystem keeps the pin current and .github/scripts/fullstack.py reads the one FROM line as the
# scanner reference (the single source). test_ci_pins.py requires exactly one such line.
#
# ZAP 2.17.0, dated build tag (the version tag moves weekly), approved by Marco 2026-10-09
#
# Digest: the multi-arch index digest of docker.io/zaproxy/zap-stable:20260807, read two ways (the Docker
# Hub API and `docker buildx imagetools inspect`). The image runs as UID 1000. It is a CI tool like Grype:
# never deployed, outside the image CVE scan (ADR-0019 decision 4).
FROM docker.io/zaproxy/zap-stable:20260807@sha256:781a2bdaea47324e7bab583e2263f21d257b0aee61ed51521a5be45f5f5081ef AS zap
