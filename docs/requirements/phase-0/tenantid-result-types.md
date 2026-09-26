# Phase 0 – Decisya.SharedKernel: TenantId and result types

## Issue 0.04b (#32) — TenantId value type and Result types (remaining scope of #16)

### Scope note (role framing)

`Decisya.SharedKernel` is a foundational library with no tenant-facing surface of its
own — the same framing #16 used for `Money`/`Currency`/`IClock`. `TenantId` and
`Result`/`Result<T>`/`DomainError` are consumed by every module (ledger, budgets, forecasts,
alerts, the future BFF/API auth layer) that *does* act on behalf of a tenant, but
there is no "role in tenant" for a value type. The stories below use **"As a Decisya
module developer"** as the acting role, and each story states which tenant-facing
guarantee it exists to protect, per `CLAUDE.md` platform invariant 1 (every persisted
aggregate carries a `TenantId`; no cross-tenant query bypasses the filter) and the
error-handling security principle ("generic message to the client, full detail to the
structured log with trace id").

**Entitlement plan:** N/A. `Decisya.SharedKernel` is a platform primitive shipped in
every subscription plan; it is not gated by an entitlement feature key. Any future
feature key referenced here would be at the *consuming* module's discretion, not
SharedKernel's.

**Style precedent.** `TenantId` follows the `Currency` shape exactly: a `readonly
struct` with a private constructor, static factory methods that validate before
construction, an `IsInitialized` flag because a struct's implicit parameterless
constructor can never be suppressed, and a dedicated exception whose `Message` never
echoes raw, unbounded or hostile input (see `InvalidCurrencyCodeException`). `Result`
follows `Money`'s precedent of being a small, dependency-free, hand-rolled value type
rather than a new package.

---

## Open questions (recommended answers adopted below)

These are the real product/engineering decisions in this issue. Each is adopted as
this document's working design so G2 (architect) and G4 (backend-dev) have a concrete
target; flag any of them to Marco for override before implementation starts, in which
case this file is revised before G2 proceeds.

1. **TenantId's underlying representation: `Guid` vs UUIDv7 vs a string slug.**
   **Recommended and adopted: `Guid`, minted as UUIDv7 (`Guid.CreateVersion7()`) by
   `TenantId.New()`.** UUIDv7 is time-ordered, which keeps the Postgres primary-key
   index (and any `tenant_id`-prefixed Redis/object-storage key, ADR-0001) from
   fragmenting the way random UUIDv4 does. A string slug (e.g. `"acme-corp"`) would be
   friendlier in self-hosted URLs, but it drags in a uniqueness/rename policy that is a
   product decision for the tenant-onboarding feature, not for SharedKernel; nothing
   here blocks adding a separate, human-readable `TenantSlug` next to `TenantId` later
   if that product need appears.
2. **`TenantId.Parse`/`TryParse` strictness: only UUIDv7-shaped text, or any valid
   GUID text?** **Recommended and adopted: lenient — accept any valid, non-empty GUID
   text.** The exact `tenant_id` claim format Keycloak issues is #22's decision (ADR-0001
   decision D6); SharedKernel must not reject a well-formed tenant id merely because it
   predates the UUIDv7 minting convention (e.g. data migrated from another system).
   Version-7 shape is a minting convention (`New()`), not a wire contract.
3. **Result as a library (FluentResults, ErrorOr) or hand-rolled?** **Recommended and
   adopted: hand-rolled**, in `Decisya.SharedKernel`, no new package. Neither package is
   in `Directory.Packages.props`; `CLAUDE.md` requires a one-line justification for any
   new dependency, and the shape needed here (success/failure, one `DomainError`, `Match`) is
   small enough that owning it outright is cheaper than justifying, pinning and
   tracking a third-party API surface for it.
4. **Implicit conversions on `Result`/`Result<T>`?** **Recommended and adopted: yes,
   from `T` to a successful `Result<T>` and from `DomainError` to a failed `Result`/
   `Result<T>`** (so a handler can `return value;` or `return someError;` directly);
   **no implicit conversion to `bool`** (no `if (result)` truthy checks) — `IsSuccess`/
   `IsFailure` must be read explicitly, since a bool conversion's polarity is exactly
   the kind of ambiguity a financial-domain handler must not leave to a reader's guess.
