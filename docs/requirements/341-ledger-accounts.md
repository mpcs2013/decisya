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
