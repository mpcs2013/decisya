# Deployable stack: publish, assemble, check, start, rotate

- Owner: devops · Last verified: 2026-10-05 (G4 of #120; .NET SDK from `global.json`, Aspire 13.5.4); "Realm changes" and the realm-related rows written 2026-10-07 (G4 of #121, Keycloak 26.7.5); "The key ring" and the rows that mention it written 2026-10-08 (G4 part 4 of #122). **No command below has been run by its author**: the agent that wrote it could run only `python .claude/scripts/lint.py` and `actionlint`. Every command is therefore marked **(unverified)** until Marco or G5 has run it once, and `deploy/tests/stack_smoke.py` is the run that clears them (G5 records the result in `docs/ai/pipeline/120.md` and `121.md`).
- When to use: starting the full stack (Caddy, Keycloak, Postgres, Redis, Api, BFF with the SPA, migrator, OTel collector) on a Linux Docker host from a clean clone, updating it to a new release, rotating a secret, changing or rebuilding the Keycloak realm, or re-exporting Caddy's root. The NAS bring-up (DSM checks, the work-window deploy, Container Manager, browser trust) is #132 and extends this runbook. What blocks the first real account is `docs/runbooks/go-live-gate.md`.
- Design: ADR-0018, `docs/architecture/deployable-stack.md`, `docs/architecture/production-identity.md` (the realm), threat models `docs/security/threat-models/deployable-stack.md` and `production-identity.md`. Release verification: `docs/runbooks/release.md`.
- Column 1 is Visual Studio 2026 or VS Code (Marco runs terminal steps in the VS Code PowerShell terminal; on the Linux host use its shell). Column 2 is the CLI. Commands are written for a POSIX shell; the PowerShell form is given where it differs.

## Rules that never bend

- **The publish command is never `dotnet run`.** Where the Aspire CLI is installed, `dotnet run` on this AppHost is taken over by it, starts run mode and ignores the publish arguments. Run the built AppHost DLL directly (step 2).
- **Never a bare `docker compose up`** in the stack folder, and never the generated file alone: it has none of the isolation. The two assembled files have non-default names on purpose, and every command here passes both (`stackctl.py` does it for you).
- **The stack folder is outside every Git working tree**, and so are its secrets, its environment file and its `images.txt`. `stackctl.py check` refuses a folder inside a clone.
- **The key ring's certificate pair never leaves your hands in the wrong direction.** `$STACK/secrets/Bff__DataProtection__Certificate` and `…CertificatePassword` are copied off the NAS by you, to your own store (after `init`, `add` and `rotate`), and are never in a backup, a snapshot that leaves the box, an image, a Compose file or a message (*The key ring → Backup*).
- **`--generate-keyring-certificate` is a tool of `stackctl.py`, never a Compose `command`, `entrypoint` or `healthcheck`.** It prints a private key on standard output; `check` refuses any service that names it.
- **No secret value on a command line, in an environment variable, in `.env`, in a log or in a message to anyone.** `stackctl.py` prints file and key names only. Nothing here asks you to read a secret file, with one exception: the single-use bootstrap password, which you copy to the clipboard without displaying it (*Realm changes → The permanent admin*).
- **No home-network address in the repository**, in an issue, a PR or a manifest: use the documentation ranges (RFC 5737: `192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`) in examples. Real values go in the stack folder's environment file, or in `ops.local/` (git-ignored) for private notes. CI scans every PR for this, docs-only PRs included.
- The environment file is named `.env` **inside the stack folder only**. The committed template is `deploy/compose/stack.env.example` (every value empty).

## Prerequisites

| Visual Studio 2026 / VS Code | CLI |
| --- | --- |
| A Linux host with Docker Engine and the Compose plugin, an account that may run Docker | `docker --version`, `docker compose version` |
| .NET SDK from `global.json` (to publish) | `dotnet --version` |
| Python 3.10 or later (stdlib only) | `python3 --version` |
| `cosign` or Docker, and `curl` | `cosign version` **(unverified)**, `curl --version` |
| A stack folder path outside any clone, owned by the account that runs Compose, for example `/srv/decisya-p0` | `mkdir -p /srv/decisya-p0` **(unverified)** |
| Shell variables used below. Unset every `COMPOSE_*` variable first: `check` refuses them. | `export STACK=/srv/decisya-p0` **(unverified)**. PowerShell: `$env:STACK = "/srv/decisya-p0"` |

## Steps

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Clean clone at the release tag.** VS 2026: *Git → Clone Repository*, then *Git → Manage Branches*, right-click the tag `v<version>` and check it out. VS Code: *Source Control → … → Checkout to…* and pick the tag. | `git clone <repository url> decisya` then `git -C decisya checkout v<version>` **(unverified)**; confirm with `git -C decisya describe --tags --exact-match` and compare with `version.txt`. |
| 2 | **Publish the Compose file** into a scratch folder. VS 2026: right-click `Decisya.AppHost` → *Build*, then run the second command from *View → Terminal*. The same two commands work in both columns. | `dotnet build src/Decisya.AppHost/Decisya.AppHost.csproj --configuration Release -warnaserror` **(unverified)**, then `dll=$(dotnet msbuild src/Decisya.AppHost/Decisya.AppHost.csproj -getProperty:TargetPath -p:Configuration=Release)` and `dotnet "$dll" --operation publish --step publish --output-path /tmp/decisya-publish` **(unverified)**. `Directory.Build.props` moves the build output out of the repository (`%LOCALAPPDATA%\decisya\artifacts\<hash>\bin\Decisya.AppHost\release\` on Windows, `~/.local/share/decisya/artifacts/<hash>/bin/Decisya.AppHost/release/` on Linux), so never guess the path; `TargetPath` prints it. In PowerShell use `$dll = dotnet msbuild … -getProperty:TargetPath …` and `dotnet $dll --operation publish --step publish --output-path $env:TEMP\decisya-publish`. It writes only `docker-compose.yaml`. |
| 3 | **Assemble** the stack folder. This fails unless the freshly published file equals the committed `deploy/compose/docker-compose.yaml` (drift). It copies the two Compose files under non-default names, the Caddyfile, the collector, Redis and Postgres init configuration, the Keycloak wrapper, the production realm file, the password list and the identity check, writes `MANIFEST.sha256`, creates `secrets/` (0700) and `trust/`, and copies `stack.env.example` to the stack's `.env` once. | `python3 deploy/compose/stackctl.py assemble --stack "$STACK" --published /tmp/decisya-publish/docker-compose.yaml` **(unverified)** |
| 4 | **Verify the release images**, then save `images.txt`. Take `images.txt` and `VERIFY.txt` from the GitHub release of the tag, run the `cosign verify` commands of `VERIFY.txt` (print them with `python3 .github/scripts/release_images.py verify-command` as `docs/runbooks/release.md` says; never retype them) and only then copy `images.txt` into the stack folder. | `cp images.txt "$STACK/images.txt"` after `cosign verify …` has passed **(unverified)** |
| 5 | **Fill in the environment file** `$STACK/.env`: the bind address, the LAN subnet, the workstation address, the three host names and the three image references from `images.txt`. Rules per key are in `deploy/compose/stack.env.example`. VS Code: *File → Open File…* and pick it from the stack folder (it is outside the workspace on purpose). | Edit with any editor, for example `nano "$STACK/.env"` **(unverified)**. No quotes, no spaces; the port must be 8443. |
| 6 | **Create the secrets** (first deploy only). It writes 17 files (0444 in a 0700 folder), derives each connection string and the Redis ACL hash from the same credential, refuses to overwrite anything and prints no value. Four of the 17 are the key ring's certificate (a base64 PKCS#12 and its password, then an empty previous pair): `init` runs the BFF image's generator with no network and the password on standard input, so Docker must be running and `DECISYA_BFF_IMAGE` (step 5) must be filled in; it pulls the image by that digest if it is not local, and writes nothing when the generator fails. These files are the only copy: `$STACK/secrets/` is **not** part of the nightly backup (*The key ring → Backup*), so copy the two certificate files off the NAS to your own store now. | `python3 deploy/compose/stackctl.py secrets init --stack "$STACK"` **(unverified)** |
| 7 | **Check everything.** Environment values by type, the allow-lists against every Docker network of this host, the assembled files against the clone, the secrets' shapes and modes, the merged Compose configuration (isolation, networks, secrets per consumer, images equal to `images.txt` and to `ContainerImages.cs`) and the Caddyfile through `caddy adapt` with your real values. Fix every `PROBLEM:` line first. | `python3 deploy/compose/stackctl.py check --stack "$STACK"` **(unverified)**. Expect `check: OK`. |
| 8 | **Start.** `up` runs the check again, starts Caddy alone, waits until it is healthy, exports Caddy's public root into `$STACK/trust/caddy-root.crt` (the certificate only, never the key), runs `keyring prepare` (the key-ring volume gets owner 1654, folder 0700, files 0600 before the BFF can start), starts everything else, waits for Keycloak (its first start imports the realm), and runs `verify`: the `docker inspect` guards, the key-ring counts (no plaintext key file, right owner and modes) and the read-only identity check on Keycloak's database. | `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)**. Expect `verify: OK` and a `note: identity:` line with `users_total=0`. First start of Keycloak takes about a minute and a half. |
| 9 | **Smoke** (G5, and after any change to the stack files). It clones the branch, publishes, assembles with a throwaway environment, starts, asserts, scans for secret values (the certificate and its windows included), restarts the stores, scans again and removes everything. It also checks the key ring as the hardened read-only tool sees it, and, last of all, sends eleven `GET /bff/login` requests with eleven different invented `X-Forwarded-For` values: the eleventh must be a 429 with a whole-second `Retry-After`, and the BFF log must hold exactly one `ratelimit.rejected` event (login, ip) and none with (login, unknown). That spends this client's login budget for a minute. Refuses a host that already has a `decisya` project. | `python3 deploy/tests/stack_smoke.py --workdir /srv/decisya-smoke --images-txt images.txt --report /tmp/decisya-smoke.json` **(unverified)** |
| 10 | **After the first start: the bootstrap admin.** Create your permanent admin with a second factor and delete the temporary one, following *Realm changes → The permanent admin* below. Then empty the single-use secret and restart Keycloak. From then on `verify` fails if a master-realm user has no OTP credential. | `python3 deploy/compose/stackctl.py secrets retire keycloak_bootstrap --stack "$STACK"` **(unverified)**, then the restart in *Compose shortcut* below. |

### Compose shortcut

Anything `stackctl.py` does not wrap (restart, logs, ps) needs both files, the project directory and the environment file. Bash, in the stack folder's parent:

| Visual Studio 2026 / VS Code | CLI |
| --- | --- |
| — (terminal only) | `C="docker compose --project-directory $STACK --env-file $STACK/.env -f $STACK/stack.generated.yaml -f $STACK/stack.overlay.yaml"` then `$C ps`, `$C logs --no-color keycloak`, `$C restart keycloak` **(unverified)**. PowerShell: `function C { docker compose --project-directory $env:STACK --env-file "$env:STACK/.env" -f "$env:STACK/stack.generated.yaml" -f "$env:STACK/stack.overlay.yaml" @args }` |

## Rotate a secret

Every command writes each file atomically and leaves no `.old` copy. Names are those of `stackctl.py secrets rotate --help`.

| Credential | CLI | Then |
| --- | --- | --- |
| `postgres_superuser` | `python3 deploy/compose/stackctl.py secrets rotate postgres_superuser --apply-db --stack "$STACK"` **(unverified)** | nothing: init scripts and break-glass only |
| `migrator_db` | `… rotate migrator_db --apply-db …` **(unverified)** | the migrator reads it on its next run (`up`) |
| `tenancy_db`, `entitlements_db` | `… rotate tenancy_db --stack "$STACK"` **(unverified)** | `$C run --rm migrator` (it re-sets the role's verifier), then `$C restart api` |
| `keycloak_db` | `… rotate keycloak_db --apply-db …` **(unverified)** | `$C restart keycloak` |
| `redis` | `… rotate redis …` **(unverified)** | `$C restart redis bff`. Sessions end (Redis is not persistent). |
| `bff_client` | `… rotate bff_client …` **(unverified)** | Keycloak imported the old secret once and keeps it: in Phase 0 run `… realm rebuild --confirm …` (*Realm changes → Rebuild*), then `$C restart bff`. After go-live this is go-live gate item C-03b. |
| `user_id_hash_key` | `… rotate user_id_hash_key …` **(unverified)** | `$C restart api bff`, and run the migrator once. Correlation across the rotation is lost (accepted). |
| `dataprotection_cert` | `… rotate dataprotection_cert …` **(unverified)** | not a one-line rotation: *The key ring → Rotate*. It moves the current pair to the previous pair first; sessions survive. |

`--apply-db` sends an `ALTER ROLE` with a client-computed SCRAM verifier over stdin; if it fails, no file changes. `--files-only` writes the files without touching the role (you alter the role yourself, for example with `\password`). A role-owning credential never rotates without one of the two flags.

## The key ring (#122)

The BFF's Data Protection key ring is the `bff-keyring` volume, read-write in the BFF only. Its keys are wrapped with a certificate (RSA 4096, a base64 PKCS#12) whose password sits beside it: four BFF-only file secrets, `Bff__DataProtection__Certificate`, `…CertificatePassword`, `…PreviousCertificate` and `…PreviousCertificatePassword`. The previous pair is **empty when unused, and both of its files are empty together**. The certificate is a long base64 value with no newline; the password is 32 or more letters and digits. `stackctl.py` makes both with the BFF image's generator, and prints neither.

At every start the BFF checks the ring and **refuses to start** (event 1830, `keyring.startup_check.failed`, nothing written) when a live key does not decrypt with the current or previous certificate, when a key file is plaintext, or when the folder is not 0700. That is deliberate: a missing certificate must never become a silent new ring. The volume itself is made right by `keyring prepare` (`up` runs it): owner 1654, folder 0700, files 0600. The one-shot container behind `prepare` and `reset` runs the pinned Caddy image with no network, a read-only root, every capability dropped but `CHOWN`, `DAC_OVERRIDE` and `FOWNER`, and exactly one mount: the named volume. It refuses a volume that holds a link, a pipe, a socket or a device, and prints counts only.

`verify` counts the key files that fail the strict rule (a `masterKey` or `unencryptedKey` anywhere; or not exactly one `encryptedSecret` with the certificate decryptor and an `EncryptedData`), and the entries with the wrong owner or mode. It prints counts, never a name or a content.

### Rotate (Phase 0 exit, the move to the VPS, a compromise, or once a year)

Sessions survive a rotation: the BFF unwraps old keys with the previous certificate. The command refuses while the previous pair is not empty (retire it first), unless `--drop-previous` is given; the BFF then refuses to start if a live key was wrapped by the dropped certificate.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Rotate.** It moves the current pair to the previous pair and writes a new current pair. | `python3 deploy/compose/stackctl.py secrets rotate dataprotection_cert --stack "$STACK"` **(unverified)** |
| 2 | **Copy the two new current files off the NAS** to your own store (do not open them): `Bff__DataProtection__Certificate` and `Bff__DataProtection__CertificatePassword`. A restore needs this pair; nothing else keeps it. | `cp "$STACK/secrets/Bff__DataProtection__Certificate" "$STACK/secrets/Bff__DataProtection__CertificatePassword" <your own store>` **(unverified)** |
| 3 | **Restart the BFF** so it reads the new files. | `$C restart bff` **(unverified)** |
| 4 | **Prove it.** `verify` ends with `verify: OK`, and its `note: keyring:` line shows `failing=0`. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)** |
| 5 | **Write the retirement date** in your own notes: see *Retire* below. | — (decision) |

### Retire the previous certificate

Retire it once no live key needs it. The BFF logs event 1831, `keyring.previous_certificate`, at every start: the latest expiration among the keys that only the previous certificate decrypts. **Retire after that date plus 10 hours** (the session lifetime). If you cannot read the event, the fallback is **93 days** after the rotation (the 90-day key lifetime plus the 10 hours, with margin). Too early is not dangerous: the BFF refuses to start and writes nothing; put the previous pair back from your off-NAS copy and wait.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Read event 1831.** VS Code with the Docker extension: *Containers*, the `bff` container, *View Logs*, and search `keyring.previous_certificate`. | `$C logs --no-color bff \| grep keyring.previous_certificate` **(unverified)**. PowerShell: `C logs --no-color bff \| Select-String keyring.previous_certificate` |
| 2 | **Retire** (both previous files become empty together). | `python3 deploy/compose/stackctl.py secrets retire dataprotection_previous --stack "$STACK"` **(unverified)** |
| 3 | **Restart the BFF**, then prove it. If it refuses to start (event 1830), a live key still needs the old certificate: restore the previous pair from your off-NAS copy and retry later. | `$C restart bff` then `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)** |

### Reset the ring

`keyring reset --confirm` stops the BFF, **deletes** (it does not move) every key file that fails the strict rule, sets the owner and modes again, and leaves the BFF stopped. Certificate-wrapped keys stay. The next start creates a new certificate-wrapped key; every old cookie then fails to unprotect, so the caller is anonymous (401 on `/api`, never 500) and signs in again; orphaned Redis tickets expire within 10 hours and cannot be read. After a reset no plaintext key remains in the volume, so none reaches a later backup copy. Use it when `verify` reports plaintext key files, or after the upgrade below.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Reset.** Refused without `--confirm`. | `python3 deploy/compose/stackctl.py keyring reset --confirm --stack "$STACK"` **(unverified)**. Expect `key ring: reset kept=<n> deleted=<n>; prepared files=<n> dirs=<n>`. |
| 2 | **Start the BFF** (with everything else). | `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)** |
| 3 | **Sign in once** from a browser (Firefox); then `verify`. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)**. Expect `verify: OK`. |

