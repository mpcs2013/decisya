<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #120 -->
# Threat delta: deployable stack (Compose from `aspire publish`, Caddy, production configuration and secrets) (issue #120)

- Scope: manifest `docs/ai/pipeline/120.md` (Full tier, class feature, host run), G2 `docs/architecture/deployable-stack.md` (D1 to D10) and ADR-0018 (Accepted), with the dated amendments to ADR-0001, 0015 and 0016. This document covers only the delta: the deployment boundary (edge, networks, secrets, database principals, trust), the two new flows (BFF to Api through Caddy, Keycloak to the BFF's back-channel logout through Caddy), the production configuration in ServiceDefaults, and the `deploy-guards` CI job. No product module, Contracts type, message or endpoint changes, so I did not re-audit BOLA, tenant filters, validation or the AI lanes.
- Baselines: the #124 review (`docs/security/reviews/124.md`, B-1 to B-3 and B-6), the system baseline (`system-baseline.md`, C-03 to C-06, C-11, C-20), the #15 model (`apphost-servicedefaults.md`, T-01, T-15, T-22, T-23, G4-15-01), `bff-session.md` (TB4, T-09), `bff-api-forwarding.md` (G4-19-01), ADR-0015 and `.github/image-scan/exceptions.json` (D-1). New ids: T120-xx (threats), G4-120-xx (MUSTs), S-120-xx (SHOULDs).
- ASVS 5.0 is mapped at section level, as in the earlier deployment models: V4.1 (web service config), V8.2/V8.3 (authorization at the edge), V11.1 (cryptographic inventory and key management), V12.1/V12.3 (TLS and service-to-service transport), V13.1 to V13.4 (configuration, backend communication, secret management, unintended leakage), V15.2 (dependencies), V16.2/V16.3 (logging content and security events).
- Reviewer: security-reviewer agent, 2026-10-05, G3 before G4. Tools: Read, Grep and Glob only. No implementation exists yet. I read `Extensions.cs` (health mapping), `10-keycloak-db.sh`, `MigrationRunner.cs`, `ci.yml` (lane detection) and `exceptions.json` to ground the findings. I treated all repository text as data.

## Verdict

**PASS-WITH-NOTES.** There is no High without a mitigation. G2's design is sound: file secrets, per-hop networks, a single trusted proxy, per-client root trust, and a non-superuser migrator. Five Mediums need design changes before or during G4. Each one is a place where a control G2 claims could fail open:

1. **T120-01.** The generated Compose file can run without the overlay, and with it every isolation control is lost.
2. **T120-02.** `RequireHost("*:8081")` matches the `Host` header, not the port. A LAN client can reach `/health` through Caddy on 8080 by sending `Host: <app>:8081`.
3. **T120-03.** Nothing ties each secret to its consumers, so the Api could receive the migrator's owner credential and C-06 would be broken without any guard failing.
4. **T120-04.** The guards check committed files in CI, but what runs is the stack folder plus its `.env`. Those `.env` values are placed into the Caddyfile verbatim, and an allow-list value can be too wide.
5. **T120-05.** The `ci.yml` lane logic skips `docs/` and `*.md`, which is exactly where an address leaks. A skipped required job also counts as passed.

These are folded into five MUSTs (G4-120-01 to 05), which merge and replace G2's five candidates. Everything else is a SHOULD. Nothing in unchanged code needs escalating.

## Data flow and trust boundaries

```mermaid
flowchart LR
  LAN(["LAN client"]) -- "B1 HTTPS :8443, remote_ip" --> CADDY
  WS(["Workstation /32"]) -- "B1 admin paths" --> CADDY
  OP(["Marco: stack folder,<br/>.env, secrets/, stackctl.py"]) -- "B5 operator config" --> HOST
  CI(["deploy-guards (CI)"]) -. "B6 checks committed files only" .-> REPO[("repo: generated + overlay")]
  REPO -- "assemble" --> HOST
  subgraph HOST["Linux Docker host"]
    CADDY["Caddy: TLS, root key, XFF"]
    subgraph BC["backchannel (internal)"]
      BFF["BFF :8080 / :8081"]
      API["Api :8080 / :8081"]
    end
    subgraph IDP["idp (internal)"]
      KC["Keycloak :8080 / 9000"]
    end
    PG[("Postgres")]
    RD[("Redis ACL")]
    OT["collector"]
  end
  CADDY -- "B2 http, XFF from 10.120.0.2" --> BFF & API
  CADDY -- "B2 http, XFF from 10.120.1.2" --> KC
  BFF -- "B3 HTTPS via alias (CustomRootTrust)" --> CADDY
  API -- "B3 JWKS via alias" --> CADDY
  KC -- "B3 logout token via app alias" --> CADDY
  BFF -- "B4 plaintext, ACL" --> RD
  API -- "B4 plaintext, SCRAM" --> PG
  KC -- "B4 plaintext, SCRAM" --> PG
  BFF & API -. "B4 OTLP" .-> OT
```

- **B1, LAN to edge.** Caddy is the only published listener. `remote_ip` is the only access control in front of Keycloak's admin paths.
- **B2, edge to apps.** Caddy terminates TLS. The apps trust `X-Forwarded-*` from Caddy's fixed address only.
- **B3, internal clients through the edge.** The BFF, the Api and Keycloak call Caddy aliases and validate against the mounted root.
- **B4, plaintext same-host hops** (C-11, accepted residual).
- **B5, operator configuration to runtime.** This boundary is new. `.env` values and the stack folder decide what runs, and no committed guard sees them.
- **B6, CI guard to deploy.** The guards prove properties of the committed files, not of the running stack.

## Rulings on the decisions G2 flagged

**1. Health on internal port 8081 outside Development (a deliberate change to #15 G4-15-01): accepted, with a correction (T120-02, G4-120-02).**
- The change is sound. The writer returns the status word only. The port is never published, and Caddy never routes it. `--health-probe` is needed because the chiseled images have no shell.
- **The proposed mechanism is wrong, though.** `RequireHost("*:8081")` matches the request's `Host` header, not the port the connection arrived on. Caddy's `reverse_proxy` passes the client's `Host` through unchanged, and both Caddy's host matcher and `HostFilteringMiddleware` ignore the port. So `curl --resolve app…:8443:<nas> -H "Host: app…:8081" https://app…:8443/health` reaches `bff:8080`, matches `*:8081`, and runs every health check. That includes the Postgres and Redis probes, unauthenticated, from the LAN, with no rate limiter until #122. The same flaw would let a client hide its own requests from tracing if the probe-trace filter keys on the host.
- **Fix:**
  - gate the endpoints and the trace filter on `HttpContext.Connection.LocalPort == 8081` (an endpoint filter or `MapWhen`), with Kestrel bound explicitly to 8080 and 8081;
  - exempt the 8081 listener from host filtering by local port (the probe sends `Host: localhost:8081`), never by adding `localhost` to `AllowedHosts`;
  - `--health-probe` takes no URL or port argument.
- **Test:** a request on the 8080 listener with `Host: x:8081` returns 404, and produces a span.

**2. The BFF reaching the Api through Caddy: accepted.**
- It keeps G4-19-01 (`https` destination), and it keeps the bearer token on TLS for the hop across the edge.
- The Api site block admits the back-channel subnet only. Keycloak is not on that network.
- Two conditions belong to G4-120-02:
  - (a) the Caddyfile sets no `trusted_proxies`. Caddy then overwrites any client-supplied `X-Forwarded-For` at the edge. The Api sees the BFF's address, and #122 partitions the Api by caller.
  - (b) no `log_credentials` option. Caddy's access log then keeps redacting `Authorization` and `Cookie`. Caddy sees the BFF's bearer in plaintext on this hop.

**3. Keycloak reaching `/bff/backchannel-logout` through Caddy's `app` alias on `idp`: accepted.**
- This is the caller that #124 G6-124-03(b) asked #120 to name.
- The `idp` subnet is admitted only for `POST /bff/backchannel-logout`. The logout token is still validated by the BFF (#18).
- `KC_TRUSTSTORE_PATHS` widens Keycloak's trust to the Caddy root, which is acceptable because Keycloak has no egress.
- SHOULD S-120-01 (fix-now): deny that path from the LAN subnet on the `app` block. Only Keycloak calls it.

**4. No TLS between containers on one host (C-11): accepted as a residual, with its review trigger.**
- Dropping `NET_RAW` is the compensating control that matters. Docker grants `NET_RAW` by default, and on a bridge it is what allows ARP spoofing and sniffing between members of the same network. With it dropped everywhere, reading a hop needs host root, which already reads process memory.
- The other compensating controls:
  - per-hop networks;
  - SCRAM per role on Postgres;
  - a Redis ACL with `default` off;
  - session tickets in Redis are Data Protection ciphertext (bff-session D1, S-2), so the BFF to Redis hop carries no usable token.
- The ADR-0018 trigger (any hop that leaves the host gets verified TLS first) is the right gate. Telemetry off-box is named in it.
- ASVS V12.3 is a deliberate deviation, recorded in ADR-0018 and accepted by Marco.

**5. B-1 (name-constrained CA): to #83, with a hard condition (S-120-09).**
- In #120, the root is trusted only by `curl --cacert` and by the BFF, Api and Keycloak containers, which have no egress. Browser trust moved to #132.
- B-1 becomes a **MUST in #132** (or in any issue that comes first) before the root enters an operating-system trust store, or before any non-synthetic user. ADR-0016 allows the OS store "only where nothing else works". If that happens, every application on the device trusts a root whose key sits on the NAS, which is the G6-124-02 scenario.

**6. B-2 (Caddy non-root): a SHOULD, but `cap_drop: [ALL]` on Caddy is a MUST (G4-120-03).**
- Caddy binds 8443 and needs no capability at all. Running as root without capabilities still leaves the CA key readable only to Caddy's own UID 0, so a non-root UID is defence in depth.
- G4 records the outcome (S-120-02).

**7. Address-leak guards: accepted, with two gaps.**
- (a) The scan must run on every PR, including docs-only PRs (T120-05). The runbook and the manifest's G4 and G5 evidence are where a real address is most likely to be pasted, and #132 is mostly docs.
- (b) `stackctl.py check` must also validate the format and width of the values (T120-04).
- `.env.example` with empty values, and the refusal to use a stack folder inside a Git tree, are accepted as designed.
- G5 note: the smoke records *whether* Caddy sees the real client address (yes or no), never the address itself.

**8. The secret table (D3) and rotation: accepted, with these changes:**
- The guard must map each secret to its consumers (T120-03, G4-120-01). The table is the specification.
- Rotation never puts a plaintext value in argv or in SQL text. `stackctl.py` computes the SCRAM verifier client-side (Python `hashlib.pbkdf2_hmac` and `hmac`, as `ScramSha256Verifier` does) and sends `ALTER ROLE … PASSWORD 'SCRAM-SHA-256$…'` through stdin to `docker compose exec -T postgres psql`. Interactive `\password` is acceptable as the fallback.
- `secrets init` refuses to overwrite an existing file, because a re-init would desynchronise the files from the verifiers on an initialised volume. `rotate` writes atomically (a temporary file in the same directory, then a rename) and leaves no `.old` copy (S-120-03).
- The bootstrap admin password is single-use, but the wrapper exports it on every start. Delete the file after first start and have the wrapper skip a missing file (S-120-04).
- `20-decisya-db.sh` keeps the `10-` pattern: `\getenv`, `log_statement = 'none'`, `log_min_error_statement = 'panic'`. Without the last setting, Postgres logs a failed `CREATE ROLE … PASSWORD` statement with the plaintext password.
- Generated connection strings never set `Include Error Detail=true` or `Persist Security Info=true`.
- The table must list every role `MigrationRunner` provisions (G4 confirms against `Program.cs`, `Migrator:*RolePassword`).

**9. The generated-plus-overlay split: accepted. The guards must test the merged output in two places, not one (T120-01, T120-04, G4-120-03).**
- Testing the merged `docker compose config` in CI is correct, because Compose merge semantics (sequence append, `!reset`, `!override`) are exactly what a per-file check would miss.
- But CI's `--no-interpolate` output is not what runs. Three things can make the deployment differ from it:
  - the stack folder is outside Git and can be edited after `assemble`;
  - `.env` can carry `COMPOSE_FILE` or `COMPOSE_PROFILES`;
  - `docker compose up` without `-f`, or a Container Manager project in #132, loads only the default-named file. That is the generated `docker-compose.yaml`, without the overlay.
- So the same guard module runs again at deploy time over the interpolated merged configuration of the stack folder, and again after start over `docker inspect`.

## Points I was asked to attack

**Can a secret reach an environment variable, `docker inspect`, a log or an image?**

The design closes the obvious paths: `secrets:` files, a name-and-value guard on `environment:`, no secret parameter in the publish model, and a canary scan of `docker inspect` and the logs. Remaining paths:

- **`env_file:`** loads a file's values into the container environment, so they appear in `docker inspect`. Under `--no-interpolate`, they may not show in CI's `config` output. Ban the `env_file`, `build`, `include` and `extends` keys outright (G4-120-01).
- **The Aspire Compose publisher adds an Aspire dashboard service by default.** It brings an unpinned image, a published port, and OTLP wiring (possibly an OTLP API key in `OTEL_EXPORTER_OTLP_HEADERS`, a name the guard regex misses). The publish model must call `WithDashboard(false)`. Add `HEADERS` and URL userinfo (`://user:pass@`) to the guard patterns (G4-120-01). The parity guard ("nothing unaccounted for") would catch the extra image, but the spike should know to expect it.
- **Healthchecks.** With `user default off`, a `redis-cli -a <pw> ping` healthcheck puts the password in argv, or in `docker inspect` if it is written literally. Redis mounts only the ACL hash file. Use a dedicated ACL user `health` with `nopass` and `+ping` only, or an equivalent that holds no credential (G4-120-01).
- **Process environment.** The Keycloak wrapper puts the DB and bootstrap passwords into the JVM's process environment (ADR-0018 records this). They are readable only through `/proc` inside that container, not through `docker inspect`. Accepted.
- **Error paths.** The canary scan reads logs "since start" of a healthy run. Connection-failure paths (Npgsql, StackExchange.Redis, Keycloak's DB pool) are where a connection string is most likely to be printed. The smoke stops and restarts Redis and Postgres once, then repeats the canary scan over all logs (S-120-05).
- **Images.** Release images are built in CI from the repository, and the secrets live outside any Git tree. Third-party images are stock. No path found.
- **Caddy access logs:** see ruling 2(b).

**Can the `deploy-guards` CI job be bypassed?** Yes, in four ways as designed (T120-05, G4-120-03):

1. **Lane skipping.** `ci.yml` `changes` ignores `^docs/` and `\.md$` (ci.yml:69-71). If `deploy-guards` reuses that lane with a job-level `if:`, a docs-only PR skips the address scan. GitHub also treats a skipped job as a passing required check. The job therefore always runs and is never skipped at job level. The address scan step is unconditional, and only the Docker-heavy steps (drift, `caddy adapt`) may be conditioned on a `deploy`/`AppHost`/`images` lane.
2. **No workflow-level `paths:` filter.** A required check that never starts blocks every PR, which leads to admin bypasses.
3. **The required-check window.** Marco adds the check to the ruleset after merge. Until then, nothing enforces it. The G7 PR body carries the exact check name. If `.github/rulesets/main.json` mirrors the live ruleset, update it in this PR so the drift is reviewable.
4. **Guard weakening.** A PR can edit `deploy/tests/**` together with the overlay. That is review-only. G6 treats any change under `deploy/tests/**` or `stackctl.py` as security-relevant (S-120-08: add `deploy/**` to the issue skill's Full-tier areas, backlog because of the tooling freeze).

Also, `aspire publish` in CI needs the Aspire CLI. There is no `.config/dotnet-tools.json` in the repository, so the CLI is a new tool that the `Aspire.Hosting.Docker` approval does not cover. Either publish through the AppHost itself (`dotnet run` with its publish arguments), or pin the CLI in a local tool manifest with Marco's approval in the PR body. Never use an install script piped to a shell (S-120-06).

**The D-1 exception re-assessment rule: accepted, tightened (S-120-07, fix-now).**
- ADR-0015 fails only on High and Critical, so every exception is a High or Critical risk acceptance. A justification that reads "dev-only container on loopback" becomes false on the first deploy. It must be rewritten **before** the first `up`, not at expiry.
- Per entry:
  - **Keycloak `pcre2` and `pcre2-syntax`:** the JVM does not load them (they are OS packages used by tools such as `grep`), which is confirmable from the match path.
  - **Redis `zlib`:** Redis does not link zlib to my knowledge. G4 confirms with the Grype match location.
  - **Postgres `zlib`:** the server binary links zlib. The justification therefore rests on exposure, not on loading: internal networks only, authenticated clients only, and the CVE's vector. That is acceptable only if the vector needs an unauthenticated network peer or attacker-controlled compressed input that no client sends. Otherwise, bump or wait.
- G2's new rule names Caddy and "a library Keycloak loads to serve HTTP". Widen it to **any component on the request path of a LAN-reachable service: Caddy, Keycloak (including its Java dependencies, which Grype also matches), the BFF and the Api**. Such an exception needs Marco's recorded approval in the PR, with a reachability argument and an expiry of at most 30 days. Otherwise, the deploy waits for a fixed digest.

## Threats

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 id | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T120-01 | B5/B6 generated file vs overlay | E, I | The generated `docker-compose.yaml` runs alone (`docker compose up` with no `-f`, a Container Manager project, or `COMPOSE_FILE` in `.env`). Networks, `cap_drop`, `no-new-privileges`, limits, `!reset` ports and secrets are all lost on a box shared with personal data | Medium | G4-120-03 | V13.1, V13.2 | Open, fix-now |
| T120-02 | B1/B2 health on 8081 | I, D | `RequireHost("*:8081")` matches the `Host` header, so `/health` (which runs the DB and Redis checks) is reachable from the LAN through Caddy on 8080 | Medium | G4-120-02 | V13.4, V8.2 | Open, fix-now |
| T120-03 | Secret mounts | E, I | A secret is mounted into a service that is not its consumer. The Api receives `ConnectionStrings__decisya` (the migrator, owner of every schema) or the superuser password, which breaks C-06, and no guard notices | Medium | G4-120-01 | V13.3, V13.2 | Open, fix-now |
| T120-04 | B5 `.env` values into the Caddyfile and allow-lists | T, E | `{$VAR}` is substituted into the Caddyfile text before parsing, so a value with whitespace, braces or a newline adds directives. `DECISYA_WORKSTATION_ADDRESS` as a CIDR range, or an allow-list overlapping a Docker-assigned bridge (if published ports lose the source address), opens `/admin/*` to the LAN (C-03) | Medium | G4-120-02, G4-120-03 | V13.1, V8.2 | Open, fix-now |
| T120-05 | B6 `deploy-guards` | T | The job is skipped on docs-only PRs (lane logic), or counts as passing when skipped. A real address lands in a runbook or manifest, or a guard gap goes unseen | Medium | G4-120-03 | V13.1 | Open, fix-now |
| T120-06 | `environment:`, `env_file`, healthchecks, the Aspire dashboard | I | A secret reaches `docker inspect` through `env_file`, a healthcheck argv, an `OTEL_EXPORTER_OTLP_HEADERS` key, or URL userinfo that the name regex misses | Medium | G4-120-01 | V13.3, V13.4 | Open, fix-now |
| T120-07 | Rotation and init | I | A plaintext password appears in argv, in shell history or in a Postgres error log (`log_min_error_statement`), or a re-run of `secrets init` desynchronises the files from the verifiers | Low | G4-120-01, G4-120-04, S-120-03 | V13.3, V16.2 | Open, fix-now |
| T120-08 | B1 edge XFF | S | A client-supplied `X-Forwarded-For` is trusted through Caddy, which skews #122 partitions and C-10 event IPs | Low | G4-120-02 (no `trusted_proxies`; KnownProxies and `KC_PROXY_TRUSTED_ADDRESSES` set to the fixed Caddy addresses) | V4.1, V16.3 | Open, fix-now |
| T120-09 | B3 back-channel trust | S, T | A back-channel client accepts any certificate, or trust is applied process-wide | Medium | G4-120-05 | V12.1, V12.3 | Mitigated by design; G4 proves it |
| T120-10 | B4 plaintext hops (C-11) | I, T | Sniffing or ARP spoofing on a bridge network | Low | `NET_RAW` dropped everywhere, per-hop networks, SCRAM, Redis ACL, Data Protection ciphertext in Redis; ADR-0018 trigger | V12.3, V13.2 | Accepted residual (ADR-0018) |
| T120-11 | Caddy root key (B-1) | S | A stolen key from Caddy's volume signs any name trusted by a client that trusts the root | Medium if in an OS store, Low otherwise | Caddy-only volume, `cap_drop: [ALL]`, no browser trust in #120; S-120-09 makes B-1 a MUST before OS-store trust | V11.1, V12.1 | Mitigated for #120; → #83 with condition |
| T120-12 | Image scan (D-1) | E | A High or Critical in a LAN-reachable component stays excepted under a justification that is false for the deployed context | Medium | G4-120-03 (parity), S-120-07 | V15.2 | Open, fix-now |
| T120-13 | C-06 DB principals | E | The migrator runs as superuser, or PUBLIC can connect, or a module role can run DDL | Medium (C-06) | G4-120-04 | V13.2 | Mitigated by design; G4 proves it |
| T120-14 | Keycloak bootstrap admin | E | The temporary admin password stays mounted and exported on every start | Low | S-120-04 | V13.3, V6.4 | Open, fix-now |
| T120-15 | LAN to `/bff/backchannel-logout` | D | Unauthenticated LAN POSTs to a JWT-validating endpoint with no rate limit before #122 | Low | S-120-01 | V4.1 | Open, fix-now |

## MUSTs for G4 (all fix-now)

These merge and replace G2's five candidates. G2's 1 becomes 01, 2 becomes 02, 3 becomes 03 (now including deploy-time enforcement), 4 becomes 04, and 5 becomes 05.

- **G4-120-01 (Medium; T120-03, T120-06, T120-07) Secrets reach containers only as files, only their listed consumers get them, and no secret reaches `docker inspect`, argv, a log or an image.**
  - The guard asserts that every service's `secrets:` set equals its row in the D3 table, as exact sets. In particular:
    - only the migrator gets `ConnectionStrings__decisya`;
    - only Postgres gets the superuser password;
    - the Api gets only `ConnectionStrings__tenancy` and `ConnectionStrings__entitlements` (plus any role G4 confirms) and the hash key;
    - the BFF gets only `ConnectionStrings__redis`, `Bff__Oidc__ClientSecret` and the hash key.
  - Banned keys anywhere in the merged configuration: `env_file`, `build`, `include`, `extends`.
  - Environment guard patterns gain `HEADERS` and URL userinfo (`://[^/@\s]+:[^/@\s]+@`).
  - The publish model calls `WithDashboard(false)`. No `ASPIRE_DASHBOARD_*` or OTLP header variable is emitted.
  - No healthcheck carries a credential. The Redis healthcheck uses an ACL user with `nopass` and `+ping` only.
  - `20-decisya-db.sh` keeps `\getenv`, `log_statement = 'none'` and `log_min_error_statement = 'panic'`. A test asserts all three lines in both init scripts.
  - `stackctl.py` rotation uses a client-side SCRAM verifier over stdin, never argv. Generated connection strings never contain `Include Error Detail=true` or `Persist Security Info=true`.
  - Caddyfile: no `log_credentials`.
  - Proof: D10 item 5 extended, `stackctl.py` canary unit tests, and the smoke canary scan (S-120-05 adds the error path).
- **G4-120-02 (Medium; T120-02, T120-04, T120-08) The edge admits exactly the D2 table, and nothing internal is reachable through it.**
  - The D2 network table and the edge table hold, with default deny (D10 items 4 and 8).
  - No upstream on 9000 or 8081. Admin paths are workstation-only, or unrouted if source addresses are lost.
  - Health and the probe-trace filter are gated on `Connection.LocalPort == 8081`, never on `RequireHost`. The management listener is exempt from host filtering by port. Unit test: `Host: x:8081` on 8080 gives 404. The #15 G4-15-01 change is accepted on these terms.
  - The Caddyfile has no `trusted_proxies`. `ForwardedHeaders` trusts exactly 10.120.0.2, and `KC_PROXY_TRUSTED_ADDRESSES` is exactly 10.120.1.2.
  - `stackctl.py check` validates every `.env` value by type and fails closed:
    - host names are LDH labels only, matching `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$`;
    - the port is an integer from 1024 to 65535;
    - the bind address is a single unicast address and never `0.0.0.0` or `::`;
    - `DECISYA_WORKSTATION_ADDRESS` is a single host (`/32` or `/128`);
    - `DECISYA_LAN_SUBNET` is a parsed private CIDR, `/16` or narrower;
    - no allow-list value overlaps **any** Docker network on the host (`docker network inspect` of all networks, the stack's `edge` and the default bridge included), not only the committed subnets.
  - Proof: D10 items 4, 8 and 10, the Caddyfile behaviour test, and the smoke.
- **G4-120-03 (Medium; T120-01, T120-04, T120-05, T120-12) What runs is what was guarded: container isolation, image provenance and guards that cannot be skipped.**
  - The R9 rules of D10 item 3 hold, and Caddy has `cap_drop: [ALL]`.
  - Every image is either a digest from `ContainerImages.cs` that is a scan alias, or one of the three validated release references.
  - The guard module is one Python module that runs in three places:
    - (a) CI, over `--no-interpolate` merged output;
    - (b) `stackctl.py check`, over `docker compose config` of the **stack folder with its real `.env`**, plus a SHA-256 comparison of every assembled file with the clone at the release tag;
    - (c) `stackctl.py verify` after start, over `docker inspect` of every running container of the project: networks, ports, `CapDrop`, `SecurityOpt`, mounts and secrets, and no unknown container.
  - The generated file can never run alone:
    - no file in the stack folder carries a Compose default name (`compose.y*ml`, `docker-compose.y*ml` or their `override` variants). The runbook and `stackctl.py up` always pass both `-f` files;
    - `stackctl.py check` rejects any `.env` key outside the D1 allow-list (so no `COMPOSE_*`) and any extra Compose file.

    If #132's Container Manager needs a single file, that is a #132 G3 decision, and never the generated file on its own.
  - `deploy-guards` has no workflow-level `paths:` filter and no job-level `if:`. It always runs. The address scan and the `.env.example` test run unconditionally, including on docs-only PRs. A test over `ci.yml` asserts this.
- **G4-120-04 (Medium, C-06; T120-13) Database principals are least privilege.** As G2 D4 and D10 item 11:
  - `decisya_migrator` is not a superuser and has no CREATEDB;
  - module roles have DML only;
  - PUBLIC has no `CONNECT` on `postgres`, `template1`, `decisya` or `keycloak`;
  - SCRAM on TCP, and `POSTGRES_HOST_AUTH_METHOD` banned;
  - a missing migrator password fails the init with `DECISYA_STACK=1`.

  Added: the C-06 test asserts that Keycloak's role cannot connect to `decisya`, and that the Api's roles cannot connect to `keycloak`.
- **G4-120-05 (Medium, B-3; T120-09) Back-channel trust is per client, pinned to the mounted root, and fails closed.** As G2 D2 and D10 items 11 and 12:
  - `CustomRootTrust` on every back-channel handler (OIDC, token client, logout-token and JWKS retrieval, the YARP forwarder, `JwtBearer`);
  - never `SSL_CERT_FILE`, a process-wide store or a `ServerCertificateCustomValidationCallback`;
  - start-up fails when `TrustedRootPath` is set but missing or not exactly one CA;
  - tests reject a foreign root and a public root.

## SHOULDs

- **S-120-01 (fix-now, Low, Caddyfile)** On the `app` block, return 403 for `/bff/backchannel-logout` from `{$DECISYA_LAN_SUBNET}`. Only the `idp` subnet may reach it. Assert this in the Caddyfile guard.
- **S-120-02 (fix-now, Low, overlay)** B-2: Caddy runs as a non-root UID with `caddy-data` owned by that UID, with `admin off` and `skip_install_trust`. G4 records the outcome in the manifest.
- **S-120-03 (fix-now, Low, `stackctl.py`)** `secrets init` refuses if any target file exists. `rotate` writes atomically (temporary file plus rename in the same directory, mode set before the rename) and leaves no `.old` copy. Unit tests for both.
- **S-120-04 (fix-now, Low, `entrypoint-stack.sh` and runbook)** The wrapper exports `KC_BOOTSTRAP_ADMIN_*` only if the file exists. The runbook deletes the bootstrap secret file after Marco creates his permanent admin, and removes it from the service's `secrets:` (guard: allowed, but optional).
- **S-120-05 (fix-now, Low, `stack_smoke.py`)** After the healthy run, stop Redis and then Postgres for a few seconds, start them again, and repeat the canary scan over all container logs.
- **S-120-06 (fix-now, Low, CI and runbook)** `aspire publish` uses either the AppHost's own publish arguments through `dotnet run`, or an Aspire CLI pinned in `.config/dotnet-tools.json` with Marco's approval and a PR-body line. Never an install script piped to a shell.
- **S-120-07 (fix-now, Low, `exceptions.json`, ADR-0015 amendment text)** D-1 as ruled above:
  - rewrite every justification before the first deploy;
  - confirm reachability per entry from the Grype match;
  - widen the rule to every component on the request path of Caddy, Keycloak, the BFF and the Api: Marco's recorded approval, a reachability argument, and an expiry of at most 30 days.
- **S-120-08 (backlog #83, tooling freeze)** Add `deploy/**` and `stackctl.py` to the issue skill's Full-tier areas. Run the address scan in the pre-push hook too, so a real address never reaches a pushed commit.
- **S-120-09 (backlog #83, with a condition)** B-1, a name-constrained CA (a root offline, a constrained intermediate in Caddy). It becomes a **MUST** in #132, or in any earlier issue, before the Caddy root enters an operating-system trust store or before any non-synthetic user.
- **S-120-10 (fix-now, Low, collector guard)** The `debug` exporter stays at `basic` verbosity (a guard assertion). `detailed` would copy span attributes and log bodies into container logs.
- **S-120-11 (backlog #83)** Direct reachability that bypasses Caddy: Postgres can reach Keycloak's 8080 and 9000 through `pg-kc`, and the BFF and Api can reach each other's 8080 directly on `backchannel`. Each needs code execution in a container first. Revisit when per-service network policies or a second host arrive.

## Always-check items

- **BFF cookies and antiforgery:** unchanged. The cookie keeps `Secure` behind the TLS-terminating proxy, because `X-Forwarded-Proto` is trusted from Caddy only (G4-120-02).
- **JWT validation:** unchanged. The issuer is identical on the front and back channels through the alias. The Api's `Authority` stays `https`, with JWKS validated against the mounted root (G4-120-05).
- **BOLA and tenant filters, schema validation and SQL:** not touched. The new SQL is DDL in init scripts and identifier-quoted runner code (unchanged).
- **Logs:** covered by G4-120-01 (secrets) and S-120-10 (the collector). `[Sensitive]` masking is unchanged.
- **Dependencies:** one new package, `Aspire.Hosting.Docker` 13.5.x (approved). The Aspire CLI is a possible second tool (S-120-06). There are two new third-party images, Caddy and the collector, scanned through G4-120-03. G6 runs `dotnet list package --vulnerable`.
- **AI lanes:** not touched.
