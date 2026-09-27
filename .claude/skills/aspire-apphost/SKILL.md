---
name: aspire-apphost
description: "How Decisya's Aspire 13.5 AppHost is wired and what goes wrong: parameters and secrets, pinned container images, persistent dev containers versus ephemeral test containers, dev-certificate HTTPS, loopback ports and orphaned containers. Use whenever a change touches src/Decisya.AppHost, container resources or the AppHost tests."
---
# aspire-apphost

The AppHost (`src/Decisya.AppHost`) starts `decisya-api`, Postgres and Keycloak. Lessons from #15 and #17; designs in `docs/architecture/apphost-servicedefaults.md` and `docs/architecture/keycloak-realm.md`.

## Secrets
- A secret reaches a container only through `builder.AddParameter(name, secret: true)`, either generated (`GenerateParameterDefault`) or set once by Marco in the AppHost's local secret store. Never hard-code a value, a default, or a literal environment secret. `AppHostConfigurationTests` checks this.
- Never read the local secret store or a `.env` file, by any tool.

## Images
- Every image is pinned by tag and sha256 digest in `ContainerImages`, one source for the AppHost and Testcontainers. `ContainerImageParityTests` ties it to `.devcontainer/engine/images.Dockerfile`. A digest bump is its own reviewed change.

## Container lifetime (the #17 incident)
- Dev runs: Postgres and Keycloak use `WithLifetime(ContainerLifetime.Persistent)` and fixed names (`decisya-postgres`, `decisya-keycloak`). They keep running after the AppHost stops, by design. The next start reuses them, so two Postgres servers can never write `decisya-postgres-data` at once; that once corrupted the volume (`PANIC: could not locate a valid checkpoint record`).
- Do not use `WithPersistentLifetime()`: it raises `ASPIREPERSISTENCE001` under `-warnaserror`.
- AppHost tests pass `--AppHost:UseEphemeralContainers=true` and a throwaway `--Postgres:DataVolumeName=decisya-apphosttests-<guid>` (`TestAppHostIsolation`), and remove that volume afterwards. The AppHost refuses ephemeral mode on the dev volume. Never point a test at `decisya-postgres-data`, and never run `docker volume rm` on it; resets are Marco's (GETTING-STARTED §3).
- A hard stop (IDE *Stop Debugging*) can leave session containers running as orphans. Marco stops them; agents never do.

## HTTPS and ports
- When the host trusts the .NET dev certificate, Aspire switches container endpoints to HTTPS (Keycloak: container port 8443, host port 8080), including the management endpoint. Tests assert paths, not schemes. Under the test host this breaks Keycloak's health check (#70, skipped with a reason); never "fix" it by disabling TLS verification.
- Every published port must bind to 127.0.0.1; `sslRequired: external` depends on it. Do not add bind mounts: the only one is the reviewed read-only realm import (`deploy/keycloak`), and a new one needs its own issue with G3.

## Verifying
- Prefer the AppHost tests (`Category=AppHost`, ephemeral containers, bounded waits) over `dotnet run` of the dev AppHost. Bound every wait with a clear message; never leave a silent 15-minute hang.

## Runnable examples
These tests are the canonical examples; CI or the host runs them, so they cannot drift from the rules above.

| Rule | Example | Run |
| --- | --- | --- |
| Secrets only via parameters; persistent dev and ephemeral test wiring | `tests/Decisya.ServiceDefaults.Tests/Architecture/AppHostConfigurationTests.cs` | `dotnet test --project tests/Decisya.ServiceDefaults.Tests` |
| Test isolation on a throwaway volume | `tests/Decisya.AppHost.Tests/TestAppHostIsolation.cs`, `AppHostResourceTests.cs` | `dotnet test --project tests/Decisya.AppHost.Tests --filter-trait "Category=AppHost"` (Docker, dev password set) |
| Image pins shared with Testcontainers and the sandbox | `src/Decisya.AppHost/ContainerImages.cs`, `tests/Decisya.Identity.Tests/ContainerImageParityTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"` |
