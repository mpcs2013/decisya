# 0027. Money on the wire: integer minor units in the JavaScript-safe range, with the ISO 4217 code

- Status: Accepted (Marco, 2026-11-22, #341 G6)
- Accepted residual risk (Marco, 2026-11-22), where nothing contains it, as the #341 G6 review states it ([`docs/security/reviews/341.md`](../security/reviews/341.md), "Residual risks for Marco", "ADR-0027 (money on the wire), own residuals"): "The database has no range check on `opening_balance_minor`. A row written outside the API (owner SQL, a future migration) with an out-of-range value makes every read of that space's list fail with 500. That fails safe, but it denies service to that space." "From EP-04, a balance that sums past the range fails reads in the same way until EP-04 refuses such writes, as point 2 requires." "Amounts beyond 2^53 - 1 minor units cannot be expressed (accepted by the ADR)."
- Date: 2026-11-21
- Deciders: Marco
- Tags: data
- Refines: [ADR-0006](0006-money-and-time-types.md) (Money as decimal minor units). ADR-0006 stays as written; this ADR adds the HTTP form, which ADR-0006 does not cover.
- Architecture note: [`docs/architecture/341-ledger-accounts.md`](../architecture/341-ledger-accounts.md)

## Context and problem statement

#341 (EP-02, accounts) is the first issue in which an amount crosses the API: an account's opening balance and its balance. The `api-contract` skill names a provisional wire form (`{ "amountMinor": int64, "currency": "EUR" }`) and asks for an ADR the first time Money crosses the API. G1 assumption A4 (kept by Marco on 2026-11-21) says amounts travel as integer minor units plus the currency code, never as a floating-point number.

The forces:
- The domain type is `Decisya.SharedKernel.Money` (decimal major units, ISO 4217 currency; ADR-0006). Storage is `bigint` minor units plus `char(3)` (CLAUDE.md schema convention). The wire needs one exact, lossless form between them.
- The consumer is a React SPA (ADR-0008). A JavaScript `number` holds integers exactly only within plus or minus 2^53 - 1 (9 007 199 254 740 991). A JSON integer above that range is silently rounded by `JSON.parse`, which is a money error the user cannot see.
- `bigint` holds plus or minus 9.22 x 10^18. The JavaScript-safe range is about 1/1000 of it: for a currency with 2 decimals it is about 90 trillion units, for 3 decimals (KWD, BHD) about 9 trillion, and for 4 decimals (CLF) about 900 billion. No personal-finance amount comes near it.
- Solo developer: one rule that the server checks and that every later money endpoint (EP-04 transactions, budgets, forecasts) copies, with no custom JSON parsing in the SPA.

## Decision drivers

- No silent loss of precision, anywhere between the database and the screen.
- One rule for every module, checked by the server, never trusted from the client.
- No new package on either side.

## Considered options

1. **Integer minor units as a JSON number, limited to the JavaScript-safe range, plus the currency code.** `{ "amountMinor": 125000, "currency": "EUR" }`. Exact in C# (`long`) and in JavaScript (`number`) because of the range limit. Chosen.
2. **Integer minor units as a JSON number over the full `bigint` range.** Rejected: values above 2^53 - 1 are rounded by the browser's `JSON.parse` without an error.
3. **Integer minor units as a JSON string** (`"amountMinor": "125000"`). Exact over the full range. Rejected: every client parses strings into a big-integer type, the OpenAPI schema can no longer say "integer", and a string invites decimal text (`"12.50"`) that the server must refuse anyway.
4. **Decimal major units as a JSON number** (`"amount": 1250.00`). Rejected: JSON numbers are parsed as binary floating point by default in JavaScript and by many tools; it breaks A4 and invariant 4.

## Decision outcome

Chosen option: **1**, because it is exact on both ends with no custom parsing, and the range it gives up is far beyond any amount Decisya holds.

1. **Shape.** Every amount on the wire is an object `{ "amountMinor": <integer>, "currency": "<code>" }`, named `Money` in each module's OpenAPI document. `amountMinor` is the amount in the currency's minor units (ISO 4217 exponent: JPY 0, EUR 2, KWD 3, CLF 4). `currency` is exactly three upper-case ASCII letters and an active code in `Decisya.SharedKernel.IsoCurrencies`. No amount is ever a decimal number, a string or a bare number without its currency.
2. **Range.** `amountMinor` lies in plus or minus 9 007 199 254 740 991 (2^53 - 1), inclusive. The OpenAPI schema states it as `type: integer`, `format: int64`, `minimum: -9007199254740991`, `maximum: 9007199254740991`. The server refuses an input outside it with a 400 and stores nothing. A value the server would emit outside it (a sum, from EP-04 on) is a defect: the server fails the request with the generic 500 rather than emit a rounded or clipped value, and the writing issue (EP-04) must refuse a write that would make a balance leave the range.
3. **Parsing.** The server reads `amountMinor` as a JSON integer only. A fraction (`12.5`), an exponent form (`1e3`, `125000.0`), a string (`"125000"`), `null` where the schema says integer, or a duplicate member is refused (System.Text.Json with `NumberHandling = Strict`; the Web defaults' `AllowReadingFromString` is turned off).
4. **Lower case and whitespace.** `currency` on the wire must already be upper case with no whitespace (`^[A-Z]{3}$`). `Currency.TryFromCode`'s trimming and upper-casing are for code paths, not for HTTP input: `"eur"` is refused (G1 Story 1).
5. **Conversion.** `Decisya.SharedKernel.Money` gains two public members, the only conversion between minor units and `Money`:
   - `public static Money FromMinorUnits(long minorUnits, Currency currency)`: exact (`minorUnits / 10^exponent`); throws `ArgumentException` for `default(Currency)`.
   - `public long ToMinorUnits()`: exact (`Amount * 10^exponent`); throws `OverflowException` when the result does not fit in a `long`, and `InvalidOperationException` for `default(Money)`.
   The range check of point 2 is a wire rule, applied by each module's endpoint mapping, not by `Money`.
6. **Where the safe-range constants live.** In #341, `Decisya.Modules.Ledger.Contracts.MoneyDto.MinAmountMinor` and `MaxAmountMinor`. The second module that puts money on the wire moves them to one shared place in the same PR (its G2 names it); it does not copy them.

### Consequences

- Good: an amount is exact in the database, in C#, in JSON and in the browser, with no big-integer library and no custom parser.
- Good: every money endpoint and every generated client share one schema, so a drift is a contract-test failure.
- Good: a currency always travels with its amount, so the SPA never guesses a currency from context.
- Bad: amounts beyond plus or minus 2^53 - 1 minor units cannot be expressed. No household amount comes near; a later institutional use would need a new ADR (option 3).
- Bad: the currency is repeated where an object already has one (an account's `currency` and its `openingBalance.currency`). The server checks that they agree and refuses a mismatch.
- Bad: from EP-04 on, a write must check that the balance it produces stays in range.
- Invariants: no `CLAUDE.md` invariant changes. This applies invariant 4 to the HTTP boundary.
- Enforced by (each test named here exists under `tests/`, checked on 2026-11-22 for #341 G6-341-07; the map is the G5 Traceability in [`docs/requirements/341-ledger-accounts.md`](../requirements/341-ledger-accounts.md)):
  - `tests/Decisya.ContractTests` (#341, Contract, Unit lane): `LedgerOpenApiContractTests.The_generated_ledger_document_deep_equals_the_committed_openapi_ledger_json` pins the generated Ledger document, its `Money` schema included, to the committed `openapi/ledger.json`, whose `Money` schema states point 1 and point 2. The Ledger document is the only module document in #341; each later module that puts money on the wire adds its own document to this test.
  - `Decisya.Api.Tests` contract cases (#341, Integration, real Api host and Postgres): `LedgerEndpointPostgresTests.Invalid_input_is_refused_with_a_generic_validation_error_and_nothing_is_stored` holds the rows `12.5`, `1e3`, `"125000"`, `2^53`, `-(2^53)`, `"eur"`, `"XXX"` and a missing currency, each refused with a 400 and no row; `LedgerEndpointPostgresTests.Minor_units_follow_the_currencys_own_exponent` round-trips JPY 5000, EUR 125000 and KWD 1500000 exactly.
  - `Decisya.SharedKernel.Tests` (Unit): `MoneyMinorUnitsPropertyTests.FromMinorUnits_and_ToMinorUnits_round_trip_exactly_across_ten_thousand_seeded_cases_and_every_exponent` is the round trip over at least 10 000 cases across exponents 0, 2, 3 and 4 (NFR-82; a seeded loop, no property-testing package). The exceptions of point 5: `MoneyMinorUnitsPropertyTests.FromMinorUnits_refuses_default_Currency_with_an_ArgumentException`, `ToMinorUnits_of_default_Money_throws_InvalidOperationException` and `ToMinorUnits_throws_OverflowException_when_the_amount_does_not_fit_a_long`.
  - NFR-07 coverage at or above 95 %: **no test or recorded coverage run proves it for #341 yet**; the coverage figure is still missing (G5 Traceability, note 0).
  - The existing analyzer ban on `double` conversions of decimals (`BannedSymbols.txt`), proven by `BannedApiCompilationTests.Building_the_banned_api_fixture_fails_with_RS0030_for_every_banned_member` and `BannedSymbolsSyncTests.BannedSymbols_txt_lists_every_required_member_with_a_corrective_message` (`Decisya.SharedKernel.Tests`).

## Amendments

<!-- Dated, attributed additions only; earlier text is never rewritten. -->
