# Rootless Podman API service: the agent sandbox's opt-in container engine for Testcontainers
# (issue #41; docs/architecture/agent-sandbox-docker-sidecar.md, G2/G4). Built by the HOST
# Docker daemon with the host's full internet access; this build does not go through the
# `egress` allow-list it will run behind (same discipline as ../Dockerfile). Build context is
# this directory only, never the repository root or `..`, so no repository file ever reaches
# the builder.
#
# Base: the Podman project's own namespace, Fedora-based, ships a non-root "podman" user
# (uid/gid 1000) with a subuid/subgid range already configured, and newuidmap/newgidmap already
# carry file capabilities (cap_setuid=ep / cap_setgid=ep) rather than the setuid bit. It also
# ships the ordinary Fedora set of setuid/setgid helpers (su, sudo, passwd, chsh, chfn, chage,
# gpasswd, newgrp, unix_chkpwd, pam_timestamp_check, mount, umount, fusermount3) -- G4 evidence:
# a first build attempt without the strip step below FAILED its own assertion and printed exactly
# this list (`docker build` runs as root with full capabilities, unlike a `docker exec` into the
# already-hardened sidecar container, which cannot even traverse every directory to find them).
# The RUN step below strips the setuid/setgid bit and any file capability from every file except
# newuidmap/newgidmap (G4-41-03), then re-asserts the invariant so a future base-image change
# that adds another one breaks the build instead of silently widening the sidecar's
# privilege-escalation surface. Losing `sudo`/`su`/`passwd`/etc. has no effect on this image:
# nothing in the sidecar's command (`podman system service`) or the pre-load path uses them, and
# `fusermount3` is not needed because native overlayfs works in the user namespace (U-41-6,
# storage.conf).
#
# renovate/dependabot: quay.io/podman/stable, pinned by digest; tracked by the docker ecosystem
# entry for /.devcontainer/engine in .github/dependabot.yml, or (if Dependabot cannot resolve
# digests for this path) a documented monthly manual bump -- docs/runbooks/agent-sandbox.md.
# Digest recorded 2026-09-24 (G4 evidence, docs/ai/pipeline/41.md).
FROM quay.io/podman/stable@sha256:301671d5ba3d9e6f8db46aaa91f10826b516170d919164908ba632d1e713288f

USER root

# --- G4-41-03: no privilege-escalation helper besides newuidmap/newgidmap ---------------------
# Pass 1: strip the setuid/setgid bit from every file except the two allowed helpers.
RUN set -eu; \
    for f in $(find / -xdev \( -perm -4000 -o -perm -2000 \) -type f 2>/dev/null); do \
      case "$f" in \
        /usr/bin/newuidmap|/usr/bin/newgidmap) ;; \
        *) chmod u-s,g-s "$f" ;; \
      esac; \
    done
# Pass 2: strip any file capability from every file except the two allowed helpers.
RUN set -eu; \
    getcap -r / 2>/dev/null > /tmp/getcap.out || true; \
    while IFS= read -r line; do \
      case "$line" in \
        "/usr/bin/newuidmap cap_setuid=ep"|"/usr/bin/newgidmap cap_setgid=ep"|"") ;; \
        *) f=$(printf '%s\n' "$line" | cut -d' ' -f1); setcap -r "$f" ;; \
      esac; \
    done < /tmp/getcap.out; \
    rm -f /tmp/getcap.out
# Pass 3: re-assert the invariant. A future base-image update that adds a new setuid/setgid file
# or file capability outside the allowed two fails the build here instead of silently widening
# the sidecar's privilege-escalation surface.
RUN set -eu; \
    bad=0; \
    for f in $(find / -xdev \( -perm -4000 -o -perm -2000 \) -type f 2>/dev/null); do \
      case "$f" in \
        /usr/bin/newuidmap|/usr/bin/newgidmap) ;; \
        *) echo "UNEXPECTED setuid/setgid file after stripping: $f" >&2; bad=1 ;; \
      esac; \
    done; \
    getcap -r / 2>/dev/null > /tmp/getcap.out || true; \
    while IFS= read -r line; do \
      case "$line" in \
        "/usr/bin/newuidmap cap_setuid=ep"|"/usr/bin/newgidmap cap_setgid=ep"|"") ;; \
        *) echo "UNEXPECTED file capability after stripping: $line" >&2; bad=1 ;; \
      esac; \
    done < /tmp/getcap.out; \
    rm -f /tmp/getcap.out; \
    [ "$bad" -eq 0 ]

# --- G4-41-07: pulls fail closed regardless of network reachability ----------------------------
# Reject every remote transport by default; only the two archive transports the host's pre-load
# uses (`podman load` of a `docker save` stream), and the local storage transport, are accepted.
# No unqualified-search-registries, so a mistyped short name never resolves through a registry
# even in the window where the engine happened to be reachable.
RUN install -d -m 0755 /etc/containers \
    && cat > /etc/containers/policy.json <<'EOF'
{
  "default": [{ "type": "reject" }],
  "transports": {
    "docker-archive": { "": [{ "type": "insecureAcceptAnything" }] },
    "oci-archive": { "": [{ "type": "insecureAcceptAnything" }] },
    "containers-storage": { "": [{ "type": "insecureAcceptAnything" }] }
  }
}
EOF
RUN cat > /etc/containers/registries.conf <<'EOF'
unqualified-search-registries = []
short-name-mode = "enforcing"
EOF

# --- Nested-container and storage settings (recorded ladder outcome, G4 evidence) --------------
COPY containers.conf /etc/containers/containers.conf
COPY storage.conf /etc/containers/storage.conf
RUN chown root:root /etc/containers/containers.conf /etc/containers/storage.conf \
        /etc/containers/policy.json /etc/containers/registries.conf \
    && chmod 0644 /etc/containers/containers.conf /etc/containers/storage.conf \
        /etc/containers/policy.json /etc/containers/registries.conf
# /home/podman/.config is on the read-only rootfs at runtime (compose.docker.yaml: `read_only:
# true`), so there is no writable per-user override of the system config above (G4-41-07).

USER podman
WORKDIR /home/podman
