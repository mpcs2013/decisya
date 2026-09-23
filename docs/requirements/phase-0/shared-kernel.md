# Phase 0 – Decisya.SharedKernel

## Issue 0.04 — SharedKernel money and time types

### Scope note (role framing)

`Decisya.SharedKernel` is a foundational library with no tenant-facing surface of its
own: it is consumed by every module (ledger, budgets, forecasts, alerts, …) that *does*
act on behalf of a tenant. There is no "role in tenant" for a value type. The stories
below therefore use **"As a Decisya module developer"** as the acting role, and each
story states which tenant-facing guarantee it exists to protect, per CLAUDE.md platform
invariant 4 (`Money` is decimal minor units + ISO 4217; time is NodaTime; `double` for
amounts and `DateTime`/`DateTimeOffset.Now` are banned by analyzer) and ADR-0006.

**Entitlement plan:** N/A. `Decisya.SharedKernel` is a platform primitive shipped in
every subscription plan; it is not gated by an entitlement feature key. Any future
feature key referenced here would be at the *consuming* module's discretion (e.g.
`ledger.multi-currency`), not SharedKernel's.

---

### Story 1 — Currency models ISO 4217 codes and minor-unit exponents

As a Decisya module developer, I want a `Currency` value type that resolves an ISO 4217
alphabetic code to its correct minor-unit exponent, so that every module that touches
money agrees on how many decimal places a given currency has (e.g. `USD` has 2, `JPY`
has 0, `BHD` has 3), protecting the tenant from silently mis-scaled amounts.

#### Acceptance criteria

```gherkin
Feature: Currency resolves ISO 4217 minor-unit exponents

  Scenario: A known ISO 4217 code resolves to the correct exponent
    Given the ISO 4217 code "USD"
    When a Currency is created from that code
    Then its minor unit exponent is 2

  Scenario: A zero-exponent currency resolves correctly
    Given the ISO 4217 code "JPY"
    When a Currency is created from that code
    Then its minor unit exponent is 0

  Scenario: A three-decimal currency resolves correctly
    Given the ISO 4217 code "BHD"
    When a Currency is created from that code
    Then its minor unit exponent is 3

  Scenario: An unknown or malformed code is rejected
    Given the code "ZZZ"
    When a Currency is created from that code
    Then a validation error is raised
    And no Currency instance is produced

  Scenario: Currency equality is code-based value equality
    Given two Currency instances both created from "EUR"
    When they are compared for equality
    Then they are equal
    And they have the same hash code

  Scenario: Currency is immutable
    Given a Currency instance
    When any of its members are inspected
    Then no member allows mutating the exponent or code after construction
```

---

### Story 2 — Money is a same-currency-safe value object

As a Decisya module developer, I want a `Money` value object that pairs a decimal amount
with a `Currency` and refuses to mix currencies in arithmetic, so that a tenant's ledger,
budget and forecast totals can never be silently corrupted by adding, say, `USD` and
`EUR` amounts together.

#### Acceptance criteria

```gherkin
Feature: Money arithmetic is precise and currency-safe

  Scenario: Constructing Money at exactly the currency's precision succeeds
    Given the amount 100.50 and currency "USD" (exponent 2)
    When Money is constructed from that amount and currency
    Then the resulting Money has amount 100.50 USD

  Scenario: Constructing Money with excess precision is rejected
    Given the amount 100.505 and currency "USD" (exponent 2)
    When Money is constructed from that amount and currency
    Then a validation error is raised naming the offending precision
    And no Money instance is produced

  Scenario: Adding two Money values of the same currency succeeds
    Given 10.00 USD and 5.25 USD
    When they are added
    Then the result is 15.25 USD

  Scenario: Adding Money values of different currencies is rejected
    Given 10.00 USD and 5.25 EUR
    When they are added
    Then a currency-mismatch error is raised
    And no result is produced

  Scenario: Subtraction below zero is allowed (e.g. refunds/credits)
    Given 10.00 USD and 25.00 USD
    When the first is subtracted from the second is reversed (25.00 USD minus 10.00 USD)
    Then the result is 15.00 USD
    And Money accepts a negative result (e.g. -15.00 USD) without throwing

  Scenario: Comparison operators require the same currency
    Given 10.00 USD and 10.00 EUR
    When they are compared with "<" or ">"
    Then a currency-mismatch error is raised

  Scenario: Money formats using the currency's exponent
    Given the amount 100.00 and currency "USD"
    When Money is formatted for display
    Then the formatted string shows exactly 2 decimal places ("100.00 USD")

  Scenario: Money is a value type with structural equality
    Given two Money instances each of 42.00 USD
    When they are compared for equality
    Then they are equal
    And they have the same hash code
```

---

### Story 3 — Deterministic allocation and rounding of Money

As a Decisya module developer, I want a `Money.Allocate` operation that splits an amount
into N parts or by weighted ratios without gaining or losing minor units, so that a
tenant's split expense, budget proration, or forecast breakdown always reconciles
exactly back to the original total — no "lost penny" bugs.

#### Acceptance criteria

