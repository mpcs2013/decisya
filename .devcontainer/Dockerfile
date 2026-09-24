# Agent sandbox workspace image (ADR-0010, issue #36; docs/architecture/agent-sandbox.md).
# Built by the HOST Docker daemon with the host's full internet access; this build does not go
# through the `egress` allow-list it will run behind. Build context is this directory only
# (never `..`), so no repository file - and in particular no .env - ever reaches the builder.
#
# Deviation from the architecture note (recorded here and in the G4 report, per its own rule
# that deviations must be justified): the note names `mcr.microsoft.com/devcontainers/dotnet`
# as the base image. At G4 that tag (`1-10.0`) still ships SDK 10.0.100-rc.2.25502.107, a
# prerelease that does not satisfy this repo's global.json ("version": "10.0.100",
# "rollForward": "latestFeature", which excludes prerelease bands). `mcr.microsoft.com/dotnet/sdk`
# is the same vendor's (Microsoft's) own namespace, ships the GA SDK, and is otherwise
# equivalent (Ubuntu 24.04, an existing non-root "ubuntu" UID/GID 1000 that this file replaces
# with "vscode"). If the devcontainers image catches up, switching back is a one-line change.
#
# renovate/dependabot: mcr.microsoft.com/dotnet/sdk, pinned by digest; tracked by the docker
# ecosystem entry for /.devcontainer in .github/dependabot.yml (G4-15).
FROM mcr.microsoft.com/dotnet/sdk@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29

ARG NODE_VERSION=26.10.0
# Recorded from https://nodejs.org/dist/v26.10.0/SHASUMS256.txt at pin time (2026-09-23). This
# is the primary integrity check: the build fails if the downloaded tarball's own sha256 does
# not match this value, independently of whether nodejs.org's TLS or the registry is trusted at
# build time. Full GPG signature-chain verification (SHASUMS256.txt.sig against the Node.js
# release team's keys) is not implemented here: hardcoding currently-valid signing key
# fingerprints without a tool to verify them live would risk pinning stale or wrong keys, which
# is worse than the honest deviation recorded in the G4 report. Follow-up: add
# `gpg --verify SHASUMS256.txt.sig SHASUMS256.txt` against a committed keyring if one is added.
ARG NODE_SHA256=cb5c9ce9c80d7b8821e3a258543c71b939138cf17c74d5cc44bbe85d6dbc5ad8

ENV DEBIAN_FRONTEND=noninteractive

# --- Non-root user (G4-17: `user: vscode` in compose.yaml) -----------------------------------
# The base image ships a non-root "ubuntu" user at uid/gid 1000; replace it with "vscode" at
# the same uid/gid so files created by either name resolve the same way on the bind mount.
RUN userdel -r ubuntu 2>/dev/null || true \
    && groupadd -g 1000 vscode \
    && useradd -m -u 1000 -g 1000 -s /bin/bash vscode

