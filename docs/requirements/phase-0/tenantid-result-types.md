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

## Traceability

Verified against `dotnet test --no-build --list-tests` on `tests/Decisya.SharedKernel.Tests` and
a full `dotnet build -warnaserror` + `dotnet test --no-build --filter-not-trait "Category=Integration"`
run at G5 (428/428 passed, 0 build warnings — matches the G4 evidence in `docs/ai/pipeline/32.md`).
No scenario or requirement needed a new test; every one already has a real, passing test from G4.
Test classes are in `Decisya.SharedKernel.Tests.Tenancy` and `.Results` unless noted.

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A new TenantId is always initialized and non-empty | `TenantIdTests.New_produces_an_initialized_non_empty_TenantId` | Unit |
| 1 | A well-formed claim string parses to a TenantId | `TenantIdTests.A_well_formed_claim_string_parses_and_formats_back_to_the_same_lowercase_text` | Unit |
| 1 | A malformed claim string is rejected | `TenantIdTests.A_malformed_claim_string_is_rejected_and_produces_no_instance` | Unit |
| 1 | The exception from a malformed claim never echoes hostile input raw | `TenantIdFormatExceptionTests.Parse_never_echoes_hostile_input_and_the_message_stays_fixed` (CRLF/ANSI, 10 KiB, JWT-shaped canaries) | Unit |
| 1 | TryParse reports failure without throwing | `TenantIdTests.TryParse_reports_failure_without_throwing_and_the_out_parameter_is_the_default`, `TryParse_of_a_null_string_reports_failure_without_throwing` | Unit |
| 1 | The all-zero GUID is never a valid tenant id | `TenantIdTests.Guid_Empty_is_rejected_by_From`, `The_all_zero_guid_text_is_rejected_by_Parse`, `The_all_zero_guid_text_is_rejected_by_TryParse_string_overload`, `The_all_zero_guid_text_is_rejected_by_TryParse_span_overload`; `TenantIdJsonTests.The_all_zero_guid_text_is_rejected_by_the_converter` | Unit |
| 1 | The default TenantId is never initialized | `TenantIdTests.The_default_TenantId_obtained_without_any_factory_is_never_initialized` | Unit |
| 1 | Equality is value-based on the underlying identifier | `TenantIdTests.Two_instances_parsed_from_the_same_guid_differing_only_by_case_or_whitespace_are_equal` | Unit |
| 1 | TenantId is immutable | `TenantIdTests.TenantId_exposes_no_public_settable_member` | Unit |
| 2 | Serializing a TenantId produces a plain JSON string | `TenantIdJsonTests.Serializing_a_TenantId_produces_a_plain_JSON_string` | Unit |
| 2 | Deserializing a valid string round-trips to the same TenantId | `TenantIdJsonTests.Deserializing_a_valid_string_round_trips_to_the_same_TenantId` | Unit |
| 2 | Deserializing a malformed JSON string fails, not a default TenantId | `TenantIdJsonTests.Deserializing_a_malformed_string_throws_and_produces_no_instance` | Unit |
| 2 | Deserializing JSON null fails for a non-nullable TenantId member | `TenantIdJsonTests.Deserializing_JSON_null_for_a_non_nullable_member_throws` | Unit |
| 2 | The converter needs no caller registration | `TenantIdJsonTests.The_round_trip_needs_no_caller_registered_converter` | Unit |
| 3 | ToString produces the canonical lowercase GUID text | `TenantIdLoggingTests.ToString_produces_the_canonical_lowercase_guid_text` | Unit |
| 3 | TenantId carries no [Sensitive] marking | `TenantIdLoggingTests.TenantId_carries_no_SensitiveAttribute_on_the_type_or_any_member` | Unit |
| 3 | A logged TenantId value is never masked | `TenantIdLoggingTests.A_logged_TenantId_value_passes_through_SensitiveDataMaskingProcessor_unmasked` | Unit |
| 3 | The default, uninitialized TenantId formats safely | `TenantIdLoggingTests.The_default_uninitialized_TenantId_formats_safely_as_an_empty_string`; `TenantIdTests.Default_TenantId_formats_as_an_empty_string_without_throwing` | Unit |
| 4 | A successful Result carries no error | `ResultTests.A_successful_Result_carries_no_error` | Unit |
| 4 | A failed Result exposes its Error | `ResultTests.A_failed_Result_exposes_its_error` | Unit |
| 4 | A successful Result\<T\> exposes its value | `ResultOfTTests.A_successful_ResultOfT_exposes_its_value` | Unit |
| 4 | A failed Result\<T\> exposes no value | `ResultOfTTests.A_failed_ResultOfT_exposes_no_value` | Unit |
| 4 | A value converts implicitly to a successful Result\<T\> | `ResultOfTTests.A_value_converts_implicitly_to_a_successful_ResultOfT` | Unit |
| 4 | A DomainError converts implicitly to a failed Result and Result\<T\> | `ResultTests.An_error_converts_implicitly_to_a_failed_Result`; `ResultOfTTests.An_error_converts_implicitly_to_a_failed_ResultOfT` | Unit |
| 4 | Neither Result nor Result\<T\> converts implicitly to bool | `SharedKernelBoundaryTests.Result_declares_no_conversion_to_bool_and_no_operator_true_or_false`, `ResultOfT_declares_no_conversion_to_bool_and_no_operator_true_or_false` | Architecture |
| 4 | Match dispatches to exactly one branch (success) | `ResultOfTTests.Match_dispatches_to_the_success_branch_exactly_once_with_the_value`; `ResultTests.Match_dispatches_to_the_success_branch_exactly_once` | Unit |
| 4 | Match dispatches to the failure branch for a failed Result | `ResultTests.Match_dispatches_to_the_failure_branch_exactly_once_with_the_error`; `ResultOfTTests.Match_dispatches_to_the_failure_branch_exactly_once` | Unit |
| 4 | Result and Result\<T\> compare by value | `ResultTests.Two_successful_results_are_equal`, `Two_failed_results_with_equal_errors_are_equal`, `A_success_and_a_failure_are_never_equal`; `ResultOfTTests.Two_successful_results_with_equal_values_are_equal`, `Two_failed_results_with_equal_errors_are_equal`, `A_success_and_a_failure_are_never_equal` | Unit |
| 5 | A code is required and must be a non-empty, non-whitespace identifier | `DomainErrorTests.A_null_empty_or_whitespace_only_code_is_rejected`, `A_null_code_specifically_throws_ArgumentNullException` | Unit |
| 5 | A code and category classify the failure without a hardcoded HTTP status | `DomainErrorTests.A_code_and_category_classify_the_failure`, `Error_exposes_no_HTTP_status_code_member` | Unit |
| 5 | Category defaults to a generic failure when not specified | `DomainErrorTests.Category_defaults_to_Failure_when_not_specified` | Unit |
| 5 | Message is available to application code but this assembly never exposes it to an HTTP client | `DomainErrorTests.Message_is_readable_by_application_code`, `Serializing_a_DomainError_never_includes_the_Message`, `ToString_never_renders_Message`; `SharedKernelBoundaryTests.Results_types_do_not_depend_on_HTTP_or_ASPNETCORE_assemblies` | Unit / Architecture |
| 5 | Two errors with the same code and category are equal regardless of message | `DomainErrorTests.Two_errors_with_the_same_code_and_category_are_equal_regardless_of_message` | Unit |
| NFR-13 | ≥ 95% line coverage on TenantId, Result, Result\<T\>, DomainError | manual — measured by `dotnet test tests/Decisya.SharedKernel.Tests --coverage --coverage-output-format cobertura` (a coverage-tool report, not a single named test); G4 recorded 100% line coverage on `TenantId`, `TenantIdFormatException`, `TenantIdJsonConverter`, `DomainError`, `Result` and `Result<T>` (`docs/ai/pipeline/32.md` G4 evidence); not re-run at G5, which reruns `dotnet test` without `--coverage` per this gate's fixed command set | manual |
| NFR-14 | TenantId round-trips losslessly across ≥ 10,000 randomized cases | `TenantIdPropertyTests.TenantId_round_trips_through_Parse_and_JSON_losslessly_across_ten_thousand_random_cases` | Unit |
| G4-32-01 | Canonical `D` shape: legacy `0x`/`0X`/`+` compatibility forms, `N`/`B`/`P`/`X` forms, a shifted hyphen, a fullwidth digit are all rejected, on every entry point | `TenantIdParsingTests.TryParse_string_overload_rejects_every_non_canonical_shape`, `TryParse_span_overload_rejects_every_non_canonical_shape`, `Parse_throws_for_every_non_canonical_shape`, `The_JSON_converter_rejects_every_non_canonical_shape` (theory over all listed forms); `TenantIdPropertyTests.Every_random_string_either_fails_to_parse_or_round_trips_to_its_trimmed_lowercase_form_across_ten_thousand_cases` (generator extended with `+`, `x`, `X`) | Unit |
| G4-32-02 | Length cap runs before the trim | `TenantIdParsingTests.A_valid_GUID_padded_with_one_megabyte_of_spaces_is_rejected`, `A_valid_GUID_padded_with_a_few_ASCII_whitespace_characters_is_accepted`, `Input_longer_than_64_characters_fails_before_any_trim_would_run` | Unit |
| G4-32-03 | No empty tenant, across all five entry points (`From`, `Parse`, both `TryParse` overloads, the JSON converter) | `TenantIdTests.Guid_Empty_is_rejected_by_From`, `The_all_zero_guid_text_is_rejected_by_Parse`, `The_all_zero_guid_text_is_rejected_by_TryParse_string_overload`, `The_all_zero_guid_text_is_rejected_by_TryParse_span_overload`; `TenantIdJsonTests.The_all_zero_guid_text_is_rejected_by_the_converter` | Unit |
| G4-32-04 | `default(TenantId)` fails closed: `IsInitialized` false, `Value` throws, `ToString()` is `""`, JSON `Serialize` throws (incl. as a DTO member) | `TenantIdTests.The_default_TenantId_obtained_without_any_factory_is_never_initialized`, `Default_TenantId_Value_throws_InvalidOperationException_with_a_fixed_message`, `Default_TenantId_formats_as_an_empty_string_without_throwing`; `TenantIdJsonTests.Serializing_the_default_TenantId_throws`, `Serializing_a_DTO_with_an_uninitialized_TenantId_member_throws` | Unit |
| G4-32-05 | `TenantIdFormatException` carries no input, no extra property, null `InnerException`, empty `Data` | `TenantIdFormatExceptionTests.Parse_never_echoes_hostile_input_and_the_message_stays_fixed`, `The_exception_declares_no_public_property_beyond_FormatExceptions_own`, `The_exception_has_a_null_inner_exception_and_empty_data` | Unit |
| G4-32-06 | Converter limits and messages: non-string token, oversized raw token, malformed string, `TenantId?` null handling, required-vs-non-required missing member | `TenantIdJsonTests.A_non_string_token_throws`, `An_object_token_throws`, `A_raw_token_over_128_bytes_is_rejected_and_the_message_carries_no_canary`, `A_malformed_value_never_appears_in_the_exception_message_or_ToString`, `A_DTO_with_a_required_TenantId_member_throws_when_the_property_is_missing`, `A_DTO_with_a_non_required_TenantId_member_defaults_when_the_property_is_missing`, `Deserializing_JSON_null_for_a_nullable_TenantId_member_yields_null`, `Deserializing_JSON_null_for_a_non_nullable_member_throws` | Unit |
| G4-32-07 | No route or string binding: no `IParsable`/`ISpanParsable`, no `[TypeConverter]`, no conversion from `string`/`Guid` | `TenantIdTests.TenantId_implements_neither_IParsable_nor_ISpanParsable`, `TenantId_carries_no_TypeConverterAttribute`, `TenantId_declares_no_implicit_or_explicit_conversion_from_string_or_guid` | Unit |
| G4-32-08 | `DomainError` safety: code pattern/length, null code/message, undefined category, `[JsonIgnore]` on `Message`, `ToString()` shape, no code echoed on failure | `DomainErrorTests.A_code_that_does_not_match_the_required_pattern_is_rejected`, `A_code_matching_the_required_pattern_is_accepted`, `A_code_longer_than_the_maximum_is_rejected`, `A_null_code_specifically_throws_ArgumentNullException`, `A_null_message_throws_ArgumentNullException`, `An_undefined_category_throws_ArgumentOutOfRangeException`, `Serializing_a_DomainError_never_includes_the_Message`, `ToString_never_renders_Message`, `An_invalid_codes_exception_never_echoes_the_code` | Unit |
| G4-32-09 | `Result<T>.Value` on failure and `.Error` on success throw `InvalidOperationException` with a fixed message, leaking neither `Value` nor `DomainError.Message` | `ResultOfTTests.Reading_Value_on_a_failure_throws_InvalidOperationException_without_leaking_the_errors_message`, `Reading_Error_on_a_success_throws_InvalidOperationException_without_leaking_the_value`, `ToString_never_renders_the_value_or_the_errors_message_for_either_outcome`; `ResultTests.A_successful_Result_carries_no_error`, `ToString_never_renders_the_errors_message` | Unit |
| G4-32-10 | No null success/failure: `Success`/implicit-from-`T`, `Failure`/implicit-from-`DomainError` all throw `ArgumentNullException` on null | `ResultOfTTests.Success_throws_ArgumentNullException_for_a_null_value`, `The_implicit_conversion_from_a_null_value_throws_ArgumentNullException`, `FailureOfT_throws_ArgumentNullException_for_a_null_error`, `The_implicit_conversion_from_a_null_error_throws_ArgumentNullException`; `ResultTests.Failure_throws_ArgumentNullException_for_a_null_error` | Unit |
| G4-32-11 | No time accessor: `New()` is version 7, 1,000 calls are distinct, no `DateTime`/`DateTimeOffset`/NodaTime member | `TenantIdTests.New_produces_a_version_7_guid`, `One_thousand_calls_to_New_produce_distinct_values`, `TenantId_exposes_no_member_typed_DateTime_DateTimeOffset_or_a_NodaTime_type` | Unit |
| G4-32-12 | Logged unmasked and stays scalar-shaped (exactly `Value`/`IsInitialized`) | `TenantIdLoggingTests.A_logged_TenantId_value_passes_through_SensitiveDataMaskingProcessor_unmasked`, `TenantId_exposes_exactly_the_two_expected_public_instance_properties` | Unit |
| G4-32-13 | XML docs as guardrails (`ToString`, `==`/`Equals`, UUIDv7 time-bits remark) | manual — XML-doc guardrail; G6 reads the diff's XML docs, per the requirement's own "Test: G6 check" — not a gap, not automatable | manual |
| G4-32-14 | XML doc rule on `DomainError.Message` content and on `Result`'s ignorable-authorization caveat | manual — XML-doc guardrail; G6 reads the diff's XML docs, per the requirement's own "Test: G6 check" — not a gap, not automatable | manual |
| G4-32-15 | No new dependency: Tenancy/Results depend on `System.*` only, allow-listed assembly references | `SharedKernelBoundaryTests.Tenancy_types_only_depend_on_System_and_Tenancy`, `Results_types_only_depend_on_System_and_Results`, `SharedKernels_referenced_assemblies_stay_on_the_allow_list` | Architecture |

<!-- gate: G5 | verdict: PASS | issue: #32 -->
