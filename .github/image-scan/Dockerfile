# The container-image CVE scanner (ADR-0015, issue #28). This file is NEVER built. It exists so that
# Dependabot's docker ecosystem keeps the pin current and .github/scripts/image_scan.py reads the one
# FROM line as the scanner reference. test_ci_pins.py requires exactly one such line.
#
# Digest: the multi-arch index digest of ghcr.io/anchore/grype:v0.120.0, resolved two ways with the
# Docker CLI not involved (issue #28, G4):
#   1) GET https://ghcr.io/v2/anchore/grype/manifests/v0.120.0 (anonymous token from
#      https://ghcr.io/token?service=ghcr.io&scope=repository:anchore/grype:pull) -> Docker-Content-Digest header
#   2) the SHA-256 of that response body
# Both returned the digest below.
FROM ghcr.io/anchore/grype:v0.120.0@sha256:5c88961f4130e830542d441c7ed6c78baa28e799163abac53d2be4923fb5ab7d AS grype
