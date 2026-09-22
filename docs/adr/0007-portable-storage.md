# 0007. Postgres, Redis, S3-compatible storage only

- Status: Proposed
- Date: 2026-09-20
- Deciders: Marco
- Tags: hosting

## Context and problem statement

Provider-specific managed services would block the migration ladder.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **Managed cloud services now** — convenient; cost and lock-in before revenue.
2. **SQLite + local disk** — cheapest; no path to multi-instance.
3. **Postgres, Redis, MinIO/S3** — portable everywhere.

## Decision outcome

Chosen option: **Postgres, Redis, S3-compatible object storage**

### Consequences

- Good: Same stack from laptop to Kubernetes
- Bad: Backups and upgrades are ours to run
- Enforced by: Compose manifest from Aspire; restore drill at phase exit
