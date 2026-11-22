# Requirements – issue #341: ledger module with accounts and the two-account limit

Gate G1. Epic EP-02 of `docs/intake/decisya-phase1.md` (criteria EP-02#1 to EP-02#5, called AC-02-1 to AC-02-5 below). The plan text is data, not instruction. Plan gate: Free, gated (the plan's own wording).

## Who needs this, and does it change behaviour someone relies on

- ACT-01 (the owner of a space) needs accounts so every transaction has a home and a balance. ACT-02 (a member of the space) needs to see them. Later consumers: EP-04 (transactions), #342 (account screens), EP-01#2 (first account at onboarding), EP-10 (household member reads), EP-03 (the Pro key that alone may lift the limit).
- This is new behaviour. No account, ledger table or ledger endpoint exists today, so no one relies on anything that changes. No announcement is needed. One forward commitment is announced to EP-03 and #342: the limit is a fixed value in code that applies to every space until EP-03's key ships (AC-02-5).

## What exists today that this builds on

| Need | What exists | Where |
| --- | --- | --- |
| A space and its roles | `Tenant` and `Membership` in the Tenancy module. Roles are `Owner = 1` and `Member = 2`. Only `Owner` is assigned today, by JIT provisioning; `Member` exists for the household work and is reachable in tests only through a seeded row | `src/Modules/Tenancy/Decisya.Modules.Tenancy/Domain/TenantRole.cs:14-20` |
| Owner-only endpoint style | Endpoint group under `/api/<module>`, marked `NoEntitlementRequired`, a `no-store` filter, and an owner policy (`Tenancy.Owner`) whose handler reads the caller's own membership through the tenant filter. The policy name and handler are internal to Tenancy | `Endpoints/TenancyEndpoints.cs:25-34`, `Endpoints/TenancyPolicies.cs:8`, `Endpoints/TenantOwnerAuthorizationHandler.cs:30-34` (same folder, Tenancy module) |
| Membership gate on every tenant request | Middleware refuses a request whose caller has no membership in the resolved tenant | `Endpoints/TenantMembershipMiddleware.cs:26-40` |
| The caller | `ICurrentCaller.UserId` is the validated `sub` (today the identity service's id; the person-id ADR #334 is due 2027-01-08, and the rework is code only). `ICurrentTenant` gives the resolution | `src/Decisya.SharedKernel/Tenancy/ICurrentCaller.cs:24` |
| Tenant scoping | `ITenantScoped`, global query filters, `[AllowCrossTenant]` | `src/Decisya.SharedKernel/Tenancy/ITenantScoped.cs`, `AllowCrossTenantAttribute.cs` |
| Money | `Money` (decimal `Amount` plus `Currency`) and `Currency` with `FromCode`/`TryFromCode`, an ISO 4217 table and `MinorUnitExponent` | `src/Decisya.SharedKernel/Money/Money.cs:29-52`, `Currency.cs:13-67`, `IsoCurrencies.cs:87` |
| Plan catalogue | Free holds `ledger.transactions`; Pro holds that and `forecasting.scenarios`. No `ledger.accounts.unlimited` key exists, and none is added here | `src/Modules/Entitlements/Decisya.Modules.Entitlements/Domain/PlanCatalog.cs:24-28`, `Decisya.Modules.Entitlements.Contracts/FeatureKeys.cs:11-23` |
| Entitlement gate on endpoints | Every `/api` endpoint carries `NoEntitlementRequired` or an entitlement policy | `src/Decisya.SharedKernel/Authorization/NoEntitlementRequiredAttribute.cs:3-6` |
| Audit writes | `IAuditWriter.AppendAsync(entry, dbTransaction)` appends one record inside the caller's open transaction, so both commit or both roll back. The closed `AuditAction` set has three Entitlements actions only. `AuditEntry` is `(TenantId, Action, FeatureKey?)`: it cannot carry an account id, and it must carry no free text | `src/Modules/Audit/Decisya.Modules.Audit.Contracts/IAuditWriter.cs:27`, `AuditAction.cs:9-19`, `AuditEntry.cs:19` |
| A worked pattern: command, unique-index race, audit, commit | `StartTrialHandler` opens a transaction, writes, lets a unique index decide the race, appends the audit record, commits | `src/Modules/Entitlements/Decisya.Modules.Entitlements/Application/StartTrialHandler.cs:70-94` |
| Module wiring | `AddTenancyModule(connection)`, `AddAuditModule()`, `MapTenancyEndpoints()` in the host | `src/Decisya.Api/Program.cs:39-40,78` |
| Fixtures for isolation and concurrency tests | Two-tenant matrix and a 16-way race exist for earlier modules (NFR-29, NFR-31, NFR-32, NFR-37) | `docs/requirements/nfr.md` |

Consequences for later gates (notes, not requirements):

1. `AuditEntry` and `AuditAction` need a reviewed change in the Audit contract so ledger writes can be recorded with the account id and no name or amount (G2).
2. The owner check lives behind Tenancy's internal policy. Ledger may not reference Tenancy's implementation (ADR-0005), so G2 chooses how Ledger learns the caller's role through `Modules.Tenancy.Contracts`.
3. Which existing feature key gates the ledger endpoints (the plan says "free, gated") is G2's. The requirement is that it is a key both plans already hold, and that no new key or second gating mechanism is added (AC-02-5, BL-F31).

## Assumptions (decided here; each can be reversed by Marco at no cost to the other stories)

- A1. Account name: required, trimmed, 1 to 80 characters, shown back exactly as typed, never run as markup. Names need not be unique.
- A2. An opening balance may be negative (an overdraft or a credit card) or zero. It is optional; absent means zero. The opening date is a calendar date (NodaTime `LocalDate`) and is required exactly when an opening balance is given.
- A3. Until EP-04 ships, no account holds a transaction, so the currency may be changed on any account. The lock is built on a "holds a transaction" fact that is false today and becomes true with EP-04.
- A4. On the wire, amounts are integer minor units plus the currency code, never a floating-point number.
- A5. Only successful writes are audited (ADR-0013). A refusal leaves a structured log line and a metric, not an audit record.

## Stories

### Story 1: create an account (EP-02#1, AC-02-1)

As an owner of a space, I want to create an account with a name, one currency and an optional opening balance with its date, so that my transactions have a home in the right currency.

Plan gate: Free (the key is G2's choice; see note 3). Not a behaviour change.

```gherkin
Scenario: The owner creates an account with an opening balance
  Given a space whose owner is signed in and which holds no account
  When the owner creates an account named "Everyday" in currency "EUR" with an opening balance of 1 250 EUR minor units 125000 dated 2026-12-01
  Then the account is created
  And the account is returned with its currency "EUR", its opening balance 125000 minor units and date 2026-12-01
  And the stored amount is a bigint of minor units and the stored currency is char(3) "EUR"

Scenario Outline: Minor units follow the currency's own exponent
  Given a space whose owner is signed in and which holds no account
  When the owner creates an account in currency "<code>" with an opening balance of <minor> minor units
  Then the account's balance reads back as exactly <minor> minor units in "<code>"
  Examples:
    | code | minor   |
    | JPY  | 5000    |
    | EUR  | 125000  |
    | KWD  | 1500000 |

Scenario: The opening balance is optional
  Given a space whose owner is signed in
  When the owner creates an account named "Savings" in currency "CHF" with no opening balance
  Then the account is created with a balance of 0 minor units in "CHF"

Scenario Outline: Invalid input is refused with a generic validation error and nothing is stored
  Given a space whose owner is signed in
  When the owner creates an account with <input>
  Then the request is refused as invalid with no entity, column or value detail in the body
  And no account is stored and no audit record is written
  Examples:
    | input                                                       |
    | an empty name                                               |
    | a name of 81 characters                                     |
    | currency "EURO"                                             |
    | currency "XXX" or any code absent from ISO 4217             |
    | currency "eur" in lower case that is not normalised by rule |
    | an opening balance with no date                             |
    | an opening date with no opening balance                     |
    | an opening balance outside the bigint range                 |
    | an opening balance sent as a decimal number such as 12.5    |
    | a field named iban, card_number or any field not in schema  |

Scenario: A member cannot create an account
  Given a space with an owner and a member who is signed in
  When the member creates an account
  Then the request is refused as forbidden
  And no account is stored and no audit record is written

Scenario: A request without antiforgery is refused
  Given a space whose owner is signed in
  When the owner creates an account without the antiforgery token
  Then the request is refused and no account is stored
```

### Story 2: see the accounts and the limit use (EP-02#1 second part, EP-02#2 second part, EP-02#4, AC-02-1, AC-02-2, AC-02-4)

As an owner or a member of a space, I want to list my space's accounts with each balance and how much of the account limit is used, so that I know where my money sits and how many accounts I may still add.

Plan gate: Free. Not a behaviour change. EP-10 relies on the member read; EP-03 will make the limit absent for a Pro space.

```gherkin
Scenario: The list shows accounts, balances and the limit use
  Given a space with an account "Everyday" in "EUR" opened at 125000 minor units
  And an account "Travel" in "JPY" opened at 5000 minor units
  When a member of the space lists the accounts
  Then both accounts are returned, each with its balance in its own currency
  And no balance is converted or summed across currencies
  And the response states used 2 and limit 2

Scenario: The balance is the opening balance plus the account's transactions
  Given an account with an opening balance of 100000 minor units in "EUR" and no transaction
  When the account is read
  Then its balance is 100000 minor units in "EUR"

Scenario: The balance adds the transactions once EP-04 provides them
  Given an account with an opening balance of 100000 minor units in "EUR"
  And transactions of -2500 and +10000 minor units in "EUR" on that account
  When the account is read
  Then its balance is 107500 minor units in "EUR"

Scenario: A member reads but the roles are not mixed up
  Given a space with an owner and a member
  When the member reads one account of the space
  Then the account is returned
  And the response carries no field that invites the member to create one

Scenario: An empty space lists no account and shows the limit
  Given a space with no account
  When the owner lists the accounts
  Then the list is empty and the response states used 0 and limit 2

Scenario: The response is not cached
  Given any account read
  Then the response carries "Cache-Control: no-store"
```

### Story 3: refuse a third account (EP-02#2, AC-02-2)

As an owner of a space on the Free plan, I want a clear refusal when I try to create a third account, so that I know the limit and why nothing was created.

Plan gate: Free. Changes no existing behaviour.

```gherkin
Scenario: A third account is refused with a message that names the limit
  Given a space with 2 accounts
  When the owner creates a third account with valid input
  Then the request is refused with a stable error code for "limit reached" (not a 5xx)
  And the message names the limit 2
  And the space still holds exactly 2 accounts
  And no audit record is written for the refused request

Scenario: The limit counts the space's accounts only
  Given space A holds 2 accounts and space B holds 0 accounts
  When the owner of space B creates an account
  Then the account is created in space B
```

### Story 4: the limit holds under concurrency, from one value in code (EP-02#5, AC-02-5)

As the product owner, I want the limit enforced by the server in the same transaction as the write and held as one fixed value in code, so that parallel requests cannot pass it and no setting or plan can change it before EP-03.

This story is delivered with Story 3. It changes no existing behaviour; it commits EP-03 to be the only lift.

```gherkin
Scenario: Parallel creates never pass the limit from empty
  Given a space with no account
  When 16 valid create requests arrive in parallel from the owner
  Then exactly 2 succeed and 14 are refused as "limit reached"
  And the space holds exactly 2 accounts
  And exactly 2 audit records exist for the space
  And no request ends in a 5xx or an unhandled exception

Scenario: Parallel creates never pass the limit from one account
  Given a space with 1 account
  When 16 valid create requests arrive in parallel from the owner
  Then exactly 1 succeeds and 15 are refused as "limit reached"
  And the space holds exactly 2 accounts

Scenario: Parallel creates at the limit stay at the limit
  Given a space with 2 accounts
  When 16 valid create requests arrive in parallel from the owner
  Then all 16 are refused as "limit reached"
  And the space holds exactly 2 accounts

Scenario: The race test repeats
  Given the three scenarios above
  Then each is run for 20 rounds on fresh spaces
  And no round ends with more than 2 accounts in a space

Scenario: The limit is a fixed value in code
  Given the ledger module's assemblies and the configuration of the host
  Then the limit is one constant in the module that owns accounts
  And no configuration key, environment variable or setting changes it
  And setting any configuration value named like an account limit leaves the limit at 2

Scenario: A Pro trial or an admin override does not lift the limit before EP-03
  Given a space with 2 accounts and an active Pro trial
  And an audited admin override for any existing feature key
  When the owner creates a third account
  Then the request is refused as "limit reached"

Scenario: No other mechanism exists
  Given the plan catalogue
  Then it holds no feature key named "ledger.accounts.unlimited" after this issue
  And no other plan, key or setting is read by the limit check
```

### Story 5: rename an account and lock its currency (EP-02#3, AC-02-3)

As an owner of a space, I want to rename an account, so that its name stays meaningful. And I want the currency to be locked once the account holds a transaction, so that its balance never mixes currencies.

Plan gate: Free. Not a behaviour change.

```gherkin
Scenario: The owner renames an account
  Given an account "Everyday" in a space whose owner is signed in
  When the owner renames it to "Household"
  Then the account is named "Household"
  And its currency, opening balance and balance are unchanged
  And exactly one audit record is written for the rename

Scenario: A rename with an invalid name is refused
  Given an account in a space whose owner is signed in
  When the owner renames it to an empty name or a name of 81 characters
  Then the request is refused as invalid and the name is unchanged
  And no audit record is written

Scenario: The currency can change while the account holds no transaction
  Given an account in "EUR" with no transaction and an opening balance of 125000 minor units
  When the owner changes its currency to "JPY" and supplies the opening balance 5000 minor units in "JPY"
  Then the account is in "JPY" with an opening balance of 5000 minor units
  And exactly one audit record is written

Scenario: A currency change must restate the opening balance
  Given an account in "EUR" with no transaction and an opening balance of 125000 minor units
  When the owner changes its currency to "JPY" and does not restate the opening balance
  Then the request is refused as invalid and the account is unchanged

Scenario: The currency cannot change once the account holds a transaction
  Given an account in "EUR" that holds one transaction
  When the owner changes its currency to "USD"
  Then the request is refused with a stable error code for "currency locked"
  And the currency is still "EUR" and no audit record is written

Scenario: A rename of a locked account is still allowed
  Given an account in "EUR" that holds one transaction
  When the owner renames it
  Then the account is renamed and its currency is still "EUR"

Scenario: A member cannot rename or change an account
  Given a space with an owner and a member who is signed in
  When the member renames an account of the space
  Then the request is refused as forbidden and the name is unchanged
```

### Story 6: spaces are isolated and every read and write is authorised per object (invariant 1, BOLA)

As an owner of a space, I want no other space ever to see or change my accounts, so that my finances stay private.

Not a behaviour change. Delivered across Stories 1 to 5; stated once so G5 can map it.

```gherkin
Scenario: A request for another space's account is indistinguishable from a missing one
  Given space A holds an account and the owner of space B is signed in
  When the owner of space B reads, renames or changes the currency of space A's account by its id
  Then the response is the same generic not-found response as for an id that does not exist
  And space A's account is unchanged and no audit record is written

Scenario: A list shows only the caller's own space
  Given space A holds 2 accounts and space B holds 1 account
  When the owner of space B lists the accounts
  Then exactly 1 account is returned and used is 1 of limit 2

Scenario: No tenant means no rows
  Given a caller whose tenant resolution is "none" or "invalid"
  When the caller lists, reads or creates accounts
  Then the request is refused and no query reaches the ledger tables

Scenario: A caller who is not a member of the resolved tenant is refused
  Given a signed-in caller with no membership in the space named by the token
  When the caller lists accounts
  Then the request is refused before any ledger query

Scenario: Concurrent changes never leak detail
  Given two simultaneous renames of one account
  Then any conflict answers with the generic not-found problem response of NFR-33
```

### Story 7: every write is audited (platform rule, ADR-0013)

As the platform operator, I want each successful account write recorded in the append-only audit with the actor and the account id and nothing personal, so that I can answer who changed what, when.

Not a behaviour change. Needs the Audit contract change of note 1.

```gherkin
Scenario: A create, a rename and a currency change each write one audit record in the same transaction
  Given an owner who creates, renames and changes the currency of an account
  Then exactly 3 audit records exist for the space, one per write
  And each carries the tenant, a ledger account action from the closed set, the account id, the actor's id as the caller id, the time and the trace id
  And none carries the account name, an amount or a currency amount

Scenario: A failed audit write rolls the account write back
  Given the audit write fails during a create
  Then no account is stored and the request does not report success

Scenario: A failed account write leaves no audit record
  Given the account write fails during a rename
  Then no audit record exists for that rename

Scenario: Refusals write no audit record
  Given a refused request (invalid, forbidden, limit reached, currency locked, not found)
  Then no audit record is written
```

### Story 8: the ledger module is a clean module (platform rule)

As the maintainer, I want `Modules.Ledger` and `Modules.Ledger.Contracts` with their own schema, role and migration under the same boundary rules as every module, so that EP-04 and later epics build on it safely.

```gherkin
Scenario: The module follows the boundary rules
  Given the solution
  Then Modules.Ledger references no other module's implementation project
  And no vendor AI SDK is referenced
  And the architecture tests are green, including a deliberate-violation fixture proven red

Scenario: The schema follows the rules
  Given the migrated database
  Then every ledger table is in the ledger schema, has tenant_id uuid not null and an index whose first column is tenant_id
  And the amount column is bigint and the currency column is char(3)
  And the account entity implements ITenantScoped

Scenario: The API's database role is least-privileged
  Given the role the API uses for the ledger schema
  Then it can select, insert and update accounts and cannot DROP or ALTER anything
  And it holds no right on other modules' schemas

Scenario: No IBAN or card field exists
  Given the account schema and the API contract
  Then there is no column, property or request field for an IBAN, a card number or an account number
```

## Contract and test mapping (what G5 maps)

| Plan criterion | Stories |
| --- | --- |
| AC-02-1 (EP-02#1) | 1, 2, 6, 8 |
| AC-02-2 (EP-02#2) | 2, 3, 4 |
| AC-02-3 (EP-02#3) | 5 |
| AC-02-4 (EP-02#4) | 2 |
| AC-02-5 (EP-02#5) | 3, 4 |

"Done when" of the issue: the API contract tests (Stories 1 to 6 request and response shapes), the parallel-limit test (Story 4) and the two-tenant isolation tests (Story 6) pass.

## Traceability

Gate G5. Every acceptance criterion and every Gherkin scenario of Stories 1 to 8 is mapped to the test that proves it. Test names follow the scenario titles (underscores for spaces). Abbreviations for the test projects and their lanes:

| Short | Project (path under `tests/`) | Trait | Where it runs |
| --- | --- | --- | --- |
| LT-U / LT-I / LT-A | `Modules/Decisya.Modules.Ledger.Tests` | Unit / Integration / Architecture | LT-U and LT-A in the Unit lane; LT-I needs Docker (Testcontainers, real Postgres 18, handlers as the real `decisya_ledger` role) |
| AT-I | `Decisya.Api.Tests` (`Authentication/LedgerEndpointPostgresTests`, real Api host, JWT, tenancy and entitlement gates, real Postgres) | Integration | CI on the PR (the project's Integration lane); written at G5, not yet run anywhere |
| BT-I | `Decisya.Bff.Tests` (`LedgerApiAntiforgeryTests`, real BFF with Keycloak and Redis) | Integration | CI on the PR; written at G5, not yet run anywhere |
| CT | `Decisya.ContractTests` (`LedgerOpenApiContractTests`) | Contract | Unit lane (no database) |
| MT-I | `Decisya.Infrastructure.Migrator.Tests` (`LedgerRoleIntegrationTests`) | Integration | local, Docker |
| ArT | `Decisya.ArchitectureTests` | Architecture | Unit lane |
| AuT | `Modules/Decisya.Modules.Audit.Tests` | Unit / Integration | Unit lane; Audit Integration local |

Run evidence (from `docs/ai/pipeline/341.md` § G4 evidence): the Unit lane ran green on `409afec` (2147 tests, 0 failed, the contract and Ledger unit tests included). Integration ran locally for Ledger (39), Migrator (34) and Audit (15) on `19d2a59` and for Entitlements (97) on `409afec`; between those two commits only an Entitlements test file changed. The Identity, Bff, Api, Persistence and Tenancy Integration projects run in CI. **Written at G5 and not yet run** (marked "new" below): `LedgerInputValidationTests`, `LedgerAccountDomainTests` (Unit), `LedgerAccountScenarioTests` (LT-I), `LedgerEndpointPostgresTests` (AT-I), `LedgerApiAntiforgeryTests` (BT-I) and one method in `LedgerModuleBoundaryTests` (LT-A). The orchestrator runs them.

All test classes of this issue use AwesomeAssertions (no FluentAssertions anywhere), carry a `[Trait("Category", ...)]`, and every Integration test uses real Postgres through Testcontainers or the real Api or BFF host; no test mocks a DbContext. Fault injection (a throwing audit writer or interceptor) wraps the real context and database, it does not replace them.

### Acceptance criteria

| AC | Proof |
| --- | --- |
| AC-02-1 (create with name, currency, optional opening balance; balance in a list) | Story 1 and Story 2 rows below: `The_owner_creates_an_account_with_an_opening_balance` (LT-I new, AT-I new), `Minor_units_follow_the_currencys_own_exponent` (LT-I new, AT-I new, LT-U new), `The_opening_balance_is_optional` (LT-I new, AT-I new), the invalid-input matrix (LT-U new, AT-I new), `LedgerSchemaIntegrationTests` (LT-I), `LedgerOpenApiContractTests` (CT) |
| AC-02-2 (limit of 2, third refused with a message naming the limit, shown in the list) | `A_third_account_is_refused_and_the_space_still_holds_exactly_2` (LT-I new), `A_third_account_is_refused_with_a_message_that_names_the_limit` (AT-I new), `LedgerAccountLimitRaceTests` (LT-I), `The_list_shows_accounts_balances_and_the_limit_use` (LT-I new, AT-I new) |
| AC-02-3 (rename; currency locked once a transaction exists) | Story 5 rows below: `The_owner_renames_an_account` (LT-I new), `The_currency_cannot_change_once_the_account_holds_a_transaction` (LT-I new, through the EP-04 seam), `A_rename_of_a_locked_account_is_still_allowed` (LT-I new), `The_currency_cannot_change_once_the_account_holds_a_transaction_and_nothing_else_changes` (LT-U new), `A_currency_change_on_an_account_that_holds_a_transaction_is_refused_and_writes_no_audit_record` (LT-I) |
| AC-02-4 (balance = opening balance + transactions) | `The_balance_is_the_opening_balance_plus_the_accounts_transactions` (LT-I new), `The_balance_adds_the_transactions_once_EP_04_provides_them` (LT-U new): 100000 - 2500 + 10000 = 107500. The transactions arrive with EP-04; until then they are supplied through the `IAccountActivity` seam |
| AC-02-5 (limit enforced by the server in the write's transaction, one fixed value, parallel-safe) | `Parallel_creates_never_pass_the_limit_from_empty`, `..._from_one_account`, `Parallel_creates_at_the_limit_stay_at_the_limit` (LT-I, 16 parallel creates, 20 rounds each), the lock-wait test, `The_limit_is_the_constant_2_and_the_only_member_of_its_type` (LT-U new), `A_Pro_trial_or_an_admin_override_does_not_lift_the_limit_before_EP_03` (AT-I new) |

### Story 1: create an account (AC-02-1)

| Scenario | Test (class.method) | Project, trait | Ran |
| --- | --- | --- | --- |
| The owner creates an account with an opening balance | `LedgerAccountScenarioTests.The_owner_creates_an_account_with_an_opening_balance` (stored `125000/EUR/2026-11-01`); `LedgerEndpointPostgresTests.The_owner_creates_an_account_with_an_opening_balance` (201, Location, no-store, returned fields, stored bigint and char(3), one audit record); `LedgerSchemaIntegrationTests.The_money_columns_are_exactly_a_bigint_amount_in_minor_units_and_a_char_3_currency` | LT-I new; AT-I new; LT-I | LT-I schema: local; new: orchestrator and CI |
| Minor units follow the currency's own exponent (JPY 5000, EUR 125000, KWD 1500000) | `LedgerAccountScenarioTests.Minor_units_follow_the_currencys_own_exponent` (Theory, JPY, EUR, KWD, negative EUR); `LedgerEndpointPostgresTests.Minor_units_follow_the_currencys_own_exponent`; `LedgerAccountDomainTests.Minor_units_follow_the_currencys_own_exponent`; `LedgerInputValidationTests.Minor_units_follow_the_currencys_own_exponent` | LT-I new; AT-I new; LT-U new | not yet run |
| The opening balance is optional | `LedgerAccountScenarioTests.The_opening_balance_is_optional`; `LedgerEndpointPostgresTests.The_opening_balance_is_optional`; `LedgerAccountDomainTests.The_opening_balance_is_optional_and_absent_means_zero_in_the_accounts_currency` | LT-I new; AT-I new; LT-U new | not yet run |
| Invalid input is refused with a generic validation error and nothing is stored (10 examples) | `LedgerEndpointPostgresTests.Invalid_input_is_refused_with_a_generic_validation_error_and_nothing_is_stored` (Theory over 15 bodies: empty name, 81 characters, `EURO`, `XXX`, `eur`, balance without date, date without balance, 2^53 (amount range), 2^63 (bigint overflow), `12.5`, string amount, `iban`, `card_number`, `tenantId`, not JSON; asserts 400, the stable code, a body with only `type, title, status, traceId, code`, the refused value never echoed, 0 accounts, 0 audit records); `A_request_that_is_not_JSON_is_refused_and_nothing_is_stored` (Theory over POST /accounts, PUT …/name and PUT …/currency, each with `text/plain` and with no content type: 400, code `ledger.body_invalid`, only `type, title, status, traceId, code`, `Cache-Control: no-store`, account unchanged, no audit record); `A_request_with_no_token_and_a_wrong_content_type_is_refused_as_unauthenticated_and_nothing_is_stored` (401, authentication before the content-type check, nothing stored); at shape level `LedgerInputValidationTests` (name, currency, opening-pair, range, date, mismatch methods) and the database checks in `LedgerSchemaIntegrationTests.The_database_refuses_an_account_row_that_breaks_a_check` | AT-I new; LT-U new; LT-I | not yet run (the schema test: local) |
| A member cannot create an account | `LedgerEndpointPostgresTests.A_member_cannot_create_an_account` (403, 0 accounts, 0 audit records; the member is a seeded Member row) | AT-I new | not yet run |
| A request without antiforgery is refused | `LedgerApiAntiforgeryTests.A_ledger_write_without_the_antiforgery_header_is_refused_and_the_api_receives_nothing` (POST and both PUTs; 403 at the BFF, the Api double receives nothing, so nothing is stored) and `A_ledger_write_with_a_valid_antiforgery_pair_is_forwarded_with_the_bearer_token_only`. The Api accepts bearer tokens only; the check lives at the BFF (G2 "Antiforgery") | BT-I new | CI |

### Story 2: see the accounts and the limit use (AC-02-1, AC-02-2, AC-02-4)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| The list shows accounts, balances and the limit use | `LedgerAccountScenarioTests.The_list_shows_accounts_balances_and_the_limit_use` (EUR 125000, JPY 5000, no conversion, used 2 limit 2); `LedgerEndpointPostgresTests.The_list_shows_accounts_balances_and_the_limit_use_and_a_member_reads_but_the_roles_are_not_mixed_up` (read as a member) | LT-I new; AT-I new | not yet run |
| The balance is the opening balance plus the account's transactions (no transaction) | `LedgerAccountScenarioTests.The_balance_is_the_opening_balance_plus_the_accounts_transactions` (first half); `LedgerAccountDomainTests.The_balance_is_the_opening_balance_when_the_account_holds_no_transaction` | LT-I new; LT-U new | not yet run |
| The balance adds the transactions once EP-04 provides them (107500) | `LedgerAccountScenarioTests.The_balance_is_the_opening_balance_plus_the_accounts_transactions` (second half, `NetActivity` seam); `LedgerAccountDomainTests.The_balance_adds_the_transactions_once_EP_04_provides_them`; `LedgerAccountDomainTests.A_balance_is_never_converted_or_summed_across_currencies` | LT-I new; LT-U new | not yet run. The seam is the specified stand-in (Out of scope: transactions) |
| A member reads but the roles are not mixed up | `LedgerEndpointPostgresTests.The_list_shows_accounts_balances_and_the_limit_use_and_a_member_reads_but_the_roles_are_not_mixed_up` (list and single read as a member; the account has no `tenantId`, `role`, `permissions` or `canCreate` member) | AT-I new | not yet run |
| An empty space lists no account and shows the limit | `LedgerAccountScenarioTests.An_empty_space_lists_no_account_and_shows_the_limit`; `LedgerEndpointPostgresTests.An_empty_space_lists_no_account_and_shows_the_limit` | LT-I new; AT-I new | not yet run |
| The response is not cached | `LedgerEndpointPostgresTests` asserts `Cache-Control: no-store` on the create (201), the list and the single read | AT-I new | not yet run |

### Story 3: refuse a third account (AC-02-2)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| A third account is refused with a message that names the limit | `LedgerEndpointPostgresTests.A_third_account_is_refused_with_a_message_that_names_the_limit` (409, code `ledger.account_limit_reached`, `limit` 2, detail names 2, still 2 accounts, no extra audit record); `LedgerAccountScenarioTests.A_third_account_is_refused_and_the_space_still_holds_exactly_2`; `LedgerAuditTests.Refusals_write_no_audit_record` | AT-I new; LT-I new; LT-I | LedgerAuditTests: local |
| The limit counts the space's accounts only | `LedgerEndpointPostgresTests.The_limit_counts_the_spaces_accounts_only`; `LedgerIsolationTests.Another_spaces_accounts_never_count_against_the_limit_and_creates_never_touch_its_lock_row` | AT-I new; LT-I | LT-I: local |

### Story 4: the limit holds under concurrency, from one value in code (AC-02-5)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| Parallel creates never pass the limit from empty (2 succeed, 14 refused, 2 accounts, 2 audit records, no 5xx or exception) | `LedgerAccountLimitRaceTests.Parallel_creates_never_pass_the_limit_from_empty` (16 parallel creates behind a gate, real `decisya_ledger` role) | LT-I | local (Ledger 39 green) |
| ... from one account | `LedgerAccountLimitRaceTests.Parallel_creates_never_pass_the_limit_from_one_account` | LT-I | local |
| ... at the limit | `LedgerAccountLimitRaceTests.Parallel_creates_at_the_limit_stay_at_the_limit` | LT-I | local |
| The race test repeats (20 rounds on fresh spaces) | the three tests above loop 20 rounds (`Rounds = 20`) on fresh tenants and assert the count, the audit rows, the book's `lock_version` each round; `A_create_waits_for_the_spaces_lock_row_then_sees_the_account_the_holder_committed_while_another_space_does_not_wait` is the deterministic lock proof | LT-I | local |
| The limit is a fixed value in code | `LedgerAccountDomainTests.The_limit_is_the_constant_2_and_the_only_member_of_its_type`; `No_configuration_option_plan_or_entitlement_is_read_by_the_ledger_domain_or_application_code`; `The_create_handler_takes_no_configuration_options_or_entitlement_service` | LT-U new, LT-U new, LT-U new | not yet run. A "set a configuration value named like a limit" test is not written: with no code that reads configuration (proven by the NetArchTest rule above) it would pass trivially |
| A Pro trial or an admin override does not lift the limit before EP-03 | `LedgerEndpointPostgresTests.A_Pro_trial_or_an_admin_override_does_not_lift_the_limit_before_EP_03` (a real trial and a real override through the admin endpoints, then the third create is still 409) | AT-I new | not yet run |
| No other mechanism exists | `LedgerAccountDomainTests.The_plan_catalogue_holds_no_unlimited_accounts_key_after_this_issue` (`FeatureKeys.All` is exactly the two keys) and the NetArchTest rule above (nothing in Domain or Application reads a plan, key or setting); `EntitlementMarkerTests` (Api.Tests, Unit; the trait was missing and is added at G5) pins the ledger group to the existing `Entitlement:ledger.transactions` policy | LT-U new; Api.Tests Unit | not yet run (EntitlementMarkerTests: green in the 2147 Unit run) |

### Story 5: rename an account and lock its currency (AC-02-3)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| The owner renames an account | `LedgerAccountScenarioTests.The_owner_renames_an_account` (name, currency, opening balance and balance unchanged, exactly one audit record); `LedgerEndpointPostgresTests.The_owner_renames_an_account_and_a_member_cannot`; `LedgerAccountDomainTests.A_rename_changes_only_the_name`; `LedgerAuditTests.A_create_a_rename_and_a_currency_change_each_write_one_audit_record_in_the_same_transaction` | LT-I new; AT-I new; LT-U new; LT-I | LedgerAuditTests: local |
| A rename with an invalid name is refused | `LedgerEndpointPostgresTests.A_rename_with_an_invalid_name_is_refused_and_the_name_is_unchanged` (empty, blank, 81 characters, missing member; name and audit count unchanged); `LedgerInputValidationTests.An_empty_blank_null_or_control_character_name_is_refused` and `A_lone_surrogate_name_is_refused` (strings built in code, each proven to hold a lone surrogate) and `A_name_of_80_characters_is_accepted_and_a_name_of_81_characters_is_refused` | AT-I new; LT-U new | not yet run |
| The currency can change while the account holds no transaction | `LedgerAccountScenarioTests.The_currency_can_change_while_the_account_holds_no_transaction` (EUR 125000 to JPY 5000, one audit record); `LedgerEndpointPostgresTests.The_currency_can_change_while_the_account_holds_no_transaction_and_must_restate_the_opening_balance` (final step); `LedgerAccountDomainTests.The_currency_changes_while_the_account_holds_no_transaction_and_restates_the_opening_balance` | LT-I new; AT-I new; LT-U new | not yet run |
| A currency change must restate the opening balance | `LedgerEndpointPostgresTests.The_currency_can_change_while_the_account_holds_no_transaction_and_must_restate_the_opening_balance` (the members absent: 400; a balance in the old currency: 400; same currency: `ledger.currency_unchanged`; the account and the audit count unchanged) | AT-I new | not yet run |
| The currency cannot change once the account holds a transaction | `LedgerAccountScenarioTests.The_currency_cannot_change_once_the_account_holds_a_transaction` (outcome `CurrencyLocked`, still EUR, no audit record, `currencyLocked` true on read); `LedgerAuditTests.A_currency_change_on_an_account_that_holds_a_transaction_is_refused_and_writes_no_audit_record`; `LedgerAccountDomainTests.The_currency_cannot_change_once_the_account_holds_a_transaction_and_nothing_else_changes`. The 409 `ledger.currency_locked` over HTTP is **manual until EP-04**: no HTTP-reachable way to make an account hold a transaction exists before EP-04; the mapping of `CurrencyLocked` to 409 is pinned by the committed OpenAPI document (`LedgerOpenApiContractTests`, CT) | LT-I new, LT-I, LT-U new; CT | LT-I LedgerAuditTests and CT: local |
| A rename of a locked account is still allowed | `LedgerAccountScenarioTests.A_rename_of_a_locked_account_is_still_allowed`; `LedgerAccountDomainTests.A_rename_of_a_locked_account_is_still_allowed_and_its_currency_stays` | LT-I new; LT-U new | not yet run |
| A member cannot rename or change an account | `LedgerEndpointPostgresTests.The_owner_renames_an_account_and_a_member_cannot` (403, name unchanged); `LedgerEndpointPostgresTests.A_member_cannot_change_an_accounts_currency` | AT-I new | not yet run |

### Story 6: isolation and object-level authorisation (invariant 1, BOLA; NFR-80)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| A request for another space's account is indistinguishable from a missing one | `LedgerIsolationTests.A_request_for_another_spaces_account_is_indistinguishable_from_a_missing_one` (read, rename and currency change by id, same outcome and same result value, account and audit unchanged); `LedgerEndpointPostgresTests.A_request_for_another_spaces_account_is_indistinguishable_from_a_missing_one` (GET, PUT name, PUT currency, a malformed id: all 404 with the same body but the trace id) | LT-I; AT-I new | LT-I: local |
| A list shows only the caller's own space | `LedgerIsolationTests.A_list_shows_only_the_callers_own_space`; `LedgerEndpointPostgresTests.A_list_shows_only_the_callers_own_space` (1 account, used 1 of limit 2) | LT-I; AT-I new | LT-I: local |
| No tenant means no rows | `LedgerIsolationTests.No_tenant_means_no_rows_and_no_query_reaches_the_ledger_tables` (command-counting interceptor: zero commands for `NoTenant` and the invalid default); `LedgerEndpointPostgresTests.A_caller_with_no_tenant_or_no_membership_is_refused_before_any_ledger_query` | LT-I; AT-I new | LT-I: local |
| A caller who is not a member of the resolved tenant is refused | `LedgerEndpointPostgresTests.A_caller_with_no_tenant_or_no_membership_is_refused_before_any_ledger_query` (list and create by a stranger: 403; the middleware's own tests are in the Api Integration lane); `LedgerEndpointPostgresTests.A_request_without_a_token_is_refused` (401) | AT-I new | not yet run |
| Concurrent changes never leak detail | `LedgerAuditTests.Two_simultaneous_renames_both_succeed_and_each_leaves_one_audit_record` (two simultaneous renames: both succeed, the last writer wins, no conflict leaks); the generic not-found body is `A_request_for_another_spaces_account_is_indistinguishable_from_a_missing_one` above. The design has no concurrency token on rename, so no conflict response exists to test | LT-I | local |
| Two-tenant isolation at the model level (the isolation-test skill) | `LedgerIsolationTests`: `Tenant_A_cannot_read_tenant_Bs_rows_through_the_global_query_filter`, `Tenant_A_cannot_update_or_delete_tenant_Bs_rows_by_id`, `Tenant_A_cannot_save_a_row_that_belongs_to_tenant_B`, `The_module_persists_only_through_ITenantScoped_aggregates_in_the_ledger_schema`; ArT `ModuleBoundaryTests.TenantModelRule_...`, `TenantIdImmutabilityRule_...`, `CrossTenantQueryRule_...`, `BulkTenantMoveRule_...` over the architecture scope that includes the Ledger assembly | LT-I; ArT | local |

### Story 7: every write is audited (ADR-0013 amendment 1; NFR-83)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| A create, a rename and a currency change each write one audit record in the same transaction | `LedgerAuditTests.A_create_a_rename_and_a_currency_change_each_write_one_audit_record_in_the_same_transaction` (3 records, closed action set, account id as subject, tenant, caller id, time from `IClock`, trace id, outcome, no feature key; a scan of every column finds neither the name nor the amount canary); `LedgerAuditTests.Audit_records_are_per_space_and_a_write_in_one_space_leaves_none_in_another`; `LedgerEndpointPostgresTests.Each_successful_write_leaves_one_audit_record_with_the_account_id_and_no_name_or_amount` (same through the real host); the database checks `LedgerAuditTests.The_database_refuses_a_malformed_audit_row_inserted_with_raw_SQL_as_the_owner_and_as_decisya_ledger`, `The_database_accepts_the_well_formed_audit_rows_of_every_action`, `The_audit_migration_adds_the_subject_column_and_the_three_checks`; AuT `AuditModuleBoundaryTests` pins the closed `AuditAction` set including the three ledger codes; `EntitlementsAuditTests.The_override_reason_never_reaches_the_audit_record` pins the audit column list including `subject_id` as `uuid` | LT-I; AT-I new; AuT; Entitlements Integration | LT-I and Entitlements: local |
| A failed audit write rolls the account write back | `LedgerAuditTests.A_failed_audit_write_rolls_the_account_write_back_for_a_create`; `An_audit_append_the_database_refuses_rolls_the_account_write_back_for_a_create` (REVOKE INSERT: real `42501`); `A_commit_that_fails_after_the_audit_append_leaves_no_account_and_no_record_for_a_create`; `A_failed_audit_write_rolls_the_rename_back_and_a_failed_currency_change_too` | LT-I | local |
| A failed account write leaves no audit record | `LedgerAuditTests.A_failed_account_write_leaves_no_audit_record_for_a_rename_and_a_currency_change` | LT-I | local |
| Refusals write no audit record | `LedgerAuditTests.Refusals_write_no_audit_record` (limit, not found, currency unchanged, forbidden) and `A_currency_change_on_an_account_that_holds_a_transaction_is_refused_and_writes_no_audit_record` (locked); at HTTP the invalid, forbidden and limit cases assert an unchanged audit count in `LedgerEndpointPostgresTests` | LT-I; AT-I new | LT-I: local |

### Story 8: the ledger module is a clean module (NFR-84, NFR-86)

| Scenario | Test | Project, trait | Ran |
| --- | --- | --- | --- |
| The module follows the boundary rules | `LedgerModuleBoundaryTests.The_Ledger_module_references_no_other_modules_implementation`; `The_Ledger_module_never_reads_the_system_clock_directly`; `The_Ledger_module_and_its_contracts_reference_no_vendor_AI_SDK` (new); the repository-wide ArT boundary rules and their deliberate-violation fixtures (`ModuleBoundaryTests`, `TenancyRuleTests`, `AuditRuleTests` including `ExplicitTransactionRule_...` and `IsolationLevelRule_...`) run over a scope that includes `Decisya.Modules.Ledger` (`ArchitectureScope`); `IntegrationCategoryTraitGuardTests.Every_class_that_takes_the_Postgres_fixture_carries_the_Integration_category_trait` (Ledger) is the marker guard | LT-A, ArT, LT-U | Unit lane: green on `409afec` (the new AI-SDK method: not yet run) |
| The schema follows the rules | `LedgerSchemaIntegrationTests.The_migration_creates_exactly_the_two_ledger_tables_and_the_history_table_in_the_ledger_schema`, `Every_ledger_table_has_tenant_id_uuid_not_null_and_an_index_that_starts_with_it` (Theory), `The_money_columns_are_exactly_a_bigint_amount_in_minor_units_and_a_char_3_currency`, `The_named_checks_and_indexes_exist`; `LedgerIsolationTests.The_module_persists_only_through_ITenantScoped_aggregates_in_the_ledger_schema`; `LedgerOpenApiContractTests.The_generated_ledger_document_deep_equals_the_committed_openapi_ledger_json` | LT-I; CT | local |
| The API's database role is least-privileged | `LedgerRoleIntegrationTests`: `Decisya_ledger_holds_exactly_SELECT_INSERT_and_UPDATE_on_the_ledger_tables_and_the_same_after_a_second_RunAsync`, `Decisya_ledger_can_select_insert_and_update_but_cannot_delete_truncate_or_run_any_DDL`, `Decisya_ledger_is_an_audit_writer_with_exactly_USAGE_on_the_schema_and_INSERT_on_the_table`, `Decisya_ledger_cannot_read_or_write_the_tenancy_or_entitlements_schema_and_they_cannot_reach_ledger`, `Decisya_ledger_carries_no_elevated_attribute_no_role_membership_no_MAINTAIN_and_its_own_SCRAM_verifier`, `A_run_that_finds_the_ledger_grants_widened_narrows_them_again_and_never_widens_anything` | MT-I | local (Migrator 34 green) |
| No IBAN or card field exists | `LedgerSchemaIntegrationTests.No_column_or_table_in_the_ledger_schema_is_an_IBAN_a_card_number_or_an_account_number` (exact column list); at the API, the `iban`, `card_number` and `tenantId` bodies of `LedgerEndpointPostgresTests.Invalid_input_is_refused_with_a_generic_validation_error_and_nothing_is_stored` are refused, and the contract test pins the closed request shapes | LT-I; AT-I new; CT | LT-I and CT: local |

### Contract tests (the issue's "Done when")

`LedgerOpenApiContractTests.The_generated_ledger_document_deep_equals_the_committed_openapi_ledger_json` (CT, Unit lane, green on `409afec`) pins every request and response shape of Stories 1 to 6. `OpenApiRouteTests` (Api.Tests, Unit) pins that no route serves the document; `EntitlementMarkerTests` pins the entitlement policy on the ledger group; `HealthEndpointTests` pins the five ledger routes.

### Check of the CLAUDE.md test rules for this issue's tests

- AwesomeAssertions only: no `FluentAssertions` reference in any Ledger, Migrator, Contract or new Api or Bff test file.
- Every test class has `[Trait("Category", ...)]`: Unit (`LedgerInputValidationTests`, `LedgerAccountDomainTests`), Architecture (`LedgerModuleBoundaryTests`), Integration (all Postgres, Api-host and BFF classes), Contract (`LedgerOpenApiContractTests`). The Ledger project's `IntegrationCategoryTraitGuardTests` fails when a fixture-taking class lacks the Integration trait.
- Integration tests use real Postgres (Testcontainers) or the real Api or BFF host; none mocks a DbContext. The fault-injection seams decorate the real writer or add interceptors to the real context.
- Two-tenant isolation tests exist for the tenant-scoped feature at three layers: the model (`LedgerIsolationTests`), the handlers and the HTTP host (`LedgerEndpointPostgresTests`), and the audit (`Audit_records_are_per_space_...`).
- Negative tests for the input and authorisation paths: invalid shapes, wrong content type, unknown members, a member, a stranger, no tenant, no token, a malformed id. Token-level negatives (expired, wrong audience, `alg=none`) are the existing Api authentication suite's; this issue adds no authentication code.

### G3 MUSTs and SHOULDs, and the tests the ADRs cite (added after the G6 review, G6-341-01 to -07)

Written after G6 and **not yet run** unless the Ran column says otherwise; the orchestrator builds and runs them. "Missing" means no test proves the item yet. Abbreviations as above; AT-U is `Decisya.Api.Tests` in the Unit lane, SKT is `Decisya.SharedKernel.Tests`.

| Item | Test (class.method) | Project, trait | State |
| --- | --- | --- | --- |
| M341-01 (a) 404 for missing, foreign and malformed ids; no bypass | `LedgerIsolationTests`; `LedgerEndpointPostgresTests.A_request_for_another_spaces_account_is_indistinguishable_from_a_missing_one`; ArT `CrossTenantQueryRule_...` over the scope | LT-I; AT-I; ArT | written at G5 |
| M341-01 (b) `Ledger.Owner` on every write | `LedgerEndpointPostgresTests.A_member_cannot_create_an_account`, `The_owner_renames_an_account_and_a_member_cannot`, `A_member_cannot_change_an_accounts_currency` (403, reachable database); metadata: `EntitlementMarkerTests.Every_ledger_write_carries_the_owner_policy_and_no_ledger_GET_does` (new, R-L13) | AT-I; AT-U new | the proof on an unreachable-database host for each write is **still missing** (the 403 tests use a reachable database) |
| M341-01 (c) combined policy holds the live fallback | `EntitlementMarkerTests.Every_ledger_endpoints_combined_policy_holds_the_live_fallback_policy_and_the_entitlement_requirement` and `Every_admin_endpoints_combined_policy_holds_the_live_fallback_policy_and_the_MFA_requirement` (G6-341-03) | AT-U new | new. For `Api.AdminMfa` the schemes are not required: its hand-built policy names none and Bearer reaches it as the one default scheme; the requirement types and claims are compared with the live fallback |
| M341-01 (d) `ICallerTenantRole` deny-by-default; `TenantMemberRole` mirrors `TenantRole` (R-L19) | none | Tenancy | **missing**; backlog G6-341-08 (#243) |
| M341-01 (e) two-tenant matrix incl. usage per tenant | `LedgerIsolationTests`; `LedgerEndpointPostgresTests.A_list_shows_only_the_callers_own_space`, `The_limit_counts_the_spaces_accounts_only` | LT-I; AT-I | written at G5 |
| M341-02 (a), (b) READ COMMITTED, no retry, `locked != 1` is a 500, 23505 matched by name | `AuditRuleTests.IsolationLevelRule_...` with its red fixture; `ModuleBoundaryTests.IsolationLevelRule_passes_over_the_current_architecture_scope`; `LedgerAccountLimitRaceTests.A_create_waits_for_the_spaces_lock_row_...` | ArT; LT-I | green or local |
| M341-02 (c) exactly one `ExecuteUpdate(Async)` site, no `ExecuteDelete` | `BulkStatementCallSiteRuleTests.The_Ledger_module_has_exactly_one_ExecuteUpdate_call_site_the_book_lock_and_no_ExecuteDelete`; red: `A_second_ExecuteUpdate_call_site_fails_the_rule`, `An_ExecuteDelete_call_site_fails_the_rule`, `A_synchronous_ExecuteUpdate_inside_a_lambda_is_found_...`, `A_moved_site_fails_the_rule_...`; green: `Exactly_the_one_permitted_site_and_no_delete_passes_the_rule` (G6-341-05 (c)) | ArT new | new |
| M341-02 (d) race matrix 0, 1, 2 existing, 16-way, 20 rounds; one round over HTTP | `LedgerAccountLimitRaceTests` (3 scenarios, handler level, local); `LedgerEndpointPostgresTests.Parallel_creates_through_the_real_host_never_pass_the_limit_and_never_return_a_5xx` (G6-341-05 (d); 16 parallel creates for 0, 1 and 2 existing; a fresh owner for 0 also exercises concurrent JIT provisioning) | LT-I; AT-I new | HTTP round new |
| M341-03 (a) writer shape and ambient checks before any SQL (R-L18) | `AuditWriterTests.A_ledger_record_is_refused_when_the_ambient_tenant_is_None_before_any_SQL`, `..._is_Invalid_...`, `..._is_another_tenant_...`, `A_ledger_record_without_a_subject_id_or_with_an_empty_one_is_refused_before_any_SQL`, `A_ledger_record_that_carries_a_feature_key_is_refused_before_any_SQL`, `An_Entitlements_record_that_carries_a_subject_id_is_refused_before_any_SQL`, `A_valid_ledger_record_under_its_own_tenant_passes_the_guard_and_reaches_the_database`, `The_Entitlements_family_keeps_its_ambient_rule_None_or_the_matching_tenant`, and (Integration) `A_valid_ledger_record_under_its_own_tenant_is_stored_with_the_subject_id_and_the_actor` (G6-341-01) | AuT new (Unit; one Integration) | new |
| M341-03 (b) three database checks | `LedgerAuditTests.The_database_refuses_a_malformed_audit_row_inserted_with_raw_SQL_as_the_owner_and_as_decisya_ledger`, `The_database_accepts_the_well_formed_audit_rows_of_every_action`, `The_audit_migration_adds_the_subject_column_and_the_three_checks` | LT-I | local |
| M341-03 (c) `decisya_ledger` in both grant lists | `AuditGrantsIntegrationTests`; `LedgerRoleIntegrationTests.Decisya_ledger_is_an_audit_writer_with_exactly_USAGE_on_the_schema_and_INSERT_on_the_table` | MT-I | local |
| M341-03 (d) no name or amount in the record; fault matrix as `decisya_ledger` | `AuditModuleBoundaryTests.AuditEntry_is_exactly_TenantId_Action_FeatureKey_and_SubjectId_with_no_actor_time_trace_or_outcome_member`; `LedgerAuditTests` fault matrix | AuT; LT-I | local |
| M341-04 (a) to (e) role, secret path, DDL refused | `LedgerRoleIntegrationTests` (all six methods; `CREATE TABLE public.*` and `CREATE TEMP TABLE` inside the DDL case); `TenancyMigratorResourceTests` (AppHost parameter and variables); `deploy/tests/test_stackctl.py`, `test_keyring.py` (`ledger_db`) | MT-I; AppHost; deploy | local |
| M341-05 (a) options values | `LedgerJsonOptionsTests` (all members; the six refusing bodies; G6-341-06 (a)) | LT-U new | new |
| M341-05 (b), (c) order: content type and charset, limit, parse; ordinal currency | `A_request_that_is_not_JSON_is_refused_and_nothing_is_stored`; `A_request_with_no_token_and_a_wrong_content_type_is_refused_as_unauthenticated_...`; `Invalid_input_is_refused_with_a_generic_validation_error_...` (`eur`, `EURO`, `XXX`); `LedgerInputValidationTests` | AT-I; LT-U | written at G5 |
| M341-05 (d) the ADR-0027 and G3 input list | `Invalid_input_is_refused_with_a_generic_validation_error_and_nothing_is_stored`, 11 rows added (`1e3`, `125000.0`, `-(2^53)`, a null amount, a missing currency, a missing opening currency, `"name"` plus `"Name"`, `currencyLocked`, `\u0000`, the escaped lone surrogate `\ud800`, a 5000-character name); `A_body_over_4_KiB_with_no_Content_Length_is_refused_and_nothing_is_stored` (Theory, three writes); `A_small_body_with_no_Content_Length_is_refused_when_the_host_cannot_enforce_the_limit`; `A_charset_other_than_utf_8_is_refused_before_the_body_is_read_and_nothing_is_stored`; `An_explicit_utf_8_charset_is_accepted`. `12.5`, `"125000"`, 2^53, 2^63, `tenantId`, `iban` and `card_number` were already rows | AT-I new | new |
| M341-06 (a), (b) pin, one package, lock file | `ApiBoundaryTests.Decisya_Api_csproj_has_exactly_...` (project references and packages) | AT-U | green in the Unit lane |
| M341-06 (c) no route | `ApiBoundaryTests.No_Decisya_Api_type_references_MapOpenApi`; `OpenApiRouteTests.In_Production_no_OpenAPI_document_route_exists_and_an_authenticated_caller_gets_404` | AT-U | green in the Unit lane |
| F341-01 each module appends only its own action family (R-L21) | `AuditActionFamilyScanTests.Over_the_real_source_tree_each_module_uses_only_its_own_audit_action_family`; red: `A_red_fixture_that_appends_another_modules_action_fails_the_scan` (7 rows), `A_using_static_of_AuditAction_outside_the_Audit_module_fails_the_scan`; green: `Each_module_may_use_its_own_family_and_the_Audit_module_may_use_all_of_them` (G6-341-04). The red fixtures are in-memory files at repository-relative paths, not files under `src/` | ArT new | new |
| F341-02 canaries on the success, refusal and failure paths | `LedgerEndpointPostgresTests.The_account_name_and_the_opening_amount_never_reach_a_log_a_span_a_metric_tag_or_a_problem_body` (`MARKER-7c1e`, `918273645`; create, rename, currency change, list, read; an invalid name, the limit, an unchanged currency, a missing id, a refused body whose unknown member is the canary; a failing audit append; a failing `SaveChanges` through a check constraint whose Postgres error would carry the row; scans log message, state, scopes and exception text, span names, tags and events, every metric tag, and the problem bodies; the Ledger meter's tags are exactly `decisya.ledger.operation`, `result` and `decisya.ledger.reason` with bounded values and no account id or tenant id) (G6-341-02) | AT-I new | new. The static catalogue entry for `AccountName` is the architect's, not a test |
| F341-03 header of the audit migration | none (a comment; read at G6, which passed it) | n/a | manual |
| F341-04 no tenant, actor, role or permission member in a response | `LedgerEndpointPostgresTests.The_owner_creates_an_account_with_an_opening_balance` and `The_list_shows_accounts_balances_and_the_limit_use_and_a_member_reads_but_the_roles_are_not_mixed_up`; `LedgerOpenApiContractTests` | AT-I; CT | written at G5 |
| S341-01 to -03 | none, by decision: backlog (#243) | n/a | not in this issue |
| ADR-0027 "Enforced by": the `Money` schema equals points 1 and 2 | `LedgerOpenApiContractTests.The_generated_ledger_document_deep_equals_the_committed_openapi_ledger_json` | CT | local |
| ADR-0027 "Enforced by": contract cases `12.5`, `1e3`, `"125000"`, 2^53, `-(2^53)`, `"eur"`, `"XXX"`, a missing currency; JPY, EUR and KWD round-trip | `LedgerEndpointPostgresTests.Invalid_input_is_refused_...` (all rows above) and `Minor_units_follow_the_currencys_own_exponent` | AT-I | rows new |
| ADR-0027 "Enforced by": property round trip of at least 10 000 cases over exponents 0, 2, 3, 4, and the point 5 exceptions (NFR-82) | `MoneyMinorUnitsPropertyTests.FromMinorUnits_and_ToMinorUnits_round_trip_exactly_across_ten_thousand_seeded_cases_and_every_exponent` (seed 20261122, both directions, ±(2^53 - 1) with the edges); `FromMinorUnits_refuses_default_Currency_with_an_ArgumentException`; `ToMinorUnits_of_default_Money_throws_InvalidOperationException`; `ToMinorUnits_throws_OverflowException_when_the_amount_does_not_fit_a_long`. No property-testing package is added: a seeded `System.Random` loop, as `MoneyAllocationPropertyTests` | SKT, Unit, new | new |
| ADR-0027 "Enforced by": the analyzer ban on `double` conversions | existing `BannedSymbols.txt` and `BannedApiCompilationTests` | SKT | green |
| ADR-0027 "Enforced by": NFR-07 coverage at or above 95 % | not measured by this issue's tests | n/a | **missing** evidence; the coverage run is the orchestrator's |
| ADR-0013 amendment 1 "Enforced by": audit writer shape and ambient matrix | the `AuditWriterTests` rows above | AuT new | new |
| ADR-0013 amendment 1 "Enforced by": `AuditModelTests` for the new checks | `AuditModelAndRegistrationTests` (subject column and the checks) and `LedgerAuditTests.The_audit_migration_adds_the_subject_column_and_the_three_checks`. No class named `AuditModelTests` exists | AuT; LT-I | local |
| ADR-0013 amendment 1 "Enforced by": `AuditGrantsIntegrationTests` with `decisya_ledger` | `AuditGrantsIntegrationTests`; `LedgerRoleIntegrationTests` | MT-I | local |
| ADR-0013 amendment 1 "Enforced by": the Ledger fault matrix (NFR-83) | `LedgerAuditTests` (`A_failed_audit_write_rolls_the_account_write_back_for_a_create`, `An_audit_append_the_database_refuses_...`, `A_commit_that_fails_after_the_audit_append_...`, `A_failed_audit_write_rolls_the_rename_back_and_a_failed_currency_change_too`, `A_failed_account_write_leaves_no_audit_record_...`) | LT-I | local |
| ADR-0013 amendment 1 point 2 "source scan, rule R-L21" | `AuditActionFamilyScanTests` (the F341-01 row) | ArT new | new |

### Gaps and notes

0. After G6: the rows marked "new" above (G6-341-01 to -07) are not yet run. Still missing: the unreachable-database proof of M341-01 (b), R-L19 (G6-341-08, backlog), the NFR-07 coverage figure, and the static catalogue entry for `AccountName` (architect).
1. All tests marked "new" were written at G5 and have not run. The orchestrator runs: `Decisya.Modules.Ledger.Tests` (Unit and Integration), `Decisya.Api.Tests` (the Integration class `LedgerEndpointPostgresTests` needs only Postgres; the project's other Integration tests need Keycloak) and `Decisya.Bff.Tests` (needs Keycloak and Redis; CI). A failure in one of them reopens G5.
2. Manual until EP-04: the HTTP 409 `ledger.currency_locked` (no way to create a transaction yet; the lock itself is proven through the `IAccountActivity` seam at handler and domain level) and the balance with real transactions (AC-02-4 proven through the same seam).
3. Not written, with reason: a test that sets a configuration value named like a limit and sees no effect (trivial once the NetArchTest rule proves no configuration is read); a Story 7 fault test at HTTP level (the handler-level fault matrix runs the same code against the real database).
4. Antiforgery lives at the BFF, not the Api: the proof is in `Decisya.Bff.Tests`, as G2 specifies.
5. G1 Story 6's "concurrent changes" scenario assumes a conflict response; the design has last-writer-wins on rename (no concurrency token), so the test pins that both succeed and neither leaks.

<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #341 -->

## Out of scope

- Transactions themselves (EP-04). The balance rule is specified here; the test that adds transactions uses a seam until EP-04 ships.
- EP-03's Pro key, its check, and the Pro-ended case (EP-03#1 to EP-03#3).
- The SPA screens (#342), onboarding's first-account step (EP-01#2, which consumes this API), and its axe and keyboard checks.
- Household invitations and the Member role's assignment (EP-10; no invite path exists). A member is a seeded row in tests.
- Deleting, closing or archiving an account (the plan has no such story; EP-12 covers account closure as a whole).
- Currency conversion, a total across accounts, IBAN, card or bank-link fields.
- Any AI feature. No financial advice text is produced, so the disclaimer criterion of invariant 6 does not apply.

## Quality targets

New rows NFR-80 to NFR-87 are added to `docs/requirements/nfr.md`: tenant isolation (80), the limit under concurrency (81), the money invariant and the schema (82, 84), audit of every write (83), validation and BOLA (85), module boundaries (86), observability and personal data (87).

## Notes for the owner (no answer needed to proceed)

1. No open question blocks this gate. Assumptions A1 to A5 are stated above; reverse any of them in one line.
2. Observation for the phase backlog (#243), not this issue: with a hard limit of 2 and no delete or archive story, an account created in the wrong currency after a transaction exists keeps its slot until EP-12's closure. Recommendation: leave out of #341 and raise a plan change after EP-04 (risk of deferring: a Free owner who mis-created an account has no way to free the slot; risk of adding it now: a delete story needs a rule for the transactions it holds and widens this PR).
3. Dates: the plan's window (2026-12-15 to 2027-01-16) is provisional until the COND-03 re-plan.

<!-- gate: G1 | verdict: PASS-WITH-NOTES | issue: #341 -->