### Upgrade an existing #120 stack (one forced sign-in)

A stack built by #120 has no certificate and an unencrypted ring (the BFF would refuse to start on it). Phase 0 users are synthetic, so the cost is one sign-in. The order matters; a wrong order fails closed: `up` before `reset` meets the plaintext refusal, and `reset` before `add` cannot read the missing secret files.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Check out the new release tag, publish and assemble** (steps 1 to 4 above; update the three image values in `$STACK/.env`). `assemble` keeps the secrets. | as steps 1 to 4 |
| 2 | **Add the certificate files.** It creates only the four missing files and refuses when any of them exists; no other secret is touched. It needs the BFF image from step 1. | `python3 deploy/compose/stackctl.py secrets add dataprotection_cert --stack "$STACK"` **(unverified)** |
| 3 | **Copy the two new current files off the NAS** to your own store (do not open them). | `cp "$STACK/secrets/Bff__DataProtection__Certificate" "$STACK/secrets/Bff__DataProtection__CertificatePassword" <your own store>` **(unverified)** |
| 4 | **Check.** | `python3 deploy/compose/stackctl.py check --stack "$STACK"` **(unverified)**. Expect `check: OK`. |
| 5 | **Reset** the unencrypted ring. | `python3 deploy/compose/stackctl.py keyring reset --confirm --stack "$STACK"` **(unverified)** |
| 6 | **Start.** `up` prepares the volume again and runs `verify`. | `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)**. Expect `verify: OK`. |
| 7 | **Sign in once** (Firefox); the old cookies no longer work. | — (browser) |

### A suspected compromise of the certificate or the ring

A key wrapped by a leaked certificate is exposed to whoever also holds a copy of the ring, and it passes the plain reset (it is certificate-wrapped). So a compromise is **not** a rotation: it is a new certificate, a reset of **every** key file, and the end of every session (S-122-04).

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Sign everyone out of Keycloak first**: until this runs, the refresh tokens held in the exposed tickets stay usable at Keycloak. Console (Firefox, the id host's `/admin/` from the workstation): realm `decisya`, *Sessions*, *Sign out all active sessions*. | `$C exec keycloak /opt/keycloak/bin/kcadm.sh create logout-all -r decisya --config /tmp/kcadm.config` **(unverified)** (sign in first as in *Realm changes → Make a small change*, step 2) |
| 2 | **New certificate.** The leaked pair becomes the previous pair for the moment. | `python3 deploy/compose/stackctl.py secrets rotate dataprotection_cert --drop-previous --stack "$STACK"` **(unverified)** |
| 3 | **Reset every key file**, certificate-wrapped ones too (`--all-keys`). | `python3 deploy/compose/stackctl.py keyring reset --confirm --all-keys --stack "$STACK"` **(unverified)** |
| 4 | **Retire the leaked pair at once**: no live key needs it any more. | `python3 deploy/compose/stackctl.py secrets retire dataprotection_previous --stack "$STACK"` **(unverified)** |
| 5 | **Copy the new pair off the NAS** to your own store, and replace the old copy there. | `cp "$STACK/secrets/Bff__DataProtection__Certificate" "$STACK/secrets/Bff__DataProtection__CertificatePassword" <your own store>` **(unverified)** |
| 6 | **Start** the stack. | `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)** |
| 7 | **If the host itself is suspected,** rotate `Bff__Oidc__ClientSecret` too. | `… rotate bff_client …` and then *Realm changes → Rebuild* **(unverified)** |

