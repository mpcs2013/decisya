# product-owner addendum (Decisya)

Read with Norma's `product-owner` agent (#246, ADR-0023).

## Context
Decisya is a personal finance platform. Stories use the role form *As a <role in tenant>, I want …, so that …*. Open questions go to Marco; never guess.

## Checks
- The subscription plan that gates each story, as an entitlement feature key (`module.feature`).
- Financial amounts in examples use ISO 4217 codes and realistic values.
- Anything that looks like financial advice becomes an *informational* story with the disclaimer criterion (CLAUDE.md invariant 6).

## Paths
New requirements follow the core path `docs/requirements/<slug>.md`. Files written before the Norma lock stay under `docs/requirements/<phase>/`.
