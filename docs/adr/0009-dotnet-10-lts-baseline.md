# 0009. Target .NET 10 LTS and Aspire 13.5

- Status: Accepted
- Date: 2026-09-22
- Deciders: Marco
- Tags: hosting, tooling

## Context and problem statement

.NET 11 RC1 (go-live) is available, GA is 10 November 2026, and Aspire 13.5 templates target both 10 and 11. Should Phase 0 start on 10 or 11?

## Decision drivers

- Solo developer; minimise tooling friction and preview risk
- Support horizon for a product that will be in Phase 0–1 through 2027
- Aspire 13.5 requires only the .NET 10 SDK

## Considered options

1. **.NET 11 RC1 now** — requires VS 2026 Insiders side by side with stable, RC-quality packages, an RC1→RC2→GA bump inside Phase 0.
2. **.NET 10 LTS** — stable VS 2026 Community, released packages, supported to November 2028.
3. **Wait for .NET 11 GA** — loses seven weeks.

## Decision outcome

Chosen option: **.NET 10 LTS**. .NET 11 is STS and its support ends in November 2028, the same month as .NET 10 LTS, so there is no support-horizon gain to offset the preview cost.

### Consequences

- Good: stable IDE and packages; no framework bump inside Phase 0.
- Bad: .NET 11 language/runtime features are unavailable until a deliberate upgrade.
- Next decision point: .NET 12 LTS (November 2027) — evaluate during Phase 4/5.
- Enforced by: `global.json` (10.0.100, latestFeature), `Directory.Build.props` (`net10.0`).
