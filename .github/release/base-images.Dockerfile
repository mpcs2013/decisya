# Base images of the release images (#119, ADR-0017). This file is NEVER built. It exists so that
# Dependabot's docker ecosystem keeps the pins current and .github/scripts/release_images.py reads the
# two FROM lines as the -p:ContainerBaseImage values (api and bff use "aspnet", the migrator "runtime").
# Directory.Build.targets (DECISYA0006) refuses any other base: tag ends in -noble-chiseled-extra, digest present.
#
# Every base must run on x86-64-v2 (NAS Celeron J3455, no AVX; ADR-0016 trigger F6). Ubuntu 24.04 amd64
# chiseled images do. A RHEL/UBI 10 base needs x86-64-v3 and is not allowed.
#
# Pins resolved 2026-10-17 (#166) by the main session: multi-arch index digests (linux/amd64 is pulled), read
# two ways (the Docker-Content-Digest header equals the SHA-256 of the manifest body).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374 AS aspnet
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:11f4c921d75b7a1f19d4eb5b7069f9e92a9d1b8412a04b3475a664b62a9b88cb AS runtime