### Backup and restore (the contract for #30)

- **What is copied.** The nightly job copies the whole `bff-keyring` volume through a read-only mount, as uid 1654, into the `dumps` folder. The copy holds certificate-wrapped key XML only.
- **The Hyper Backup source is `dumps` alone.** `secrets/` is never inside `dumps`, so neither certificate pair nor any other secret reaches the backup. Redis is not in the backup either, so no archive holds ticket ciphertext and the ring together.
- **Stack-folder snapshots never leave the box.** DSM Snapshot Replication of the stack folder holds `secrets/` (the certificate pair) together with the ring copy; replicating those snapshots off the box needs a new decision first.
- **The certificate pair is not backed up.** You keep a copy of the two current files off the NAS, beside the Hyper Backup password (after `init`, `add` and `rotate`).
- **Restore sequence** (the #30 drill carries the full runbook): stop the BFF; copy the files onto the volume; run `keyring prepare`; have the certificate pair in `secrets/`; start the BFF. Payloads protected before the backup unprotect again. Without the certificate the BFF refuses to start and writes no file. Sessions do not survive, because Redis is not restored. A restore writes into the ring, so it is an integrity boundary: #30 confirms that Hyper Backup's client-side encryption authenticates the set (S-122-10).

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Normalise a restored ring** (owner and modes), then start. | `python3 deploy/compose/stackctl.py keyring prepare --stack "$STACK"` **(unverified)**, then `python3 deploy/compose/stackctl.py up --stack "$STACK"` |
| 2 | **Prove it.** `verify` counts no plaintext key file. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)** |

