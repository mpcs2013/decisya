# Base images of the release images (#119, ADR-0017). This file is NEVER built. It exists so that
# Dependabot's docker ecosystem keeps the pins current and .github/scripts/release_images.py reads the
# two FROM lines as the -p:ContainerBaseImage values (api and bff use "aspnet", the migrator "runtime").
# Directory.Build.targets (DECISYA0006) refuses any other base: tag ends in -noble-chiseled-extra, digest present.
#
# Every base must run on x86-64-v2 (NAS Celeron J3455, no AVX; ADR-0016 trigger F6). Ubuntu 24.04 amd64
# chiseled images do. A RHEL/UBI 10 base needs x86-64-v3 and is not allowed.
#
# Pins resolved 2026-11-18 (#320) by the main session: the .NET 10.0.15 builds (DOTNET_VERSION/ASPNET_VERSION
# 10.0.15, created 2026-11-10), which fix GHSA-67wp-ghc2-fw2g, GHSA-wr8v-9x5q-ggcm and GHSA-x33c-w48q-qrgc in the
# 10.0.13 aspnet base. Multi-arch index digests (linux/amd64 is pulled), read two ways (the registry
# Docker-Content-Digest header and the MCR catalog API), each for both the 10.0.15-noble-chiseled-extra tag and the
# floating 10.0-noble-chiseled-extra tag, with equal digests; cooldown ended 2026-11-17, no waiver. Previous pins: #166 (10.0.13).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b AS aspnet
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:b64d614c3c1c440abe6d57f510c5a4cfc0ccd56a9b96f96c1503efe4a7ac3e56 AS runtime
