# Architecture Decision Records

MADR format. Status flows Proposed → Accepted → (Deprecated | Superseded by NNNN).

| # | Title | Status | Date |
| --- | --- | --- | --- |
| [0001](0001-multi-tenant-single-instance.md) | Multi-tenant model, single-instance deployment | Accepted | 2026-09-20 |
| [0002](0002-keycloak-identity-provider.md) | Keycloak as sole identity provider | Accepted | 2026-09-20 |
| [0003](0003-bff-session-pattern.md) | BFF session pattern with Redis ticket store | Accepted | 2026-09-20 |
| [0004](0004-vendor-neutral-ai.md) | Vendor-neutral AI via Microsoft.Extensions.AI | Accepted | 2026-09-20 |
| [0005](0005-modular-monolith-wolverine.md) | Hybrid modular monolith with Wolverine | Accepted | 2026-09-20 |
| [0006](0006-money-and-time-types.md) | Money as decimal minor units, time as NodaTime | Accepted | 2026-09-20 |
| [0007](0007-portable-storage.md) | Postgres, Redis, S3-compatible storage only | Accepted | 2026-09-20 |
| [0008](0008-spa-capability-manifest.md) | React SPA behind the BFF with server-computed capability manifest | Accepted | 2026-09-20 |
| [0009](0009-dotnet-10-lts-baseline.md) | Target .NET 10 LTS and Aspire 13.5 | Accepted | 2026-09-22 |
| [0010](0010-agent-sandbox-devcontainer.md) | Run Claude Code agents in a network-isolated devcontainer | Accepted (partly superseded by [0011](0011-host-development-default.md)) | 2026-09-23 |
| [0011](0011-host-development-default.md) | Host development by default; the agent sandbox is optional | Accepted | 2026-09-26 |