### Rate-limit settings

The BFF limits `/bff/login` (with sign-out and the OIDC callback paths) and `/bff/backchannel-logout` per client address, and `/api/*` and `/api/admin` per session, otherwise per address, in sliding 60-second windows: login 10, back-channel logout 300, api 300 (60 for an anonymous address), admin 30 (an admin request counts in the api bucket too). A refused request gets a 429 with a whole-second `Retry-After`, and the BFF logs one `ratelimit.rejected` event (event 1820) per partition and window. The edge limit is go-live gate item C-09b, and the BFF limit alone does not stop password guessing (*go-live-gate.md*).

The defaults need no setting. To change one, make a reviewed change to the overlay's `bff` `environment:` with the keys `Bff__RateLimits__<login|backchannel_logout|api|admin>__PermitLimit` (1 to 100000) and `…__WindowSeconds` (1 to 3600), and for `api` also `…__AnonymousPermitLimit`. `check` refuses any other `Bff__RateLimits__*` key, a value out of range and the same key on another service; the BFF refuses to start on an unknown key as well.

## Realm changes

The stack imports one realm file, `deploy/keycloak/production/realm-decisya.json`, mounted read-only as one file (never the folder), together with `common-passwords.txt`. Keycloak imports it **once, on an empty database**, after the wrapper has validated the host, the port and the client secret and turned them into the file's two placeholders. A restart never overwrites a realm that exists, so **a change to the file does not reach a running stack**. Four situations follow (architecture note `production-identity.md`, D2):