5. **Is a `TenantId`'s text form personally identifiable, and must it be masked in
   logs?** **Recommended and adopted: no — `TenantId` carries no `[Sensitive]`
   marking and its `tenant_id` log field stays unmasked**, consistent with #15's
   existing design: #15 already reserved `tenant_id` as a plain (if then-empty) log
   field distinct from `user_id`, which is hashed by `UserIdHasher` before it is ever
   logged. A tenant identifier names an organisation/account, not a natural person; it
   is unmasked so an operator can correlate an incident to a tenant directly from a log
   line, the same way `CLAUDE.md`'s observability principles already assume.

---

### Story 1 — TenantId is a validated, never-default identifier

As a Decisya module developer, I want a `TenantId` value type that can only be
produced already valid — minted fresh, parsed from a known-good `Guid`, or parsed from
a claim string — and that is never silently the all-zero GUID, so that no module can
persist, query or route on a tenant identifier that doesn't actually identify a tenant
(`CLAUDE.md` invariant 1: "every persisted aggregate ... carries `TenantId`").

#### Acceptance criteria

```gherkin
Feature: TenantId is a validated, never-default identifier

  Scenario: A new TenantId is always initialized and non-empty
    Given no existing tenant identifier
    When a new TenantId is minted via TenantId.New()
    Then it is initialized (IsInitialized is true)
    And its underlying value is not Guid.Empty

  Scenario: A well-formed claim string parses to a TenantId
    Given the claim value "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70"
    When TenantId.Parse is called with that string
    Then a TenantId is produced
    And formatting it back to a string yields the same lowercase GUID text

  Scenario: A malformed claim string is rejected
    Given the claim value "not-a-guid"
    When TenantId.Parse is called with that string
    Then a TenantIdFormatException is raised
    And no TenantId instance is produced

  Scenario: The exception from a malformed claim never echoes hostile input raw
    Given the claim value contains control characters, an embedded newline, or is far
      longer than any real GUID (e.g. a multi-kilobyte string)
    When TenantId.Parse is called with that value
    Then a TenantIdFormatException is raised
    And its Message contains only a short, printable, truncated preview (or no
      preview at all), never the raw unbounded or control-character input verbatim

  Scenario: TryParse reports failure without throwing
    Given the claim value "not-a-guid"
    When TenantId.TryParse is called with that string
    Then it returns false
    And the out parameter is the uninitialized default TenantId

  Scenario: The all-zero GUID is never a valid tenant id
    Given the value Guid.Empty, or the text "00000000-0000-0000-0000-000000000000"
    When TenantId.From is called with the Guid, or TenantId.Parse with the text
    Then a TenantIdFormatException is raised
    And no TenantId instance is produced

  Scenario: The default TenantId is never initialized
    Given default(TenantId), obtained without going through New, From or Parse
    When IsInitialized is inspected
    Then it is false

  Scenario: Equality is value-based on the underlying identifier
    Given two TenantId instances parsed from the same GUID text (differing only in
      letter case or surrounding whitespace)
    When they are compared for equality
    Then they are equal
    And they have the same hash code

  Scenario: TenantId is immutable
    Given a TenantId instance
    When any of its members are inspected
    Then no member allows mutating the underlying value after construction
```

---

### Story 2 — TenantId round-trips through JSON as a plain string

As a Decisya module developer, I want `TenantId` to serialize as a plain JSON string
and to reject a malformed or null string rather than silently substituting a default
value, so that a `TenantId` field on any DTO or Wolverine message that crosses a JSON
boundary can never turn into an unrelated or empty tenant on the way through.

#### Acceptance criteria

```gherkin
Feature: TenantId serializes to and from JSON as a plain string

  Scenario: Serializing a TenantId produces a plain JSON string
    Given a TenantId parsed from "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70"
    When it is serialized with System.Text.Json
    Then the JSON value is the plain string "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70"
    And it is not a JSON object

  Scenario: Deserializing a valid string round-trips to the same TenantId
    Given the JSON string "\"018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70\""
    When it is deserialized as TenantId
    Then the result equals TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70")

  Scenario: Deserializing a malformed JSON string fails, not a default TenantId
    Given the JSON string "\"not-a-guid\""
    When it is deserialized as TenantId
    Then a JsonException is raised
    And no TenantId instance is produced

  Scenario: Deserializing JSON null fails for a non-nullable TenantId member
    Given the JSON literal null for a property typed TenantId (not TenantId?)
    When it is deserialized
    Then a JsonException is raised

  Scenario: The converter needs no caller registration
    Given a POCO with a plain TenantId property and default JsonSerializerOptions
    When it is serialized and deserialized
    Then the round trip in the two prior scenarios succeeds without the caller adding
      a converter to JsonSerializerOptions.Converters
```