```gherkin
Feature: Money allocation never drifts from the original total

  Scenario: Equal three-way split of an amount that doesn't divide evenly
    Given 100.00 USD allocated into 3 equal parts
    When the allocation is computed
    Then the parts are 33.34 USD, 33.33 USD, 33.33 USD
    And the parts sum to exactly 100.00 USD

  Scenario: Weighted-ratio allocation sums back to the original amount
    Given 100.00 USD allocated by ratios [1, 1, 2]
    When the allocation is computed
    Then the parts sum to exactly 100.00 USD
    And each part is proportional to its ratio within one minor unit

  Scenario: Allocation on a zero-exponent currency never produces fractional minor units
    Given 100 JPY (exponent 0) allocated into 3 equal parts
    When the allocation is computed
    Then every part is a whole number of JPY
    And the parts sum to exactly 100 JPY

  Scenario: Allocation rejects invalid ratios
    Given 100.00 USD and ratios [0, 0]
    When allocation is attempted
    Then a validation error is raised
    And no allocation is produced

  Scenario: Rounding is deterministic (banker's rounding) regardless of amount sign
    Given an allocation step that lands exactly on a midpoint between two minor units
    When the allocation is computed
    Then the midpoint rounds to the nearest even minor unit
    And repeating the computation produces the identical result every time

  Scenario: Refund allocation mirrors the original allocation
    Given a charge of 100.00 USD allocated by ratios [1, 1, 1]
    And a refund of -100.00 USD allocated by the same ratios [1, 1, 1]
    When both allocations are computed
    Then the refund's shares are the exact element-wise negation of the charge's shares
    And this holds for every amount and ratio set, including one that lands exactly on a
      midpoint tie-break
```

---

### Story 4 — Deterministic "now" via an injectable IClock

As a Decisya module developer, I want an injectable `IClock` abstraction backed by
NodaTime, so that deterministic features (ledger postings, budget period boundaries,
forecasts, alerts) can be unit-tested against a fixed, controllable instant instead of
the real wall clock, and so that no module reaches for the banned `DateTime.Now` /
`DateTimeOffset.Now` family.

#### Acceptance criteria

```gherkin
Feature: Time is obtained only through IClock

  Scenario: Production composition root resolves a system-backed clock
    Given the application's dependency injection container is built for production
    When IClock is resolved
    Then it returns the current real-world Instant via NodaTime's system clock

  Scenario: Tests can pin and advance time deterministically
    Given a test clock fixed at 2026-01-15T00:00:00Z
    When a component depending on IClock reads the current instant
    Then it observes exactly 2026-01-15T00:00:00Z
    And when the test clock is advanced by 1 day
    Then the same component observes exactly 2026-01-16T00:00:00Z

  Scenario: No module bypasses IClock to reach NodaTime's system clock directly
    Given the compiled set of module assemblies (excluding Decisya.SharedKernel itself)
    When an architecture test scans for direct references to NodaTime's system-clock singleton
    Then no such reference is found outside Decisya.SharedKernel

  Scenario: Banned DateTime "now" members fail the build
    Given a code file that calls DateTime.Now, DateTime.UtcNow, DateTimeOffset.Now, or DateTimeOffset.UtcNow
    When the solution is built
    Then the build fails with error RS0030
    And the diagnostic message directs the developer to use IClock via SharedKernel
```

---

### Story 5 — Banned-API enforcement for money is verified, not just configured

As a Decisya module developer, I want an automated, repo-owned check proving that
double-precision money conversions fail the build, so that the enforcement described in
ADR-0006 is a tested guarantee rather than a configuration file nobody verifies.

#### Acceptance criteria

```gherkin
Feature: Double-precision money conversions are unbuildable

  Scenario: Converting a decimal amount to double fails the build
    Given a code file that calls Convert.ToDouble on a decimal amount, or Decimal.ToDouble
    When the solution is built
    Then the build fails with error RS0030
    And the diagnostic message states that Money is decimal and must never become double

  Scenario: The banned-API regression fixture is exercised in CI
    Given a minimal, intentionally non-compiling fixture project referencing each banned member in BannedSymbols.txt
    When the CI enforcement check runs
    Then it asserts that compilation fails for every listed banned member
    And the check fails CI if any banned member unexpectedly compiles (i.e. BannedSymbols.txt regressed)

  Scenario: BannedSymbols.txt stays in sync with this story's banned members
    Given the members System.DateTime.Now, System.DateTime.UtcNow, System.DateTimeOffset.Now, System.DateTimeOffset.UtcNow, System.Convert.ToDouble(System.Decimal), System.Decimal.ToDouble(System.Decimal)
    When BannedSymbols.txt is inspected
    Then every one of these members is present with a corrective message
```

---

## Out of scope for issue 0.04

- **EF Core persistence mapping** (value converters for `Money`, `Currency`, and NodaTime
  `Instant`/`LocalDate`/etc. into Postgres columns, including `Npgsql.NodaTime` wiring
  and column types/precision). Deferred to the first module issue that actually persists
  an aggregate carrying these types (e.g. the ledger module's first migration), so the
  mapping can be reviewed against a real schema rather than speculatively.
- **JSON serialization** of `Money`, `Currency`, and NodaTime types across the BFF/API
  boundary (custom `System.Text.Json` converters, wire format for the SPA). Deferred to
  the first API endpoint issue that returns one of these types in a response DTO.
- **Multi-currency conversion / FX rates.** `Money` guards against mixing currencies; it
  does not convert between them. Any FX-rate feature is a separate, later story.
- **Localized display formatting** (locale-specific thousands separators, currency
  symbol placement for the SPA). Story 2's formatting AC only guarantees the correct
  number of decimal places; locale-aware presentation is a frontend concern for a later
  issue.
## Decisions (Marco, 2026-09-23)

1. **ADR-0006 status:** moved to `Accepted` alongside this issue.
2. **Excess-precision behavior:** constructing `Money` from a decimal with more
   fractional digits than the currency's exponent allows **throws**. Rounding is always
   an explicit, separate call (e.g. percentage-based fees round before constructing).
3. **Currency table scope:** `Currency` ships the **full active ISO 4217 list** (~180
   codes with their minor-unit exponents) as static data from day one.

## Non-functional requirements

See `docs/requirements/nfr.md` — this issue adds NFR-07, NFR-08, and NFR-09.