| Situation | What you do |
| --- | --- |
| First start of a stack | Nothing: step 8 imports the realm. |
| Restart, with the same or a changed file | Nothing happens to the existing realm. |
| A small change in Phase 0 (one setting, one flow step, one mapper) | The PR changes the file **and** adds an entry to the *Change log* below; you apply it to the running realm with *Make a small change*; then `verify`. |
| A large change (flows, user profile), any doubt, or a client-secret rotation, while every user is synthetic | *Rebuild*. |
| Anything, once one user is not synthetic | `realm rebuild` refuses. A non-destructive migration is go-live gate item C-03b. |

`verify` is the proof after every change: it checks, from Keycloak's own database, that the realm exists and requires TLS, that login and admin events are on with 90 days of retention and no `jboss-logging` listener, that the browser flow and the password policy are the committed ones, that the BFF client's secret and URIs hold no placeholder or wildcard, and the two trip-wires (a user without `synthetic=true` while a C-02 switch is off; a master-realm user without an OTP once the bootstrap secret is retired). It prints counts and flags, never a name.

`$C` is the Compose shortcut above. In the commands below the Keycloak admin console is the left column (Firefox, from the workstation address, on the id host's `/admin/`), and `kcadm.sh` is the right. **`kcadm.sh` signs in with a password grant, which an account that holds an OTP credential cannot complete.** Before `secrets retire` the temporary bootstrap administrator can use it; afterwards, an administrator with a second factor cannot, and the console is the only route. Write that in the change entry when it applies. The Keycloak container keeps `kcadm.sh`'s login in `/tmp`, which is a tmpfs: it vanishes when the container restarts.

### Make a small change

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Edit the committed file** in the PR: VS 2026 *File → Open → File…*, VS Code *Explorer*, the path above. The test projects (`tests/Decisya.Identity.Tests`) pin the settings that matter; run them as the PR body says. | any editor on `deploy/keycloak/production/realm-decisya.json`; `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"` **(unverified)** |
| 2 | **Sign in to the console:** the id host's `/admin/`, realm `master`. | `$C exec -it keycloak /opt/keycloak/bin/kcadm.sh config credentials --config /tmp/kcadm.config --server http://localhost:8080 --realm master --user <your admin>` (it asks for the password; never type it on the command line) **(unverified)** |
| 3 | **Make the same change in the live realm.** Realm settings, Authentication, Clients or User profile in realm `decisya`: whichever page holds the setting. | for a realm setting: `$C exec keycloak /opt/keycloak/bin/kcadm.sh update realms/decisya --config /tmp/kcadm.config -s <setting>=<value>`; for a client: `… kcadm.sh get clients -r decisya --config /tmp/kcadm.config -q clientId=decisya-bff --fields id` and then `… update clients/<id> -r decisya --config /tmp/kcadm.config -s <setting>=<value>` **(unverified)**. Never put a secret value in `-s`. |
| 4 | **Write the Change log entry** below: the date, the change, the issue, who applied it. | an editor on this file |
| 5 | **Prove it.** | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)**. Expect `verify: OK`. |

