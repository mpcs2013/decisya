# Egress allow-list proxy for the agent sandbox (ADR-0010, issue #36).
# Built by the HOST Docker daemon with the host's full internet access; this build does not
# go through the `egress` allow-list it produces (docs/architecture/agent-sandbox.md,
# "Supply chain"). Build context is this directory only (never `..`), so no repository file -
# and in particular no .env - ever reaches the builder.
#
# renovate/dependabot: debian, pinned by digest; tracked by the docker ecosystem entry for
# /.devcontainer/egress in .github/dependabot.yml (G4-15).
FROM debian@sha256:9cc080028c43b27d2074d63a5f9caf7166d731494965616c1a6d2827a004585c

# Debian ships two builds of squid from the same source package: `squid` (linked against
# GnuTLS) and `squid-openssl` (linked against OpenSSL). Only the OpenSSL build supports
# `ssl_bump peek`/`splice` with the `ssl::server_name` ACL that this proxy's TLS-SNI check
# needs (docs/architecture/agent-sandbox.md U-22; verified at G4 with `squid -v`, which must
# report `--with-openssl` in its configure options).
RUN apt-get update \
    && apt-get install -y --no-install-recommends squid-openssl openssl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /etc/squid/ssl /var/log/squid \
    # Throw-away, self-signed, 10-year certificate. `cert=` is mandatory on an ssl-bump
    # http_port even though nothing is ever decrypted here: every allowed CONNECT is spliced
    # (ssl_bump splice) and everything else is terminated at the ClientHello (ssl_bump
    # terminate). This certificate is never used to sign anything and is never presented to a
    # client.
    && openssl req -x509 -nodes -newkey rsa:2048 -days 3650 \
         -keyout /etc/squid/ssl/dummy.pem -out /etc/squid/ssl/dummy.pem \
         -subj "/CN=decisya-sandbox-egress-unused-cert" \
    && chown -R proxy:proxy /etc/squid/ssl /var/log/squid \
    && chmod 600 /etc/squid/ssl/dummy.pem

COPY squid.conf /etc/squid/squid.conf
COPY allowlist-tls.txt /etc/squid/allowlist-tls.txt
COPY allowlist-http.txt /etc/squid/allowlist-http.txt

# Non-root, matching compose.yaml's `user: proxy` (G4-16). Port 3128 needs no capability.
USER proxy
ENTRYPOINT ["/usr/sbin/squid", "-N", "-d", "1", "-f", "/etc/squid/squid.conf"]