---

### Story 3 — TenantId's text form is safe to log unmasked

As a Decisya module developer, I want `TenantId.ToString()` to produce the canonical,
lowercase GUID text and to carry no `[Sensitive]` marking, so that the `tenant_id`
field #15 already reserved on every structured log line can be filled with a real
value without the masking pipeline redacting it, letting an operator correlate an
incident to a tenant straight from stdout (`CLAUDE.md`: "Logs are JSON to stdout with
`trace_id`, `span_id`, `tenant_id`, hashed `user_id`" — only `user_id` is hashed).

#### Acceptance criteria

```gherkin
Feature: TenantId's text form is safe to log unmasked

  Scenario: ToString produces the canonical lowercase GUID text
    Given a TenantId parsed from "018F2C9E-6B7A-7C3E-8B1A-2F3C4D5E6F70"
    When it is formatted with ToString
    Then the result is exactly "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70"

  Scenario: TenantId carries no [Sensitive] marking
    Given the TenantId type and its members
    When they are inspected for Decisya.SharedKernel.Observability.SensitiveAttribute
    Then none of them carry it

  Scenario: A logged TenantId value is never masked
    Given a log entry whose state includes a TenantId value under the key "tenant_id"
    When the entry passes through SensitiveDataMaskingProcessor
    Then the emitted value is the plain GUID text, not a masked placeholder

  Scenario: The default, uninitialized TenantId formats safely
    Given default(TenantId)
    When it is formatted with ToString
    Then no exception is thrown
    And the result is an empty string, signalling "no tenant" rather than a
      fabricated identifier
```

---

### Story 4 — Result and Result&lt;T&gt; represent an expected failure without throwing

As a Decisya module developer, I want `Result` and `Result<T>` types that a handler
can return to signal an expected domain failure (e.g. "tenant not found", "insufficient
funds") as a value instead of throwing an exception, so that expected business
outcomes don't pay the cost or the control-flow surprise of exceptions, and so that
every handler has one consistent shape to test.

#### Acceptance criteria

```gherkin
Feature: Result and Result<T> represent an expected success or failure without throwing

  Scenario: A successful Result carries no error
    Given Result.Success() is constructed
    Then IsSuccess is true and IsFailure is false
    And reading Error throws InvalidOperationException

  Scenario: A failed Result exposes its Error
    Given a DomainError with code "tenant.not_found"
    When Result.Failure(error) is constructed
    Then IsSuccess is false and IsFailure is true
    And Error equals the given error

  Scenario: A successful Result<T> exposes its value
    Given Result<int>.Success(42)
    Then IsSuccess is true
    And Value equals 42
    And reading Error throws InvalidOperationException

  Scenario: A failed Result<T> exposes no value
    Given a DomainError with code "money.currency_mismatch"
    When Result<int>.Failure(error) is constructed
    Then IsSuccess is false
    And reading Value throws InvalidOperationException
    And Error equals the given error

  Scenario: A value converts implicitly to a successful Result<T>
    Given a method with return type Result<int>
    When it executes the statement "return 42;"
    Then the caller observes a successful Result<int> with Value 42

  Scenario: A DomainError converts implicitly to a failed Result and Result<T>
    Given a method with return type Result<int>
    When it executes the statement "return someError;" for a DomainError instance someError
    Then the caller observes a failed Result<int> with Error equal to someError

  Scenario: Neither Result nor Result<T> converts implicitly to bool
    Given a Result or Result<int> value
    When the compiled source is inspected
    Then no implicit conversion to bool exists (no "if (result)" is a valid statement)

  Scenario: Match dispatches to exactly one branch
    Given a successful Result<int> with Value 42
    When Match is called with an onSuccess and an onFailure delegate
    Then only onSuccess is invoked, exactly once, with argument 42
    And onFailure is never invoked

  Scenario: Match dispatches to the failure branch for a failed Result
    Given a failed Result with a given DomainError
    When Match is called with an onSuccess and an onFailure delegate
    Then only onFailure is invoked, exactly once, with that DomainError
    And onSuccess is never invoked

  Scenario: Result and Result<T> compare by value
    Given two successful Result<int> instances both holding 42
    When they are compared for equality
    Then they are equal
    And two failed Result instances holding equal DomainErrors are also equal
    And a successful and a failed instance are never equal
```

---

### Story 5 — DomainError carries a stable code and category without leaking detail to a client

As a Decisya module developer, I want a `DomainError` type with a required, stable,
machine-readable `Code`, a `Category` (for a later HTTP-status mapping), and a
developer-facing `Message` that nothing in this assembly ever sends to an HTTP
response, so that a handler can describe *why* it failed in a way a future
`ProblemDetails` mapping (#20) can act on, while the security principle "generic
message to the client, full detail to the structured log" holds by construction.

#### Acceptance criteria

```gherkin
Feature: DomainError carries a stable code and category, safe to expose, without leaking detail

  Scenario: A code is required and must be a non-empty, non-whitespace identifier
    Given an attempt to create a DomainError with a null, empty or whitespace-only code
    When DomainError.New is called
    Then an ArgumentException is raised
    And no DomainError instance is produced

  Scenario: A code and category classify the failure without a hardcoded HTTP status
    Given the code "tenant.not_found" and the category ErrorCategory.NotFound
    When a DomainError is created from them
    Then DomainError.Code is "tenant.not_found"
    And DomainError.Category is ErrorCategory.NotFound
    And DomainError exposes no HTTP status code member (mapping Category to a status is #20's
      concern, not SharedKernel's)

  Scenario: Category defaults to a generic failure when not specified
    Given a DomainError created with only a code and a message
    When Category is inspected
    Then it is ErrorCategory.Failure

  Scenario: Message is available to application code but this assembly never exposes
    it to an HTTP client
    Given a DomainError created with a Message describing internal detail (e.g. a
      constraint name or a stack detail)
    When Decisya.SharedKernel's public surface is inspected
    Then Message is readable by application and test code
    And no type in Decisya.SharedKernel serializes a DomainError or a Message to an HTTP
      response (no ProblemDetails mapping exists in this assembly; #20 owns that hop)

  Scenario: Two errors with the same code and category are equal regardless of message
    Given two DomainError instances created with code "tenant.not_found", category NotFound,
      and different Message text
    When they are compared for equality
    Then they are equal
    And they have the same hash code
```

---

## Out of scope for issue 0.04b

- **`ITenantScoped`** and the interface every persisted aggregate implements. Owned by
  **#22 (0.10)**; `TenantId` is the value type that interface's `TenantId` property
  will hold.
- **EF Core persistence mapping** for `TenantId` (the value converter, the global
  query filter that reads it, `TenantDbContext`). Owned by **#22 (0.10)**.
- **Claim extraction**: reading the `tenant_id` claim off an authenticated principal
  and populating an `ITenantContext`/`ILogEnrichmentContext` with a real `TenantId`.
  Owned by **#20 (0.08)** — this issue only makes `TenantId.Parse` available for #20 to
  call once a claim string exists.
- **`ITenantContext` / any ambient-tenant service.** Not created here; SharedKernel
  ships the value type only, no service that resolves "the current tenant".
- **`ProblemDetails` mapping** from `DomainError.Code`/`DomainError.Category` to an HTTP status and
  response body. Owned by **#20 (0.08)**, per the `api-contract` skill's existing
  `application/problem+json` convention.
- **Aggregating multiple errors** (e.g. a list of validation failures from
  FluentValidation) into one `Result`. `DomainError` here models exactly one failure; a
  multi-error shape, if needed, is a later, separate addition once a real validation
  pipeline exists.
- **A human-readable `TenantSlug`** or any tenant-naming feature. If self-hosting UX
  later needs one, it is added alongside `TenantId`, not instead of it (see Open
  question 1).

## Decisions (Marco, 2026-09-26)

1. **`Error` renamed to `DomainError`.** `Error` triggers CA1716 (a naming-rule
   violation for colliding with a Visual Basic reserved word), which this repo's
   analyzer configuration treats as a build error. Every reference to the
   result-error type in this document is `DomainError`; `ErrorCategory` and the
   `Result`/`Result<T>` `Error` property keep their existing names, since neither of
   those identifiers triggers the clash.

## Non-functional requirements

See `docs/requirements/nfr.md` — this issue adds NFR-13 and NFR-14.

<!-- gate: G1 | verdict: PASS | issue: #32 -->