### Rebuild (Phase 0, while every user is synthetic)

`realm rebuild --confirm` stops Keycloak, drops and recreates the `keycloak` database (the only database it can name), and starts Keycloak, which imports the realm file again. Everything in Keycloak's database goes: the realm, its users, the master-realm administrator and the event store. It refuses, before it changes anything, when the identity check cannot run or prints anything unexpected, when one user lacks `synthetic=true`, or when the bootstrap secret is retired (the rebuilt Keycloak would have no administrator).

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Decide it needs a rebuild** (a large change, a doubt, or a client-secret rotation) and write the *Change log* entry. | — (decision) |
| 2 | **Make the bootstrap administrator available again** if you had retired it. | `python3 deploy/compose/stackctl.py secrets rotate keycloak_bootstrap --stack "$STACK"` **(unverified)** |
| 3 | **Rebuild.** | `python3 deploy/compose/stackctl.py realm rebuild --confirm --stack "$STACK"` **(unverified)**. Expect `rebuilt: …` and exit 0; on a refusal it prints the reason and changes nothing. |
| 4 | **Wait for Keycloak** (about a minute and a half). | `$C ps keycloak` until it is healthy **(unverified)** |
| 5 | **Recreate the permanent admin with TOTP**, then `secrets retire` and restart: *The permanent admin* below. | as below |
| 6 | **Re-provision the synthetic users**: *Provision a synthetic user* below. | as below |
| 7 | **If you rotated the client secret:** restart the BFF so it reads the file. | `$C restart bff` **(unverified)** |
| 8 | **Prove it.** | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)**. Expect `verify: OK`. |

### The permanent admin

The first administrator is made in one sitting so nobody else can enrol the second factor first (S-121-04). It lives in realm `master`, which only the workstation address can reach.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Sign in** to the id host's `/admin/` as `decisya-bootstrap`. The password is the file `$STACK/secrets/keycloak_bootstrap_admin_password`: copy it to the clipboard without showing it, paste it into the password field, then clear the clipboard. | Linux desktop: `xclip -selection clipboard < "$STACK/secrets/keycloak_bootstrap_admin_password"`. PowerShell: `Get-Content -Raw "$env:STACK/secrets/keycloak_bootstrap_admin_password" \| Set-Clipboard` **(unverified)** |
| 2 | **Create your account:** realm `master`, *Users*, *Add user*, a username of your choice, *Required user actions*: *Update Password* and *Configure OTP*. Set a temporary password (Credentials tab, *Temporary* on). Role mapping: *admin*. | — (the console; `kcadm.sh` would put the password on a command line) |
| 3 | **Enrol in the same sitting:** sign in as the new user in a private Firefox window, choose the real password, scan the code with an authenticator app. | — (browser) |
| 4 | **Delete the temporary admin** (`decisya-bootstrap`) from the console. | — (the console) |
| 5 | **Retire the secret and restart Keycloak.** | `python3 deploy/compose/stackctl.py secrets retire keycloak_bootstrap --stack "$STACK"` then `$C restart keycloak` **(unverified)** |
| 6 | **Prove it:** `verify` now fails on any master-realm user without an OTP credential. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)**. Expect `verify: OK`. |

### Provision a synthetic user

