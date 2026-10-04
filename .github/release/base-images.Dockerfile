# Base images of the release images (#119, ADR-0017). This file is NEVER built. It exists so that
# Dependabot's docker ecosystem keeps the pins current and .github/scripts/release_images.py reads the
# two FROM lines as the -p:ContainerBaseImage values (api and bff use "aspnet", the migrator "runtime").
# Directory.Build.targets (DECISYA0006) refuses any other base: tag ends in -noble-chiseled-extra, digest present.
#
# Every base must run on x86-64-v2 (NAS Celeron J3455, no AVX; ADR-0016 trigger F6). Ubuntu 24.04 amd64
# chiseled images do. A RHEL/UBI 10 base needs x86-64-v3 and is not allowed.
#
# Pins resolved 2026-10-04 by the main session: multi-arch index digests (linux/amd64 is pulled), read
# two ways (the Docker-Content-Digest header equals the SHA-256 of the manifest body).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd AS aspnet
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:b18ef5184a6afa186bdeb51cfff41751ab9f7daec068f1419af0982a0964ba90 AS runtime
