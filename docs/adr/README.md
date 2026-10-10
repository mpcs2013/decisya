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
| [0008](0008-spa-capability-manifest.md) | React SPA behind the BFF with server-computed capability manifest | Accepted; amendment 1 Accepted (#26, 2026-10-03) | 2026-09-20 |
| [0009](0009-dotnet-10-lts-baseline.md) | Target .NET 10 LTS and Aspire 13.5 | Accepted | 2026-09-22 |
| [0010](0010-agent-sandbox-devcontainer.md) | Run Claude Code agents in a network-isolated devcontainer | Accepted (partly superseded by [0011](0011-host-development-default.md)) | 2026-09-23 |
| [0011](0011-host-development-default.md) | Host development by default; the agent sandbox is optional | Accepted | 2026-09-26 |
| [0012](0012-cross-tenant-admin-commands-target-tenant-scope.md) | Cross-tenant admin commands run in a target-tenant scope | Accepted; amendment 1 Accepted (#25, 2026-10-02) | 2026-09-30 |
| [0013](0013-audit-records-in-the-audited-command-transaction.md) | Audit records are appended in the audited command's own transaction | Accepted | 2026-10-01 |
| [0014](0014-ci-supply-chain-pinning.md) | Pin every CI and hook dependency by commit SHA or digest | Accepted | 2026-10-03 |
| [0015](0015-container-image-cve-scan-grype.md) | Scan the pinned container images with Grype, run from a digest-pinned image | Accepted; amendment 2026-10-06 Accepted (Marco, 2026-10-07) (#120) | 2026-10-03 |
| [0016](0016-phase-0-hosting.md) | Hosting for Phase 0 (home NAS) and the private VPS fallback | Accepted | 2026-10-05 |
| [0017](0017-release-images-sdk-containers-cosign.md) | Release images: SDK container publishing on pinned chiseled bases, release-please, syft SBOMs, keyless cosign | Accepted | 2026-10-04 |
| [0018](0018-deployable-stack-compose-secrets-transport.md) | Deployable stack: Compose from `aspire publish` plus a reviewed overlay, file secrets, single-host internal transport | Accepted | 2026-10-05 |
| [0019](0019-full-stack-ci-apphost-target-zap-baseline.md) | Full-stack CI: the AppHost on the runner as the test target, a per-run dev password, and an unauthenticated ZAP baseline under our own policy | Accepted (Marco, 2026-10-09) | 2026-10-09 |
