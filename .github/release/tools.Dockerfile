# Release tools (#119, ADR-0017). This file is NEVER built. Each FROM line is one tool image that
# .github/scripts/release_images.py runs with `docker run`, by this exact tag@digest. Dependabot bumps them.
# Aliases are exact: syft, cosign, gitleaks. Anything else fails closed.
#
# Pins resolved 2026-10-04 by the main session: latest release past the 7-day cooldown (syft v1.52.0 of
# 2026-09-17, cosign v3.1.3 of 2026-08-06), index digests read two ways (header == SHA-256 of the body).
# gitleaks must equal GITLEAKS_VERSION in ci.yml and the pre-commit `# frozen:` version (test_gitleaks_parity.py).
FROM ghcr.io/anchore/syft:v1.52.0@sha256:500e2d872ac019436926e8322b4fc1f39441d94d21f6f4046c6ff29b30e8cb02 AS syft
FROM ghcr.io/sigstore/cosign/cosign:v3.1.3@sha256:9e5c2f2edc34351160407ca3416c61855bdf9403c3c5936e0f0be7fc261611b8 AS cosign
FROM ghcr.io/gitleaks/gitleaks:v8.30.1@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f AS gitleaks