# --- OS packages --------------------------------------------------------------------------
# python-is-python3: both hooks (.claude/hooks/*.py) and .devcontainer/*.py need `python` on
# PATH, or they fail open (cc:T-06). git: already present in the base image; kept explicit so
# a base-image change that drops it is caught by CI. No `gh`, no docker CLI, no `sudo`
# (confirmed absent from this base; the RUN below is a no-op guard against a future change).
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python-is-python3 git ca-certificates \
    && apt-get purge -y --auto-remove sudo 2>/dev/null || true \
    && rm -rf /var/lib/apt/lists/*

# --- Node (.node-version pins the major; Claude Code needs a JS runtime) ---------------------
RUN curl -fsSL -o /tmp/node.tar.gz "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.gz" \
    && echo "${NODE_SHA256}  /tmp/node.tar.gz" | sha256sum -c - \
    && tar -xzf /tmp/node.tar.gz -C /usr/local --strip-components=1 \
    && rm /tmp/node.tar.gz \
    && node --version && npm --version

# --- Claude Code, at an exact version with integrity checked (G4-15) -------------------------
# .devcontainer/claude/package.json pins "@anthropic-ai/claude-code" to one version; its
# committed package-lock.json pins every transitive package (including the linux-x64 platform
# binary) by an npm integrity (sha512) hash. `npm ci` refuses to install anything that does not
# match the lockfile exactly.
COPY claude/package.json claude/package-lock.json /opt/claude-code/
RUN cd /opt/claude-code && npm ci --omit=dev \
    && ln -s /opt/claude-code/node_modules/.bin/claude /usr/local/bin/claude \
    && chmod -R a+rX /opt/claude-code \
    && claude --version

# --- Managed settings (G4-13, U-7, U-23): baked in, not in the project or user settings, so
# neither a project change nor a user-level write from inside the sandbox can override these
# keys. root:root 0644 - readable, not writable, by "vscode".
#
# Deliberately NOT set here: "forceLoginMethod": "console". The architecture note's original
# design (docs/architecture/agent-sandbox.md, "Claude credential") set this expecting it to mean
# "no claude.ai subscription login" and to be compatible with an API key. Marco's actual run of
# Claude Code 2.1.273 with DECISYA_SANDBOX_ANTHROPIC_API_KEY set showed the opposite: this
# version reads "console" as "this machine is pinned to an interactive OAuth Console login", and
# refuses to start with a non-OAuth Anthropic credential ("A non-OAuth Anthropic credential
# cannot satisfy the org pin"). That directly contradicts G4-11 (Marco's decision: a dedicated,
# spend-capped API key, never an interactive login). U-23 is disproven for this key at this
# version. Removing the key does not reopen a claude.ai subscription login as a practical path
# in this sandbox: no login host (claude.ai, console.anthropic.com) is on the egress allow-list
# (docs/architecture/agent-sandbox.md, "Removed from #36 or deliberately excluded"), so an
# interactive OAuth flow has nowhere to complete from inside `workspace` regardless of this
# setting. Revisit if a future Claude Code version reports a compatible meaning for this key
# (re-test with the exact pinned version before re-adding it).
COPY managed-settings.json /etc/claude-code/managed-settings.json
RUN chown root:root /etc/claude-code/managed-settings.json && chmod 0644 /etc/claude-code/managed-settings.json

# --- Git: no host hooks, and the bind mount's ownership never trips safe.directory -----------
RUN git config --system safe.directory /workspaces/decisya \
    && git config --system credential.helper "" \
    && git config --system push.default nothing

# --- Home layout baked into the image (copied into decisya-sandbox-home on its first use;
# G4-13, U-30). /home/vscode and ~/.claude are sticky (mode 1775, group "vscode"): vscode can
# create new entries but cannot rename or replace the root-owned ones already here, mirroring
# how /tmp works. A `sandbox.py reset` removes the volume, so this layout cannot drift for good.
RUN install -d -m 1775 -o root -g vscode /home/vscode \
    && install -d -m 1775 -o root -g vscode /home/vscode/.claude \
    && install -d -m 0755 -o root -g vscode /home/vscode/.claude/agents \
    && install -d -m 0755 -o root -g vscode /home/vscode/.claude/skills \
    && install -d -m 0755 -o root -g vscode /home/vscode/.claude/commands \
    && printf '{}\n' > /home/vscode/.claude/settings.json \
    && : > /home/vscode/.claude/CLAUDE.md \
    && install -d -m 1775 -o root -g vscode /home/vscode/.nuget \
    && install -d -m 0755 -o root -g vscode /home/vscode/.nuget/NuGet \
    && cat > /home/vscode/.nuget/NuGet/NuGet.Config <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF
RUN cat > /home/vscode/.npmrc <<'EOF'
fund=false
audit=false
EOF
RUN cat > /home/vscode/.gitconfig <<'EOF'
[safe]
	directory = /workspaces/decisya
[credential]
	helper =
EOF
# Deliberately minimal: PATH additions live in the image ENV below (applies to every process
# regardless of shell or how it is invoked), not here. /etc/profile's own logic sources
# /etc/bash.bashrc for some non-interactive shells depending on $BASH and $PS1, which is a
# base-image behaviour outside this file's control; keeping these two nearly empty avoids
# fighting it.
RUN cat > /home/vscode/.bashrc <<'EOF'
# Sandbox-managed (ADR-0010, issue #36); root-owned, see the "Home layout" note in
# docs/architecture/agent-sandbox.md. PATH is set via the image's ENV, not here.
EOF
RUN cp /home/vscode/.bashrc /home/vscode/.profile
RUN true \
    && chown root:vscode /home/vscode/.claude/settings.json /home/vscode/.claude/CLAUDE.md \
       /home/vscode/.nuget/NuGet/NuGet.Config /home/vscode/.npmrc /home/vscode/.gitconfig \
       /home/vscode/.bashrc /home/vscode/.profile \
    && chmod 0644 /home/vscode/.claude/settings.json /home/vscode/.claude/CLAUDE.md \
       /home/vscode/.nuget/NuGet/NuGet.Config /home/vscode/.npmrc /home/vscode/.gitconfig \
       /home/vscode/.bashrc /home/vscode/.profile
# ~/.claude.json, ~/.claude/projects, ~/.nuget/packages, ~/.decisya-build, ~/.dotnet,
# ~/.microsoft are deliberately NOT created here: they are vscode-owned and created at runtime
# by Claude Code, NuGet, dotnet and this repo's Directory.Build.props (G4-05) respectively.

# Environment (non-secret only). GIT_TERMINAL_PROMPT and the telemetry opt-outs apply to every
# tool that reads them; the proxy variables point at the `egress` service by its compose DNS
# name. SSH_AUTH_SOCK, BROWSER, GIT_ASKPASS and VSCODE_* are never set anywhere in this image
# or in compose.yaml, so they are simply absent in the container (G4-07), not "unset".
ENV DECISYA_SANDBOX=true \
    PATH="/home/vscode/.dotnet/tools:${PATH}" \
    HTTPS_PROXY=http://egress:3128 \
    HTTP_PROXY=http://egress:3128 \
    https_proxy=http://egress:3128 \
    http_proxy=http://egress:3128 \
    NO_PROXY=localhost,127.0.0.1,egress \
    no_proxy=localhost,127.0.0.1,egress \
    MSBUILDDISABLENODEREUSE=1 \
    DOTNET_CLI_USE_MSBUILD_SERVER=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_CERT_REVOCATION_MODE=online \
    TESTINGPLATFORM_TELEMETRY_OPTOUT=1 \
    GIT_OPTIONAL_LOCKS=0 \
    GIT_TERMINAL_PROMPT=0 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    DISABLE_AUTOUPDATER=1 \
    DISABLE_TELEMETRY=1 \
    DISABLE_ERROR_REPORTING=1

WORKDIR /workspaces/decisya
USER vscode
