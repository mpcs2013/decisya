# 0016. Hosting for Phase 0 (home NAS) and the private VPS fallback

- Status: Accepted
- Date: 2026-10-05 (drafted 2026-10-04)
- Deciders: Marco
- Tags: hosting, security

## Context and problem statement

Phase 0 needs a deployed environment before the phase exit. Issues #119 (release images), #120 (Compose, Caddy, secrets), #121 (Keycloak hostname), #122 (rate limiting, key ring backup), #29 (deployment) and #30 (backup) all assume a target. Phase 0 has synthetic users only.

**Marco decided on 2026-10-04 that Phase 0 runs on his home NAS.** The private EU VPS from the first draft of this ADR has two roles: (a) the go-live target, per ADR-0001 (one VPS running Compose from `aspire publish`, then K3s, then managed Kubernetes, with the Azure Container Apps review trigger in #120), and (b) the fallback for Phase 0 if the NAS is too slow (F1 to F6 below).

The NAS (data from Marco): Synology DS918+, Intel Celeron J3455 (x86-64, 4 cores, 1.5 GHz base), 8 GB RAM, DSM 7.x with Container Manager (Docker and Compose projects). It also holds Marco's personal files and backups. DSM's own web server uses ports 80/443 and 5000/5001. At rest it already has 1844 MB of swap in use (Marco's `free -m`, 2026-10-04).

The stack: Keycloak 26, Postgres 18, Redis 8 (Valkey drop-in), `Decisya.Api`, `Decisya.Bff` (serving the SPA), the migrator, Caddy at the edge, and an OpenTelemetry collector. ADR-0007 applies (S3-compatible storage, no managed-provider lock-in before revenue). ADR-0014, ADR-0015 and ADR-0017 apply: every image is pinned by digest; the release images are scanned, signed with keyless cosign and published by the release workflow (#119, `v0.1.0` released 2026-10-04).

Forces:
- **Shared box:** the stack runs next to Marco's personal files and backups. A container escape or a stolen DSM admin session reaches both. Isolation is therefore a set of MUSTs (R9), not a preference.
- **Fixed hardware:** 8 GB RAM shared with DSM, a low-power CPU, swap already in use. Keycloak start-up and password hashing are the CPU peaks.
- **Solo-developer time:** every moving part is Marco's to patch, rotate and restore.
- **Security:** the box will hold the deploy secrets, the realm signing key, the Data Protection key ring and the Caddy internal CA's root key. Release blockers C-01, C-03, C-05, C-09, C-10 and C-20 (`docs/security/threat-models/system-baseline.md`) must be closed before this deployment.
- **Portability:** Phase 0 rehearses the production shape (ADR-0001), so moving to the VPS changes configuration, not design.
- **Cost:** no price is stated here as fact. Marco verifies current prices before choosing a provider.

## Decision drivers

- Nothing on the NAS stack is reachable from the internet. No router port-forward to the NAS.
- No container can see personal data, the Docker socket, or the host.
- Caddy (stock image, pinned by digest, scanned once #120 adds it to the ADR-0015 targets) is the only TLS terminator for the app.
- The same Compose file and Caddyfile run on the NAS and on the VPS; only variables (names, port, TLS issuer) differ.
- The NAS is never the only copy of the stack's data.
- No credential in CI that reaches the box. No secret in an image.

## Considered options

### R0. Phase 0 host

1. **Home NAS (Synology DS918+)**. Chosen. No new provider account, no monthly cost, data stays at home. It shares a box with personal data (R9) and has fixed, modest hardware (R5).
2. **Private EU VPS (the first draft)**. The go-live target (ADR-0001) and the Phase 0 fallback. Switching to it when a fallback trigger fires needs no new ADR.

### R1. CPU architecture

1. **x86-64 (amd64)**. Chosen; #119 builds `linux/amd64` only, and ADR-0015 scans `linux/amd64`.
2. ARM64 and multi-arch. Rejected: the NAS is amd64, and multi-arch doubles CI and scan cost.

Constraint: the J3455 (Goldmont) is x86-64-v2, without AVX or AVX2. Every current image in the stack runs on v2. A base that requires x86-64-v3, such as RHEL 10 or UBI 10, is excluded for Phase 0; an image moving to one is fallback trigger F6.

### R2. Private access to the NAS

DSM 7 has no built-in WireGuard; the kernel on this model predates in-kernel WireGuard.

1. **LAN only for Phase 0**. Chosen. No remote access to the stack, no new service, account or open port.
2. WireGuard on the home router, if the router supports it. The first choice when remote access is needed: the VPN ends on the router, nothing is forwarded to the NAS, no third party.
3. A mesh-VPN package on the NAS. Second choice for later: no inbound port, but a third-party control plane, and the whole NAS joins the network unless an ACL limits peers to port 8443; client source addresses seen by Caddy must be verified.
4. DSM VPN Server (OpenVPN or L2TP/IPsec). Rejected: it needs a port-forward to the NAS and puts a DSM service on the internet next to personal data.
5. WireGuard container (userspace). Rejected: it needs `NET_ADMIN` and `/dev/net/tun` (against R9) and a port-forward to the NAS.

**QuickConnect is disabled** (Marco, 2026-10-05; this replaces the 2026-10-04 "QuickConnect stays on"). It would relay DSM to the internet without a port-forward, and DSM control equals control of Container Manager and so of the stack. With it off, DSM is reachable from the LAN only (R9.13).

For the VPS (fallback and go-live), the first draft's choice stands: WireGuard self-run on the VPS, provider firewall admits only its UDP port, SSH on `wg0` only.

### R3. Names and certificates

No public HTTP(S) port exists, so ACME HTTP-01 and TLS-ALPN-01 cannot work.

1. **LAN names under `home.arpa` (RFC 8375) and Caddy's internal CA**. Chosen (Marco, 2026-10-05).
   - `nas.home.arpa` → the NAS's LAN 1 address, for DSM (its own name, not in the app path).
   - `app.p0.home.arpa` and `id.p0.home.arpa` → the same address, for the stack (Caddy on 8443). App and Keycloak keep separate origins, as in production.
   - `.home.arpa` is special-use: no public CA issues certificates for it, and DNS-01 is not possible. Caddy's internal CA (`tls internal`) issues both site certificates. No domain, no DNS API token on the NAS, no Certificate Transparency entries.
   - Trust: the Caddy root is imported only into a dedicated Phase 0 Firefox profile (not Marco's everyday profile) and Playwright's browsers; no enterprise-roots setting. The OS store only where nothing else works (for example Playwright Chromium on Windows), recorded in #120. The root is removed from every store, and Caddy's PKI data deleted so a new root is generated, at the Phase 0 exit, on the move to the VPS, and on any suspected compromise of the NAS or DSM. A name-constrained CA is a SHOULD for #120. The BFF and Api containers trust the same root for the OIDC back channel and JWKS fetch at `https://id.p0.home.arpa:8443`. Trust is added by mounting the public root certificate read-only; certificate validation is never disabled.
   - Name resolution: a router host override if the router supports it, else hosts entries on Marco's devices; #120 records which and confirms it in Firefox (with DoH) and Playwright. Inside Compose, the backends reach `id.p0.home.arpa` through a network alias on Caddy, set only on the dedicated back-channel network (R9.6).
2. **Own domain, delegated `p0.<domain>` zone, DNS-01 with a zone-scoped token and a custom xcaddy build carrying one DNS module** (the 2026-10-04 direction). Superseded for Phase 0 by R3.1: it needs a domain, a DNS token on the NAS and a custom image Dependabot does not bump. **Kept for the go-live VPS**, where a public domain arrives (DNS-01, or HTTP-01 once a public port exists).
3. Mesh-VPN-issued certificates. Rejected: one name per node, so no separate app and identity origins.

### R4. Caddy image, publishing and the edge rate limit

Image: **the stock Caddy image, pinned by digest** (ADR-0014). ADR-0015 scans only `postgres`, `keycloak` and `redis` today; #120 adds Caddy and the OTel collector to the scan targets, with a guard that every digest in the deploy Compose file is scanned. No custom xcaddy build and no DNS module in Phase 0 (this replaces the 2026-10-04 build direction; the custom build returns only if the go-live VPS needs a DNS module). No `caddy-ratelimit`: C-09 is met in-app (#122); the edge is the LAN boundary plus Caddy's `remote_ip` allow-list, request-size limits and timeouts.

Publishing (DSM holds 80/443 and 5000/5001):

1. **Caddy on 8443 from a bridge network**. Chosen. `https_port 8443`, published only as `<LAN 1 address>:8443:8443`, no HTTP listener. URLs carry the port (`https://app.p0.home.arpa:8443`); Keycloak's hostname and the realm's redirect URIs include it.
2. macvlan with a dedicated LAN address, Caddy on 443. Rejected: the host cannot reach macvlan children without a shim DSM does not support, and the Compose file gains LAN-specific settings.
3. DSM's reverse proxy in front. Rejected: DSM's nginx would terminate TLS, so Caddy stops being the only terminator.
4. Move DSM off 80/443. Rejected: unsupported edits that DSM updates revert.

### R5. Size

1. **Stack total about 3.5 GB of hard limits, DSM keeps at least 4 GB**. Chosen. Per-container `mem_limit`, `cpus` and `pids_limit` (table in the architecture note), set from #120's measurement; the headroom check is part of #120's Done-when.
2. For the VPS (fallback and go-live): 4 vCPU, 8 GB RAM, at least 80 GB SSD, as in the first draft.

### R6. Image pulls

1. **Public GHCR packages, pulled by digest, `cosign verify` before every deploy**. Chosen. Each release carries `VERIFY.txt` with the exact `cosign verify` commands and `images.txt` with the digests (#119, ADR-0017; `v0.1.0` released 2026-10-04). Marco runs the commands on his workstation against the digests written into the Compose file. The NAS pulls by digest, so it runs exactly what was verified; it needs no pull token and no cosign binary.
2. Private packages with a pull token. Rejected as in the first draft.

### R7. Backup

1. **Nightly logical dumps of every Postgres database (modules and Keycloak) plus a copy of the Data Protection key ring into a `dumps` folder in the stack's shared folder; DSM Hyper Backup, client-side encrypted, sends that folder to an S3-compatible bucket at an EU provider**. Chosen; the bucket provider is chosen in #30.
   - The task is the stack's own: its own bucket, bucket-scoped access key and encryption password. It never includes personal data; personal backup tasks never include the stack.
   - The encryption password and Hyper Backup's key file are kept off the NAS.
   - Bucket versioning where offered; the access key cannot delete old versions where the provider allows that.
   - Not backed up: Redis (sessions need not survive a restore, and no backup holds both ticket ciphertext and the key ring), the Postgres data directory, and Caddy's data volume. Losing Caddy's data means a new internal root that Marco re-trusts on his devices and in the containers; this keeps the root key out of off-site archives.
   - DSM Snapshot Replication of the stack's folder is an optional same-box layer, not a backup of record.
2. Snapshots only, or a local USB copy only. Rejected: same box or same house.
3. restic in a container. Not preferred on the NAS (Hyper Backup is there and patched by the vendor); on the VPS, #30 uses restic or an equivalent.

### R8. Provider and region (VPS and bucket)

1. **An EU-headquartered VPS provider in an EU data centre**, picked by the checklist in the architecture note when a fallback trigger fires or at go-live. Verify current prices then.
2. A non-EU provider's EU region, or a hyperscaler VM. Not preferred.

The backup bucket is at an EU provider with an S3 API that Hyper Backup supports (#30). Once the VPS exists, the bucket provider differs from the VPS provider.

### R9. NAS isolation (MUST, implemented and checked in #120)

1. A dedicated DSM user for the stack: not in `administrators`, no application privileges, no access to any other shared folder. It owns the stack's shared folder.
2. A dedicated shared folder (for example `decisya-p0`) for configuration, secret files, the Caddy root certificate (public part) and dumps. Readable only by the stack user and administrators: no other account, no guest access, no sharing links, hidden from network browsing.
3. Never mount the Docker socket into any container; no tool that needs it.
4. Never mount a personal shared folder, a `/volume*` root, `/`, or a DSM system path. Bind mounts come only from the dedicated shared folder.
5. No privileged container, no `cap_add`, no `devices`, no `network_mode: host`, no `pid: host`. `no-new-privileges:true` on every service; `cap_drop: [ALL]` where the image allows it; non-root users where supported.
6. Only Caddy publishes a port, as `<LAN 1 address>:8443:8443`. Every other service is on `internal: true` networks with no published ports and no egress, unless #120 shows a need. A dedicated `internal: true` back-channel network with a fixed subnet holds only Caddy, the BFF, the Api and Keycloak; Caddy's `id.p0.home.arpa` alias exists only there.
7. No router port-forward and no UPnP mapping to the NAS.
8. DSM firewall on: DSM (HTTPS 5001), SMB, SSH (when on), 8443 and the other project's backup receiving service from the LAN subnet only; deny the rest. Docker's iptables rules can bypass the DSM firewall for published ports, so Caddy also enforces `remote_ip` allow-lists: the LAN subnet on both site blocks; the back-channel subnet only on the `id.p0` block and only for `/realms/<app realm>/*`; `/admin/*` and `/realms/master/*` only from Marco's workstation address (DHCP reservation); Keycloak's port 9000 never routed. If #120 finds that published ports do not keep LAN source addresses, the allow-list is never widened to the bridge subnet: the admin paths stay unrouted and #120 records it. #120 verifies from outside the range and that no container reaches DSM's ports 5000, 5001 and 22.
9. DSM hardening: default `admin` disabled, 2FA on every DSM account, auto-block on. **SSH for `mpcs_nas`: key authentication only, LAN only through the DSM firewall, off when not in use.**
10. DSM and Container Manager kept updated; #120 records the versions. After every DSM update, confirm that a password SSH login is refused (key-only is an `sshd` setting that updates can revert).
11. Marco records the end date of DS918+ security updates in #120 (fallback trigger F5).
12. Per-container memory, CPU and PID limits (R5); container logs rotated.
13. QuickConnect stays disabled (Marco, 2026-10-05). #120 checks it in DSM: *Control Panel → External Access → QuickConnect*, "Enable QuickConnect" unticked.
14. The other project that uses the NAS as a backup target stays separate: its own shared folders, accounts and backup tasks, with no access to `decisya-p0`; its backup receiving service is allowed in the DSM firewall from the LAN only (R9.8).

## Fallback triggers ("the NAS is too slow")

Measured in #120 (first deployment) and again in #29 (phase-exit rehearsal), on a warm stack with synthetic users and the NAS otherwise in normal use. Crossing any one switches Phase 0 to the private VPS (R2 VPS, R5.2, R8.1) **without a new ADR**; the switch is recorded in #29 and as a dated note under "Decision outcome".

| # | Measure | Threshold that triggers the fallback |
| --- | --- | --- |
| F1 | Keycloak ready time: container start to `/health/ready` 200, median of 3 cold starts | > 180 s |
| F2 | Login round-trip: Playwright (Firefox), "Sign in" to authenticated app page, 20 runs | p95 > 3 s |
| F3 | `GET /api/capabilities` through Caddy, authenticated, 200 requests at concurrency 5 | p95 > 500 ms |
| F4 | Memory over a 15-minute run (smoke plus idle) | host available memory < 1 GB sustained for 5 min; or swap in use more than 256 MB above the at-rest swap measured and recorded at the start of each run (1844 MB on 2026-10-04, for context); or any OOM kill of a container or DSM service |
| F5 | Vendor support status | the DS918+ no longer receives DSM security updates |
| F6 | Image compatibility | a required image cannot run on the J3455 (x86-64-v2), for example a move to a UBI 10 or RHEL 10 base; or an R9 MUST cannot be met on DSM |

Before declaring F1 or F2, #120 may apply the cheap levers once: an optimized Keycloak start (`kc.sh build`, `start --optimized`) and a lower hashing cost for synthetic users only. A threshold crossed after one re-measure stands.

## Decision outcome

**Accepted (Marco, 2026-10-05).**

- R0.1: Phase 0 on the DS918+; the drafted private VPS is the go-live target and the fallback (F1 to F6).
- R1.1: x86-64; x86-64-v2 only, so UBI 10 / RHEL 10 bases are excluded (F6).
- R2.1: LAN only, no VPN, no port-forward. QuickConnect disabled (Marco, 2026-10-05; R9.13).
- R3.1: `nas.home.arpa` (DSM), `app.p0.home.arpa` and `id.p0.home.arpa` (Decisya), all to the NAS's LAN 1 address; Caddy's internal CA; a dedicated Phase 0 Firefox profile, Playwright's browsers and the BFF and Api containers trust its root, removed and regenerated at the Phase 0 exit, on the move to the VPS and on suspected compromise. R3.2 is kept for the go-live VPS.
- R4: stock, digest-pinned Caddy, added to the scan targets in #120; no custom build, no DNS module, no rate-limit module; Caddy on `<LAN 1 address>:8443` from a bridge network.
- R5.1: about 3.5 GB of stack memory limits; DSM keeps at least 4 GB.
- R6.1: public GHCR images pulled by digest, verified with `cosign verify` from each release's `VERIFY.txt`; no pull token and no cosign on the NAS.
- R7.1: dumps plus key ring copy, Hyper Backup encrypted to an EU S3-compatible bucket; the bucket provider is chosen in #30.
- R8: picked when a fallback trigger fires or at go-live.
- R9 in full, with SSH for `mpcs_nas` key-only, LAN-only and off when unused.
- Fallback thresholds F1 to F6 as drafted; F4's swap growth measured from the at-rest swap at the start of each run.

### Consequences

- Good:
  - No new provider account, server, domain or monthly cost for Phase 0. Nothing in the stack is reachable from the internet.
  - No DNS API token on the NAS and no custom Caddy image to maintain; Dependabot bumps the stock Caddy digest.
  - The same Compose file and Caddyfile serve the NAS and the VPS; the names, the port and the TLS issuer are variables.
  - Two origins in Phase 0, as in production.
  - Backups are encrypted before they leave the NAS and never contain Redis, personal data or the CA root key.
- Bad:
  - The stack shares a box, a kernel and DSM admin accounts with Marco's personal data. R9 reduces this; it does not remove it.
  - No remote access to DSM while QuickConnect is off.
  - The Caddy root key lives in Caddy's data volume on the NAS. Any browser that trusts the root trusts every certificate it signs, for any name, so trust is limited to a dedicated Firefox profile and Playwright, and the root is regenerated at the Phase 0 exit; only Caddy mounts its data volume.
  - Trust must be distributed by hand (Firefox profile, Playwright, BFF, Api) and redone if Caddy's data is lost or the root is regenerated.
  - Phase 0 differs from production in names, port (`:8443`) and certificate issuer; Keycloak and realm settings carry the differences as variables.
  - No remote access to the stack in Phase 0.
  - Fixed hardware with swap already in use: if a trigger fires, Phase 0 moves to the VPS late in the phase.
  - The DSM firewall may not filter Docker-published ports, so the 8443 boundary rests on the LAN boundary and Caddy's `remote_ip` allow-lists; if Docker does not keep LAN source addresses, the Keycloak admin paths stay unrouted.
- Review triggers:
  - Any fallback trigger F1 to F6.
  - The first need for remote access: revisit R2 (router WireGuard, then a mesh VPN with an ACL).
  - The first deployment reachable from the public internet (go-live VPS): a public domain with DNS-01 or HTTP-01 (R3.2), `caddy-ratelimit` (R4), physical backups with point-in-time recovery (R7), port 443.
  - Any image moving to an x86-64-v3 base.
  - Any wish to turn QuickConnect back on (needs a new review).
  - The Phase 0 exit, the move to the VPS, or a suspected compromise of the NAS or DSM: remove the Caddy root from every store and regenerate it.
- Enforced by (in the issues named): the release workflow's digest, scan and cosign steps (#119, done); guard tests over the Compose file and Caddyfile for digest pins, scanned digests, R9's container rules, networks, published ports, key ring mounts, memory limits and `remote_ip` allow-lists (#120); the firewall, reachability, trust and headroom checks and the F1 to F4 measurements (#120, #29); the restore drill (#30).