Every Phase 0 account in realm `decisya`, including your own operator account (it holds no financial data), carries `synthetic=true`. The attribute is editable by administrators only. Without it `verify` fails.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Add the user:** realm `decisya`, *Users*, *Add user*: a username and an email address that is plainly a test account, *Required user actions* as the scenario needs. In the *synthetic* field type `true`. Assign the realm role `tenant-user` or `platform-admin`. A `platform-admin` is made to enrol an OTP at its first sign-in. | `$C exec keycloak /opt/keycloak/bin/kcadm.sh create users -r decisya --config /tmp/kcadm.config -s username=<name> -s enabled=true -s attributes.synthetic=true` **(unverified)** |
| 2 | **Set the password** (Credentials tab, *Temporary* on). It must satisfy the realm's policy: 12 to 128 characters, not the username or email, not in `common-passwords.txt`, not one of the last 3. | — (the console; `kcadm.sh` would put the password on a command line) |
| 3 | **Prove it:** `verify` prints `users_without_synthetic=0`. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)** |

### Rotate the realm signing key

Keycloak keeps the realm's RS256 key in its database. Rotate by adding a key with a higher priority, keeping the old one passive for at least the longest token or session lifetime (the SSO session maximum is 10 hours; use 24 hours), then disabling it.

| # | Visual Studio 2026 / VS Code | CLI |
| --- | --- | --- |
| 1 | **Add the key:** realm `decisya`, *Realm settings*, *Keys*, *Add providers*, *rsa-generated*, algorithm RS256, a priority higher than the current key's. | `$C exec keycloak /opt/keycloak/bin/kcadm.sh create components -r decisya --config /tmp/kcadm.config -s name=rsa-<yyyymmdd> -s providerId=rsa-generated -s providerType=org.keycloak.keys.KeyProvider -s parentId=<realm id> -s 'config.priority=["<higher>"]' -s 'config.algorithm=["RS256"]'` **(unverified)** |
| 2 | **Make the old key passive** after the new one signs, and **disable it** after 24 hours. | `… kcadm.sh update components/<old key id> -r decisya --config /tmp/kcadm.config -s 'config.passive=["true"]'` and later `-s 'config.enabled=["false"]'` **(unverified)** |
| 3 | **Write the Change log entry**, then `verify`. | `python3 deploy/compose/stackctl.py verify --stack "$STACK"` **(unverified)** |

### What Keycloak's event store holds

- Login and admin events live in Keycloak's database for 90 days and are read in the console, from the workstation. They hold usernames and client addresses (accepted residual R-1); a password typed into the username field is stored too. Exports stay on the workstation. Nothing else leaves: the realm has no `jboss-logging` listener.
- The `master` realm keeps Keycloak's default `jboss-logging` listener, which writes console sign-ins to the container log (rotated, 3 files of 10 MB). That is recorded as accepted for Phase 0. To switch it off: `$C exec keycloak /opt/keycloak/bin/kcadm.sh update events/config -r master --config /tmp/kcadm.config -s 'eventsListeners=[]'` **(unverified)**, or in the console, realm `master`, *Realm settings*, *Events*, *Event listeners*.

### Change log

| Date | Change | Issue | Applied by |
| --- | --- | --- | --- |
| 2026-10-07 | Initial production realm, imported at the first start | #121 | the first start (no change to apply) |

## Update to a new release

Repeat steps 1 to 4 at the new tag (a new `images.txt`, a new assemble), update the three image values in `$STACK/.env`, run `check`, then `up`. Compose recreates only what changed. **From a #120 stack to #122:** follow *The key ring → Upgrade an existing #120 stack* instead (`add`, `reset`, `up`, one forced sign-in). After any loss or regeneration of Caddy's data, `python3 deploy/compose/stackctl.py export-root --stack "$STACK"` **(unverified)** and restart `keycloak api bff`.

## Verify

| Check | Expect |
| --- | --- |
| `python3 deploy/compose/stackctl.py check --stack "$STACK"` | `check: OK` |
| `python3 deploy/compose/stackctl.py verify --stack "$STACK"` | `verify: OK`: no unknown container, no privilege, capability, device, host namespace or Docker socket, only Caddy publishes a port, networks as designed; Keycloak holds exactly the wrapper, the realm file and the password list, read-only; and the identity check passes (below) |
| the `note: identity:` line of the same command | counts and `on`/`off` flags only, for example `users_total=0 users_without_synthetic=0 tenant_mfa=off breached_list=off`. Never a name. |
| `$C ps` | every service running or healthy; `migrator` exited 0 |
| `curl --cacert "$STACK/trust/caddy-root.crt" --resolve app.p0.home.arpa:8443:<bind address> https://app.p0.home.arpa:8443/` from a LAN client | the SPA's `index.html` |
| the same with `/admin/` on the id host from any client that is not the workstation | `403` |

