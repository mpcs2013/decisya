# Deployable stack: publish, assemble, check, start, rotate

- Owner: devops · Last verified: 2026-10-05 (G4 of #120; .NET SDK from `global.json`, Aspire 13.5.4). **No command below has been run by its author**: the agent that wrote it could run only `python .claude/scripts/lint.py` and `actionlint`. Every command is therefore marked **(unverified)** until Marco or G5 has run it once, and `deploy/tests/stack_smoke.py` is the run that clears them (G5 records the result in `docs/ai/pipeline/120.md`).
- When to use: starting the full stack (Caddy, Keycloak, Postgres, Redis, Api, BFF with the SPA, migrator, OTel collector) on a Linux Docker host from a clean clone, updating it to a new release, rotating a secret, or re-exporting Caddy's root. The NAS bring-up (DSM checks, the work-window deploy, Container Manager, browser trust) is #132 and extends this runbook.
- Design: ADR-0018, `docs/architecture/deployable-stack.md`, threat model `docs/security/threat-models/deployable-stack.md`. Release verification: `docs/runbooks/release.md`.
- Column 1 is Visual Studio 2026 or VS Code (Marco runs terminal steps in the VS Code PowerShell terminal; on the Linux host use its shell). Column 2 is the CLI. Commands are written for a POSIX shell; the PowerShell form is given where it differs.

## Rules that never bend

- **The publish command is never `dotnet run`.** Where the Aspire CLI is installed, `dotnet run` on this AppHost is taken over by it, starts run mode and ignores the publish arguments. Run the built AppHost DLL directly (step 2).
- **Never a bare `docker compose up`** in the stack folder, and never the generated file alone: it has none of the isolation. The two assembled files have non-default names on purpose, and every command here passes both (`stackctl.py` does it for you).
- **The stack folder is outside every Git working tree**, and so are its secrets, its environment file and its `images.txt`. `stackctl.py check` refuses a folder inside a clone.
- **No secret value on a command line, in an environment variable, in `.env`, in a log or in a message to anyone.** `stackctl.py` prints file and key names only. Nothing here asks you to read a secret file.
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
| 2 | **Publish the Compose file** into a scratch folder. VS 2026: right-click `Decisya.AppHost` → *Build*, then run the second command from *View → Terminal*. | `dotnet build src/Decisya.AppHost/Decisya.AppHost.csproj --configuration Release -warnaserror` **(unverified)**, then `dotnet "$(find artifacts/bin/Decisya.AppHost -name Decisya.AppHost.dll -print -quit)" --operation publish --step publish --output-path /tmp/decisya-publish` **(unverified)**. On a Windows machine the build output is under `%LOCALAPPDATA%\decisya\artifacts\<hash>\bin\Decisya.AppHost\release\`. It writes only `docker-compose.yaml`. |
| 3 | **Assemble** the stack folder. This fails unless the freshly published file equals the committed `deploy/compose/docker-compose.yaml` (drift). It copies the two Compose files under non-default names, the Caddyfile, the collector, Redis and Postgres init configuration and the Keycloak wrapper, writes `MANIFEST.sha256`, creates `secrets/` (0700) and `trust/`, and copies `stack.env.example` to the stack's `.env` once. | `python3 deploy/compose/stackctl.py assemble --stack "$STACK" --published /tmp/decisya-publish/docker-compose.yaml` **(unverified)** |
| 4 | **Verify the release images**, then save `images.txt`. Take `images.txt` and `VERIFY.txt` from the GitHub release of the tag, run the `cosign verify` commands of `VERIFY.txt` (print them with `python3 .github/scripts/release_images.py verify-command` as `docs/runbooks/release.md` says; never retype them) and only then copy `images.txt` into the stack folder. | `cp images.txt "$STACK/images.txt"` after `cosign verify …` has passed **(unverified)** |
| 5 | **Fill in the environment file** `$STACK/.env`: the bind address, the LAN subnet, the workstation address, the three host names and the three image references from `images.txt`. Rules per key are in `deploy/compose/stack.env.example`. VS Code: *File → Open File…* and pick it from the stack folder (it is outside the workspace on purpose). | Edit with any editor, for example `nano "$STACK/.env"` **(unverified)**. No quotes, no spaces; the port must be 8443. |
| 6 | **Create the secrets** (first deploy only). It writes 13 files (0444 in a 0700 folder), derives each connection string and the Redis ACL hash from the same credential, refuses to overwrite anything and prints no value. These files are the only copy: back up `$STACK/secrets/` as carefully as the data volumes. | `python3 deploy/compose/stackctl.py secrets init --stack "$STACK"` **(unverified)** |
| 7 | **Check everything.** Environment values by type, the allow-lists against every Docker network of this host, the assembled files against the clone, the secrets' shapes and modes, the merged Compose configuration (isolation, networks, secrets per consumer, images equal to `images.txt` and to `ContainerImages.cs`) and the Caddyfile through `caddy adapt` with your real values. Fix every `PROBLEM:` line first. | `python3 deploy/compose/stackctl.py check --stack "$STACK"` **(unverified)**. Expect `check: OK`. |
| 8 | **Start.** `up` runs the check again, starts Caddy alone, waits until it is healthy, exports Caddy's public root into `$STACK/trust/caddy-root.crt` (the certificate only, never the key), starts everything else, and runs `verify` over `docker inspect`. | `python3 deploy/compose/stackctl.py up --stack "$STACK"` **(unverified)**. Expect `verify: OK`. First start of Keycloak takes about a minute and a half. |
| 9 | **Smoke** (G5, and after any change to the stack files). It clones the branch, publishes, assembles with a throwaway environment, starts, asserts, scans for secret values, restarts the stores, scans again and removes everything. Refuses a host that already has a `decisya` project. | `python3 deploy/tests/stack_smoke.py --workdir /srv/decisya-smoke --images-txt images.txt --report /tmp/decisya-smoke.json` **(unverified)** |
| 10 | **After the first start: the bootstrap admin.** Log in to the id host's admin console from the workstation address (an allow-listed `/admin` path), create your permanent admin and delete the temporary one (MFA and the realm are #121 and C-01). Then empty the single-use secret and restart Keycloak. | `python3 deploy/compose/stackctl.py secrets retire keycloak_bootstrap --stack "$STACK"` **(unverified)**, then the restart in *Compose shortcut* below. |

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
| `bff_client` | `… rotate bff_client …` **(unverified)** | set the same value as the client secret in the admin console, then `$C restart bff` |
| `user_id_hash_key` | `… rotate user_id_hash_key …` **(unverified)** | `$C restart api bff`, and run the migrator once. Correlation across the rotation is lost (accepted). |

`--apply-db` sends an `ALTER ROLE` with a client-computed SCRAM verifier over stdin; if it fails, no file changes. `--files-only` writes the files without touching the role (you alter the role yourself, for example with `\password`). A role-owning credential never rotates without one of the two flags.

## Update to a new release

Repeat steps 1 to 4 at the new tag (a new `images.txt`, a new assemble), update the three image values in `$STACK/.env`, run `check`, then `up`. Compose recreates only what changed. After any loss or regeneration of Caddy's data, `python3 deploy/compose/stackctl.py export-root --stack "$STACK"` **(unverified)** and restart `keycloak api bff`.

## Verify

| Check | Expect |
| --- | --- |
| `python3 deploy/compose/stackctl.py check --stack "$STACK"` | `check: OK` |
| `python3 deploy/compose/stackctl.py verify --stack "$STACK"` | `verify: OK`: no unknown container, no privilege, capability, device, host namespace or Docker socket, only Caddy publishes a port, networks as designed |
| `$C ps` | every service running or healthy; `migrator` exited 0 |
| `curl --cacert "$STACK/trust/caddy-root.crt" --resolve app.p0.home.arpa:8443:<bind address> https://app.p0.home.arpa:8443/` from a LAN client | the SPA's `index.html` |
| the same with `/admin/` on the id host from any client that is not the workstation | `403` |

The root is trusted by `curl --cacert` and by the BFF, Api and Keycloak containers only. **Do not add it to a browser or an operating-system trust store here**: that needs the name-constrained CA first (a MUST in #132, G3 S-120-09), and a dedicated Firefox profile is the only browser trust ADR-0016 allows until then.

## Rollback

| Situation | CLI |
| --- | --- |
| Stop the stack, keep the data | `$C down` **(unverified)** |
| Go back one release | Assemble the previous tag (steps 1 to 4), restore the previous `images.txt` and image values, `check`, `up`. Secrets are never rolled back. |
| Delete everything, data included | `$C down --volumes` **(unverified)**. This removes the Postgres, key-ring and Caddy volumes: Keycloak's realm and users, the sessions' key ring and Caddy's root go with them. A new root means re-exporting it. Back up first (#30). |
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
