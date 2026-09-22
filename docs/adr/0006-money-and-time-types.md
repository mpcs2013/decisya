# 0006. Money as decimal minor units, time as NodaTime

- Status: Proposed
- Date: 2026-09-20
- Deciders: Marco
- Tags: data

## Context and problem statement

Floating-point money and naive DateTime cause silent financial errors.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **decimal + DateTime** — rounding fine, time zones ambiguous.
2. **double** — never for money.
3. **Money value object (decimal, minor units, ISO 4217) + NodaTime** — explicit and testable.

## Decision outcome

Chosen option: **Money value object + NodaTime**

### Consequences

- Good: Deterministic rounding; unambiguous instants and civil dates
- Bad: Slight learning curve for NodaTime mapping
- Enforced by: BannedSymbols.txt (RS0030 as error) and Money unit tests