The root is trusted by `curl --cacert` and by the BFF, Api and Keycloak containers only. **Do not add it to a browser or an operating-system trust store here**: that needs the name-constrained CA first (a MUST in #132, G3 S-120-09), and a dedicated Firefox profile is the only browser trust ADR-0016 allows until then.

## Rollback

| Situation | CLI |
| --- | --- |
| Stop the stack, keep the data | `$C down` **(unverified)** |
| Go back one release | Assemble the previous tag (steps 1 to 4), restore the previous `images.txt` and image values, `check`, `up`. Secrets are never rolled back. |
| Delete everything, data included | `$C down --volumes` **(unverified)**. This removes the Postgres, key-ring and Caddy volumes: Keycloak's realm and users, the sessions' key ring and Caddy's root go with them (the certificate pair stays in `secrets/`, and a new ring is wrapped with it). A new root means re-exporting it. Back up first (#30). |
| A secret may have leaked | `rotate` it (table above) and restart its consumers; for the `user_id_hash_key` also run the migrator once. |

## What a `check` problem means

| Message starts with | Meaning and fix |
| --- | --- |
| `…: missing`, `…: empty`, `…: not allowed in the stack's environment file` | an environment key is missing, empty or not in `stack.env.example` (no `COMPOSE_*`) |
| `… overlaps the Docker network …` | an allow-list value overlaps a Docker network on this host; choose another subnet, never widen an allow-list to a bridge subnet |
| `assembled file … was changed after assemble` or `differs from the clone` | someone edited the stack folder, or the clone is at another tag: check out the release tag and run `assemble` again |
| `… carries a Compose default name` | a file named `docker-compose.yaml` or `compose.yaml` is in the stack folder; delete it |
| `secret …: …` | a secret file is missing, malformed, has a trailing newline, is writable by others, or a derived file no longer matches its credential (a half-finished rotation) |
| `service …: …` | the merged configuration breaks a guard; the stack files were edited. Re-assemble from the release tag. |
| `Docker checks could not complete: …` | Docker or the pinned Caddy image was not reachable; the message names the failing call |
| `identity check: realm decisya is missing …` | Keycloak has not finished its first start, or its database was recreated and the import failed: read `$C logs --no-color keycloak` (the wrapper names the variable at fault, never a value) |
| `identity check: … could not run`, `… printed a line that is not key=value`, `… did not print all 20 keys` | the read-only check did not run or its output was not exactly what it prints: Postgres down, the `keycloak` database missing, or a Keycloak upgrade changed the table layout. `realm rebuild` treats this as a refusal. |
| `identity check: N user(s) without synthetic=true while a C-02 switch is off` | an account exists that is not marked synthetic (the trip-wire of `go-live-gate.md`): mark it if it is a test account, or switch C-02 on before a real account is created |
| `identity check: N master-realm user(s) or service account(s) without an OTP credential …` | after `secrets retire`, a master-realm user of the Keycloak console has no second factor: enrol one (*The permanent admin*). A master-realm service account (a client with a client secret) cannot hold an OTP credential and counts the same: remove the client. Keeping one needs a G3 decision. |
| `identity check:` followed by a setting (TLS, events, admin events, the browser flow, the password policy, the client's secret or URIs) | the live realm differs from the committed one. Fix it in the console and add a change-log entry, or `realm rebuild` in Phase 0. |
| `secret Bff__DataProtection__…: …`, `secrets … must be empty together`, `… holds the current certificate` | a key-ring certificate file is malformed (base64 on one line, up to 16 KiB; the password 32 to 256 letters and digits), the previous pair is half empty, or a rotation stopped half way: repair it from your off-NAS copy, or `rotate`/`retire` again (*The key ring*) |
| `volume …: driver is not allowed`, `… driver_opts is not allowed`, `… a name override` | the overlay turned a named volume into something else (possibly a host path): re-assemble from the release tag |
| `service …: the key-ring certificate generator flag …` | a service's command, entrypoint or healthcheck names `--generate-keyring-certificate`; it prints a private key and belongs to `stackctl.py` alone: re-assemble |
| `service …: Bff__RateLimits__… …`, `… is a BFF setting` | a rate-limit key is misspelt, out of range or on the wrong service (*The key ring → Rate-limit settings*) |
| `key ring: N key file(s) are plaintext or not wrapped …` | the ring holds a key the BFF would refuse (the #120 ring, or one written without the certificate): `keyring reset --confirm`, then `up` (*The key ring → Reset*) |
| BFF will not start, event 1830 (`key_undecryptable`): it cannot decrypt the key ring and the certificate is lost | **Pair in the off-NAS store:** restore the two current files into `$STACK/secrets/`, then `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)**. **Pair gone:** remove the four `Bff__DataProtection__*` secret files, run `python3 deploy/compose/stackctl.py secrets add dataprotection_cert --stack "$STACK"`, then `python3 deploy/compose/stackctl.py keyring reset --confirm --all-keys --stack "$STACK"`, then `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)**; everyone signs in again. **Never** use `down --volumes` for this: it also deletes Postgres. (VS Code / VS 2026: run the same commands in the terminal.) |
| `key ring: N entr(y/ies) have the wrong owner …` | the volume was restored or written by another user: `keyring prepare` |
| `key ring check: …` | the read-only one-shot did not run or printed something unexpected (Docker down, the pinned Caddy image absent); it fails closed and prints no name |
| `keyring … refused: …` | the volume holds a link, a pipe, a socket or a device; nothing was changed; inspect the volume by hand |
| `keycloak container: …` | the running Keycloak container mounts something other than the wrapper, the realm file and the password list, read-only: the stack was edited by hand; re-assemble and `up` |
