# 0016. Hosting for Phase 0 (home NAS) and the private VPS fallback

- Status: Proposed
- Date: 2026-10-04
- Deciders: Marco
- Tags: hosting, security

## Context and problem statement

Phase 0 needs a deployed environment before the phase exit. Issues #119 (release images), #120 (Compose, Caddy, secrets), #121 (Keycloak hostname), #122 (rate limiting, key ring backup), #29 (deployment) and #30 (backup) all assume a target. Phase 0 has synthetic users only.

**Marco decided on 2026-10-04 that Phase 0 runs on his home NAS.** The private EU VPS from the first draft of this ADR now has two roles: (a) the go-live target, per ADR-0001 (one VPS running Compose from `aspire publish`, then K3s, then managed Kubernetes, with the Azure Container Apps review trigger in #120), and (b) the fallback for Phase 0 if the NAS is too slow (thresholds below).

The NAS (data from Marco): Synology DS918+, Intel Celeron J3455 (x86-64, 4 cores, 1.5 GHz base), 8 GB RAM, DSM 7.x with Container Manager (Docker and Compose projects). It also holds Marco's personal files and probably his backups. DSM's own web server uses ports 80/443 and 5000/5001.

The stack: Keycloak 26, Postgres 18, Redis 8 (Valkey drop-in), `Decisya.Api`, `Decisya.Bff`, the SPA (served by the BFF or by Caddy), Caddy at the edge, and an OpenTelemetry collector. ADR-0007 applies (S3-compatible storage, no managed-provider lock-in before revenue). ADR-0014 and ADR-0015 apply: every image is pinned by digest, and the release images are scanned and signed with cosign (#119).

Forces:
- **Shared box:** the stack runs next to Marco's personal files and backups. A container escape or a stolen DSM admin session reaches both. Isolation is therefore a set of MUSTs (R9), not a preference.
- **Fixed hardware:** 8 GB RAM shared with DSM and a low-power CPU. Keycloak start-up and password hashing are the CPU peaks.
- **Solo-developer time:** every moving part is Marco's to patch, rotate and restore.
- **Security:** the box will hold the deploy secrets, the realm signing key and the Data Protection key ring. Release blockers C-01, C-03, C-05, C-09, C-10 and C-20 (`docs/security/threat-models/system-baseline.md`) must be closed before this deployment.
- **Portability:** Phase 0 rehearses the production shape (ADR-0001), so moving to the VPS changes configuration, not design.
- **Cost:** no price is stated here as fact. Marco verifies current prices before choosing a provider.

## Decision drivers

- Nothing on the NAS is reachable from the internet. No router port-forward to the NAS.
- No container can see personal data, the Docker socket, or the host.
- Caddy (pinned, scanned, signed) is the only TLS terminator for the app.
- The same Compose file and Caddyfile run on the NAS and on the VPS; only variables differ.
- The NAS is never the only copy of the stack's data.
- No credential in CI that reaches the box. No secret in an image.

## Considered options

### R0. Phase 0 host

1. **Home NAS (Synology DS918+)**. Decided by Marco, 2026-10-04. No new provider account, no monthly cost, data stays at home. It shares a box with personal data (R9) and has fixed, modest hardware (R5).
2. **Private EU VPS (the first draft)**. The go-live target (ADR-0001) and the Phase 0 fallback. Switching to it when a fallback trigger fires needs no new ADR.

### R1. CPU architecture

1. **x86-64 (amd64)**. Confirmed by the NAS hardware. #119 builds `linux/amd64` only; ADR-0015 already scans `linux/amd64`.
2. ARM64 and multi-arch. Rejected: the NAS is amd64, and multi-arch doubles CI and scan cost.

Constraint: the J3455 (Goldmont) supports x86-64-v2 but not v3 (no AVX or AVX2). Every current image in the stack runs on v2. An image whose base requires x86-64-v3 (for example a future move to a RHEL 10 or UBI 10 base) will not start on the NAS. #120 checks each image starts. This is a fallback trigger (F6).

### R2. Private access to the NAS

DSM 7 has no built-in WireGuard. The kernel on this model predates in-kernel WireGuard.

1. **LAN only for Phase 0** (recommended). No remote access at all. No new service, account or open port. Marco develops and tests from home. A later need for remote access is a review trigger; the order of preference is then R2.2, then R2.3.
2. **WireGuard on the home router**, if the router supports it. The VPN ends on the router, not on the NAS. The router opens its own UDP port; nothing is forwarded to the NAS. No third party. Depends on the router model.
3. **Tailscale Synology package**. No inbound port anywhere (NAT traversal and relays), which matters more for a home box than for a VPS with a public IP. Against: a third-party control plane can add nodes (mitigate with Tailnet Lock), access depends on that account and its identity provider, and the NAS joins the tailnet as a whole, so DSM, SMB and the stack are all reachable unless the tailnet ACL allows only Marco's devices to port 8443. On DSM 7 the package runs in userspace networking by default; client source addresses seen by Caddy must be verified.
4. **DSM VPN Server (OpenVPN or L2TP/IPsec)**. Rejected: it needs a router port-forward to the NAS, which the drivers forbid, and it puts a DSM-managed service on the internet in front of a box holding personal data. L2TP/IPsec with a pre-shared key is dated.
5. **WireGuard container (userspace `wireguard-go`)**. Rejected: it needs `NET_ADMIN` and `/dev/net/tun`, which breaks the R9 capability rule, and it also needs a port-forward to the NAS.

For the VPS (fallback and go-live), the first draft's choice stands: **WireGuard self-run on the VPS**, provider firewall admits only its UDP port, SSH on `wg0` only.

### R3. Domain, DNS and certificates

No public HTTP(S) port exists, so ACME HTTP-01 and TLS-ALPN-01 cannot work.

1. **Own domain, Phase 0 names in a delegated `p0.<domain>` zone, DNS-01 through a DNS provider with a zone-scoped API token and a maintained `caddy-dns` module**. Kept, per Marco's direction. Two origins (`app.p0.<domain>`, `id.p0.<domain>`), as in production. The token on the box can only edit the `p0` zone.
2. Tailscale certificates and a private CA. Rejected as in the first draft: one name per node, or a root to trust in Firefox, Playwright's browsers and the BFF's back-channel.

Name resolution inside the LAN (the names must resolve to the NAS's LAN address):
- a. **Local DNS override on the home router** (recommended, if the router supports host overrides). No service on the NAS. Every LAN device resolves the names.
- b. **DSM DNS Server package**. Works with any router, but adds a service on the NAS, and the LAN's resolution depends on the NAS if DHCP hands it out. Use it only as the resolver for Marco's devices, with forwarding for everything else.
- c. **Public A records in the `p0` zone pointing to the private LAN address**. No local service. Publishes the LAN address, and routers with DNS-rebind protection drop such answers unless the names are exempted.
- d. **Hosts file on Marco's devices** (two or three devices). Always the fallback.

Certificate names appear in public Certificate Transparency logs. They reveal only that `app.p0` and `id.p0` exist.

### R4. Caddy build, publishing and the edge rate limit

Build: **custom Caddy built with xcaddy, carrying exactly one module, the DNS provider for R3.1**. Kept, per Marco's direction. #119 builds it from digest-pinned `caddy:<v>-builder` and `caddy:<v>` images, pins the module to an exact version, scans it with Grype and signs it with cosign. Dependabot does not bump xcaddy modules; the bump is manual, with the Caddy version. No `caddy-ratelimit` in Phase 0: C-09 is met in-app (#122); the edge is the LAN boundary plus Caddy's `remote_ip` allow-list, request-size limits and timeouts.

Publishing (DSM holds 80/443 and 5000/5001):

1. **Caddy on a non-DSM port, 8443, from a bridge network** (recommended). Caddy sets `https_port 8443`, publishes only `8443:8443`, and needs no port 80 (DNS-01). URLs carry the port (`https://app.p0.<domain>:8443`). Keycloak's hostname and the realm's redirect URIs include it. Caddy stays the only TLS terminator. The difference from production is one variable.
2. **macvlan with a dedicated LAN address, Caddy on 443**. Exact production URLs. Rejected: on macvlan the host cannot reach its own child interfaces, so traffic that arrives on the NAS host (any VPN that ends on DSM, R2.3) cannot reach Caddy without a host shim interface that DSM does not support and resets on reboot. The Compose file also gains a LAN-specific parent interface and address.
3. **DSM's reverse proxy in front**. Rejected: DSM's nginx terminates TLS with a DSM-managed certificate, so Caddy stops being the only TLS terminator, and a DSM component becomes part of the app's trust path and its forwarded-header chain.
4. **Move DSM off 80/443**. Rejected: DSM's nginx configuration is not meant to be edited, and updates revert changes.

### R5. Size

The NAS is fixed: 4 cores, 8 GB RAM, shared with DSM and its packages. The stack gets a hard budget, set with per-container limits (`mem_limit`, `cpus`, `pids_limit`), so it cannot starve DSM:

1. **Stack total about 3.5 GB of hard limits, DSM keeps at least 4 GB** (recommended; the per-container table is in the architecture note). #120 measures DSM's own use with the stack stopped and the stack's use with `docker stats`, then sets the limits. The headroom check is part of #120's Done-when.
2. For the VPS (fallback and go-live): **4 vCPU, 8 GB RAM, at least 80 GB SSD**, as in the first draft.

### R6. Image pulls

1. **Public GHCR packages, pinned by digest, `cosign verify` before every deploy**. Unchanged, per Marco's direction. No pull token on the box. `cosign verify` runs on Marco's workstation against the exact digests written into the Compose file; the NAS pulls by digest, so it gets exactly what was verified, and needs no cosign binary.
2. Private packages with a pull token. Rejected as in the first draft.

### R7. Backup

1. **Nightly logical dumps of every Postgres database (modules and Keycloak) plus a copy of the Data Protection key ring, written to a `dumps` folder in the stack's shared folder; DSM Hyper Backup, with client-side encryption, sends that folder to an S3-compatible bucket at an EU provider** (recommended, per Marco's direction).
   - The Hyper Backup task is the stack's own: its own bucket, its own bucket-scoped access key, its own encryption password. It never includes personal data, and personal backup tasks never include the stack.
   - The encryption password and Hyper Backup's key file are kept off the NAS (an offline copy).
   - Bucket versioning where the provider offers it; the access key cannot delete old versions where the provider allows that. #30 checks Hyper Backup works with the chosen bucket settings.
   - Redis is not backed up. Sessions need not survive a restore, and no backup may hold both the ticket ciphertext and the key ring.
   - Caddy's certificate storage and the Postgres data directory are not backed up; certificates are re-issued, the database is restored from dumps.
   - DSM Snapshot Replication of the stack's shared folder is an optional second, same-box layer (fast rollback, not a backup of record).
2. Snapshots only, or a local USB copy only. Rejected: same box or same house. The NAS must not be the only copy.
3. restic in a container (the first draft's tool). Viable and portable to the VPS. Not preferred on the NAS: Hyper Backup is already there and patched by Synology. On the VPS, #30 uses restic or an equivalent.

### R8. Provider and region (VPS and bucket)

Applies to the go-live VPS and the Phase 0 fallback only, plus the backup bucket.

1. **An EU-headquartered VPS provider in an EU data centre**, picked by the checklist in the architecture note (GDPR Article 28 agreement, EU location, provider firewall, snapshots, hardware-key 2FA, scoped API tokens, amd64 plans of the R5.2 size). Marco picks it when the fallback fires or at go-live. Verify current prices then.
2. Non-EU provider's EU region, or a hyperscaler VM. Not preferred, as in the first draft.

The backup bucket is at an EU provider with an S3 API that Hyper Backup supports. Once the VPS exists, the bucket provider differs from the VPS provider.

### R9. NAS isolation (MUST, implemented and checked in #120)

1. A dedicated DSM user for the stack: not in `administrators`, no application privileges (no File Station, SMB, AFP, FTP, WebDAV, Drive, or DSM login), no access to any other shared folder. It owns the stack's shared folder.
2. A dedicated shared folder (for example `decisya-p0`) for everything the stack keeps on disk outside named volumes: configuration, secret files, dumps. No other shared folder grants it access, and it grants no access to other DSM users except administrators.
3. Never mount the Docker socket into any container. No tool that needs it (Watchtower, Portainer agent, Docker-provider proxies).
4. Never mount a personal shared folder, `/volume*` roots, `/`, or any DSM system path into a container. Bind mounts come only from the dedicated shared folder.
5. No privileged container ("Execute container using high privilege" stays off). No `cap_add`, no `devices`, no `network_mode: host`, no `pid: host`. `security_opt: no-new-privileges:true` on every service; `cap_drop: [ALL]` where the image allows it; non-root users where the image supports them.
6. Only Caddy publishes a port (8443). Postgres, Redis, Keycloak (including its management port), Api, BFF and the collector are on `internal: true` networks with no published ports and no egress, unless #120 shows a service needs egress.
7. No router port-forward and no UPnP mapping to the NAS. DSM's router configuration and UPnP stay off.
8. DSM firewall on: allow DSM (HTTPS 5001 only), SMB and 8443 from the LAN subnet (and the VPN subnet when one exists), deny everything else. Because Docker's own iptables rules can bypass the DSM firewall for published ports, Caddy also enforces a `remote_ip` allow-list on both site blocks, and #120 verifies from outside the allowed range. #120 also verifies that no container can reach DSM's management ports (5000, 5001, 22).
9. DSM admin hardening: the default `admin` account disabled, 2FA on every administrator, auto-block on, SSH off unless a CLI deploy needs it (then LAN only, key authentication).
10. DSM and Container Manager kept updated: automatic installation of important DSM updates; Container Manager updated from Package Center; #120 records the versions.
11. Marco checks Synology's product support status page for the DS918+ and records the end date of security updates in #120. That date is a fallback trigger (F5).
12. Per-container memory, CPU and PID limits (R5); container logs rotated (`json-file` with `max-size` and `max-file`).

## Fallback triggers ("the NAS is too slow")

Measured first in #120 (first deployment) and again in #29 (phase-exit rehearsal), on a warm stack with synthetic users, the NAS otherwise in normal use. Crossing any one switches Phase 0 to the private VPS (R2 VPS, R5.2, R8.1) **without a new ADR**; the switch is recorded in #29 and in a dated note under "Decision outcome".

| # | Measure | Threshold that triggers the fallback |
| --- | --- | --- |
| F1 | Keycloak ready time: container start to `/health/ready` 200, median of 3 cold starts | > 180 s |
| F2 | Login round-trip: Playwright (Firefox), from "Sign in" to authenticated app page, 20 runs | p95 > 3 s |
| F3 | `GET /api/capabilities` through Caddy, authenticated, 200 requests at concurrency 5 | p95 > 500 ms |
| F4 | Memory over a 15-minute run (smoke plus idle) | host available memory < 1 GB sustained for 5 min, or swap use grows by > 256 MB, or any OOM kill of a container or DSM service |
| F5 | Synology support status | the DS918+ no longer receives DSM security updates |
| F6 | Image compatibility | a required image cannot run on the J3455 (x86-64-v2), or an R9 MUST cannot be met on DSM (for example memory limits not enforced) |

Before declaring F1 or F2, #120 may apply the cheap levers once: an optimized Keycloak start (`kc.sh build` at image build time, `start --optimized`), and a lower hashing cost for synthetic users only. A threshold crossed after one re-measure stands.

## Decision outcome

**Decided (Marco, 2026-10-04):** R0.1, Phase 0 on the home NAS; the private VPS is the go-live target and the Phase 0 fallback.

**Marco's direction recorded (2026-10-04):** R1.1 (x86-64), R3.1 (DNS-01, delegated `p0` zone), R4 build (custom Caddy, DNS module only), R6.1 (public GHCR, digest pins, `cosign verify`), R7.1 (Hyper Backup, encrypted, off-site EU bucket, Redis excluded), R9 (isolation MUSTs), and the fallback-trigger mechanism.

**Pending Marco's choices.** Recommended: R2.1 (LAN only), R3 name resolution a (router override, hosts file as fallback), R4 publishing 1 (port 8443), R5.1 (stack budget about 3.5 GB), the F1 to F4 threshold values, QuickConnect off (see the architecture note), and the DNS provider and bucket provider. R8 is picked at fallback or go-live, not now.

When Marco chooses, this section records each choice and the status becomes Accepted.

### Consequences

- Good:
  - No new provider account, server or monthly cost for Phase 0. Nothing on the NAS is reachable from the internet.
  - The same Compose file and Caddyfile serve the NAS and the VPS; the port, the addresses and the VPN layer are variables or host setup.
  - Two origins and real certificates in Phase 0, as in production.
  - Backups are encrypted before they leave the NAS, kept at an EU provider, and never contain Redis or personal data.
- Bad:
  - The stack shares a box, a kernel and a DSM admin account with Marco's personal data. A container escape or a DSM compromise reaches both. R9 reduces this; it does not remove it. The DSM kernel is older than a current distribution kernel.
  - URLs carry `:8443` in Phase 0, so Keycloak and realm settings differ from production in the port.
  - No remote access in Phase 0 (if R2.1).
  - Fixed hardware: if the fallback triggers fire, Phase 0 moves to the VPS late in the phase.
  - #119 owns a custom Caddy build whose module Dependabot does not bump. A DNS API token lives on the NAS, limited to the `p0` zone.
  - The DSM firewall may not filter Docker-published ports, so the port-8443 boundary rests on the LAN boundary and Caddy's `remote_ip` allow-list.
- Review triggers:
  - Any fallback trigger F1 to F6.
  - The first need for remote access: revisit R2 (router WireGuard, then Tailscale).
  - The first deployment reachable from the public internet (go-live VPS): revisit `caddy-ratelimit` (R4), HTTP-01 (R3), physical backups with point-in-time recovery (R7), and the port (443).
  - Any image moving to an x86-64-v3 base.
- Enforced by (to be implemented in the issues named): the release workflow's digest, scan and cosign steps (#119); guard tests over the Compose file and Caddyfile for digest pins, R9's container rules, published ports and memory limits (#120); the firewall, reachability and headroom checks and the F1 to F4 measurements (#120, #29); the restore drill (#30).
