# Phase 0 – Decisya.AppHost + Decisya.ServiceDefaults

## Issue 0.03 (#15) — Aspire AppHost + ServiceDefaults (OTel, health, JSON logs)

### Scope note (role framing)

`Decisya.AppHost` and `Decisya.ServiceDefaults` are platform infrastructure with no
tenant-facing surface of their own: every host in the platform (the future
`Decisya.Api`, `Decisya.Bff`, and every module) inherits them, but no tenant ever
calls into `Decisya.ServiceDefaults` directly. There is no "role in tenant" for a
wiring library. The stories below use **"As a Decisya platform operator"** as the
acting role — the person (Marco today, on-call staff later) who runs the platform,
watches the Aspire dashboard in dev, and reads stdout logs during an incident — per
`CLAUDE.md`'s Observability and Security principles.

**Entitlement plan:** N/A. `Decisya.AppHost` and `Decisya.ServiceDefaults` are shipped
identically regardless of subscription plan; they are not gated by an entitlement
feature key. Any future feature key referenced here would belong to a consuming
module, not to this infrastructure.

**Scope, tightly bound to issue #15:**

- OpenTelemetry wiring in `Decisya.ServiceDefaults`, OTLP exporter only.
- `/health` and `/alive` endpoints, wired through `MapDefaultEndpoints`.
- JSON logs to stdout carrying `trace_id`, `span_id`, and reserved (possibly empty)
  `tenant_id` / hashed `user_id` fields.
- The `[Sensitive]` attribute and the `SensitiveDataMaskingProcessor` masking hook
  (named in `.claude/scripts/prereqs.py`'s `otel-instrumentation` check as expected
  from this issue).
- A `src/Decisya.Api` **skeleton** host — `AddServiceDefaults` and the health
  endpoints only, no auth, no business endpoints, no database — added as an `AppHost`
  resource, whose job in this issue is to let the Done-when be demonstrated: a call
  to its health endpoint produces a trace visible in the Aspire dashboard. #20 (0.08)
  adds JWT validation and the first business endpoints to this same project.

---

### Story 1 — Telemetry leaves every host only through OTLP

As a Decisya platform operator, I want every `Decisya.ServiceDefaults`-instrumented
host to export its traces and metrics exclusively through the OpenTelemetry Protocol
(OTLP) exporter, so that the platform never depends on a proprietary observability
vendor and any host can be pointed at any OTLP-compatible backend (the Aspire
dashboard today, anything else later) without a code change (`CLAUDE.md`: "OpenTelemetry
only (OTLP exporter); no vendor SDK").

#### Acceptance criteria

```gherkin
Feature: Telemetry leaves every host only through OTLP

  Scenario: The OTLP exporter activates when the AppHost supplies an endpoint
    Given a host referencing Decisya.ServiceDefaults
    And its configuration includes OTEL_EXPORTER_OTLP_ENDPOINT, set by the AppHost
    When the host builds its OpenTelemetry pipeline at startup
    Then traces and metrics are registered for export over OTLP to that endpoint

  Scenario: No exporter is wired when no OTLP endpoint is configured
    Given a host referencing Decisya.ServiceDefaults
    And no OTEL_EXPORTER_OTLP_ENDPOINT is present in its configuration
    When the host starts
    Then it starts successfully
    And no OTLP export attempt is made and no exception is thrown

  Scenario: No vendor-specific exporter package is referenced
    Given the compiled Decisya.ServiceDefaults assembly and any host referencing it
    When its package and assembly references are inspected
    Then no telemetry exporter other than the OpenTelemetry OTLP exporter is present
    And no vendor SDK (for example Application Insights, Datadog, New Relic) is referenced

  Scenario: Outgoing HTTP calls propagate trace context
    Given a host referencing Decisya.ServiceDefaults makes an outgoing HTTP call
      while a request is being traced
    When the call is made
    Then the outgoing request carries a W3C traceparent header for the active trace
```

---

### Story 2 — Health and liveness endpoints report process state

As a Decisya platform operator, I want each host to expose a liveness endpoint and a
readiness endpoint, so that an orchestrator, or I, can tell a hung or not-yet-ready
process from a healthy one before it is trusted with traffic.

#### Acceptance criteria

```gherkin
Feature: Health and liveness endpoints report process state

  Scenario: /alive reports liveness using only checks tagged "live"
    Given a host referencing Decisya.ServiceDefaults has started successfully
    And the environment is Development
    When a GET request is made to /alive
    Then the response status is 200
    And only health checks tagged "live" are evaluated for that response

  Scenario: /health reports readiness across every registered check
    Given a host referencing Decisya.ServiceDefaults has started
    And the environment is Development
    And every registered health check currently reports Healthy
    When a GET request is made to /health
    Then the response status is 200

  Scenario: /health reflects an unhealthy dependency
    Given a host referencing Decisya.ServiceDefaults has started
    And the environment is Development
    And at least one registered health check currently reports Unhealthy
    When a GET request is made to /health
    Then the response status is 503

  Scenario: Health endpoints are not exposed outside Development by default
    Given a host referencing Decisya.ServiceDefaults is running with environment
      Production or Staging
    When the application starts
    Then /health and /alive are not mapped
    And a request to either path returns 404
```

---

### Story 3 — Structured JSON logs correlate with the active trace

As a Decisya platform operator, I want every log line written to stdout by a
`Decisya.ServiceDefaults`-instrumented host to be a single well-formed JSON object
carrying the `trace_id` and `span_id` of the request it was written during, so that I
can pivot from a trace in the Aspire dashboard straight to the log lines it produced
during an incident (`CLAUDE.md`: "Logs are JSON to stdout with trace_id, span_id,
tenant_id, hashed user_id").

#### Acceptance criteria

```gherkin
Feature: Structured logs correlate with the active trace

  Scenario: A log written during a traced request carries matching identifiers
    Given an incoming HTTP request is being traced by OpenTelemetry
    When a log entry is written by application code during that request
    Then the JSON log record on stdout includes a trace_id field and a span_id field
    And those values equal the active Activity's TraceId and SpanId

  Scenario: A log written outside any active trace carries no fabricated identifiers
    Given no OpenTelemetry Activity is active (for example, a log written at startup,
      before any request arrives)
    When a log entry is written
    Then the JSON log record's trace_id and span_id fields are empty or absent
    And no error occurs

  Scenario: Every stdout log line is well-formed JSON
    Given a host referencing Decisya.ServiceDefaults is running
    When any log entry is written to stdout
    Then the line is exactly one well-formed JSON object, with no interleaved
      non-JSON text on the same line
```

---

### Story 4 — Sensitive fields are masked, tenant and user context are reserved

As a Decisya platform operator, I want a `[Sensitive]` attribute and a
`SensitiveDataMaskingProcessor` log-processing hook that masks any field carrying it,
plus reserved `tenant_id` and hashed `user_id` fields on every log record, so that no
personally identifiable or tenant-confidential value is ever readable in plaintext in
the platform's logs — a guarantee that must exist before any module that actually
carries PII or tenant data ships (`CLAUDE.md`: "Mark PII with [Sensitive]; the log
processor masks it. Never log raw transaction descriptions, tokens, cookies or
passwords" and invariant 1's tenant-isolation-in-every-store, which names logs
explicitly).

#### Acceptance criteria

```gherkin
Feature: Sensitive fields are masked and tenant/user context is reserved on every log record

  Scenario: A field marked [Sensitive] is masked in the emitted log line
    Given a log entry includes a property decorated with the [Sensitive] attribute
    When the entry is written to stdout through the SensitiveDataMaskingProcessor
    Then the property's value in the JSON output is replaced with a masked placeholder
    And the original value does not appear anywhere in the emitted line

  Scenario: Every log record carries tenant_id and user_id fields, populated or not
    Given a log entry is written by a host with no authenticated tenant or user in
      context (for example, an anonymous call to /alive)
    When the entry is written
    Then the JSON log record includes a tenant_id field and a user_id field
    And both are empty or null, not omitted from the record

  Scenario: user_id is never logged raw
    Given a log entry is written while a user id is available in the logging context
    When the entry is written
    Then the user_id field contains a one-way hash of that id
    And the raw id does not appear anywhere in the emitted line

  Scenario: The masking hook fails closed
    Given the SensitiveDataMaskingProcessor cannot determine whether a property is
      marked [Sensitive] (for example, an internal error while inspecting it)
    When a log entry containing that property is written
    Then the property is masked rather than emitted unmasked
    And the rest of the log entry is still written, not silently dropped
```

---

### Story 5 — The AppHost proves the pipeline with a health call (the Done-when)

As a Decisya platform operator, I want the AppHost to run a `Decisya.Api` skeleton
host (`Decisya.ServiceDefaults` wiring and the health endpoints only — no auth, no
business endpoints, no database) and show, in the Aspire dashboard, a trace produced
by a call to that host's health endpoint, so that every later module host inherits
verified telemetry, health and logging wiring by construction, proven before any
tenant-facing code exists, and so that #20 (0.08) has a running host to add JWT
validation and business endpoints to. This is the issue's literal Done-when.

#### Acceptance criteria

```gherkin
Feature: The AppHost proves the observability pipeline with a health call

  Scenario: A call to the Decisya.Api skeleton's liveness endpoint produces a trace
    visible in the Aspire dashboard
    Given the AppHost is running locally with the Aspire dashboard open
    And it has added the Decisya.Api skeleton host as a resource
    When a GET request is made to the skeleton host's /alive endpoint
    Then the Aspire dashboard's Traces view shows a completed trace for that request
    And the trace's span carries the skeleton host's service name

  Scenario: The AppHost supplies OTLP configuration without manual host setup
    Given the AppHost project has added the Decisya.Api skeleton host as a resource
    When the AppHost starts that resource
    Then the resource receives OTEL_EXPORTER_OTLP_ENDPOINT and OTEL_SERVICE_NAME from
      the AppHost automatically
    And no OTLP endpoint or service name is hardcoded in the host's own configuration

  Scenario: The trace and the structured log agree
    Given the trace from the first scenario is visible in the Aspire dashboard
    When the skeleton host's stdout log line for that same request is inspected
    Then the log line's trace_id and span_id match the trace shown in the dashboard

  Scenario: The skeleton host carries no business surface yet
    Given the Decisya.Api skeleton host as delivered by this issue
    When its endpoint map is inspected
    Then it exposes only /health and /alive
    And it requires no authentication, and defines no business endpoint and no
      database connection
```

*(Verifying "the dashboard shows a trace" visually is a human/exploratory step; the
automatable proxy is that an OTLP receiver — the Aspire dashboard's own collector, or
a test double for it — receives a completed span for the request, and the skeleton
host's stdout log for the same request carries the matching `trace_id`/`span_id`. G5
may mark the visual dashboard check itself as manual, with this note as the reason.)*

---

## Out of scope for issue #15

- **`Decisya.Api` beyond the skeleton.** This issue creates `src/Decisya.Api` as a
  skeleton host (`AddServiceDefaults` + `MapDefaultEndpoints` only). JWT validation
  and the first business endpoints are added by **#20 (0.08)**
  (`.claude/scripts/prereqs.py`); the `Decisya.Bff` host in front of it (ADR-0003)
  comes later.
- **`ITenantScoped`, `TenantDbContext`, real tenant-filtered persistence.** Owned by
  **#22 (0.10)**.
- **The `TenantId` value type.** Owned by **#32 (0.04b)**. Until it exists, the
  `tenant_id` log field (Story 4) is a reserved, empty/nullable string field, not a
  real domain type.
- **Populating `tenant_id` / `user_id` from a real authenticated principal.** Needs
  Keycloak, the BFF/API auth handlers and `TenantId` (#32). This issue only reserves
  the fields and masks them; it does not resolve them from a request.
- **Postgres, Redis and Keycloak `AppHost` resources** (`builder.AddPostgres(...)`,
  `builder.AddRedis(...)`, `builder.AddKeycloak(...)`). Added by a later Phase 0
  issue, not yet reached in the sequence read for this issue.
- **`Decisya.Bff`** (ADR-0003: its own host, cookie/OIDC, YARP, ticket store). Created
  by the issue that delivers the BFF.
- **Internal JWT validation** (short-lived RS256/ES256, `iss`/`aud`/`exp`, alg
  allow-list — the BFF→API hop in `CLAUDE.md`'s security principles). Added to the
  `Decisya.Api` skeleton by **#20 (0.08)**; the `Decisya.Bff` side comes once that
  host exists.
- **The migrator process and its DDL-owning role** (ADR-0005). Created by the first
  issue that needs to run EF migrations.
- **Per-module `ActivitySource`/`Meter` registration** (`Decisya.<Module>`, per
  `CLAUDE.md`'s observability principles). Each module registers its own when it is
  scaffolded, using the `otel-instrumentation` skill, which depends on the
  `[Sensitive]` attribute and `SensitiveDataMaskingProcessor` this issue delivers.
- **Repo-wide NetArchTest enforcement** that no assembly outside
  `Decisya.Infrastructure.Ai` (or, here, outside the OTLP exporter) references a
  vendor SDK. `Decisya.ArchitectureTests` is owned by **#22 (0.10)**; Story 1's third
  scenario is checked by a narrower, local test in this issue's own test project.
- **Generic-error-to-client / detailed-error-to-log middleware** (`CLAUDE.md`:
  "Errors: generic message to the client, full detail to the structured log with
  trace id"). Deferred to the first host with real endpoints beyond health checks
  (`Decisya.Api`, #20).

## Decisions (Marco, 2026-09-25)

Both open questions from the first draft of this document are answered. No question
is left unanswered.

1. **Host identity (Story 5).** `src/Decisya.Api` is created **now**, in this issue,
   as a skeleton — not a throwaway sample. It carries only
   `AddServiceDefaults`/`MapDefaultEndpoints` and the health endpoints: no auth, no
   business endpoints, no database. `Decisya.Api` therefore moves out of the "out of
   scope" list above and into this issue's scope as a skeleton; **#20 (0.08)** adds
   JWT validation and the first business endpoints to this same project, and the
   `Decisya.Bff` host in front of it (ADR-0003) comes later. `docs/GETTING-STARTED.md`
   step 2's verification text ("Resources page is empty") is corrected in this
   issue's PR to match: `Decisya.Api` is added as an `AppHost` resource, and the
   dashboard shows a trace for a call to its health endpoint.
2. **Health endpoint exposure.** `/health` and `/alive` stay Development-only for
   now, per the Aspire/ASP.NET Core template default. The deployment issue (0.17)
   decides production probes.

## Non-functional requirements

See `docs/requirements/nfr.md` — this issue adds NFR-10, NFR-11 and NFR-12.

<!-- gate: G1 | verdict: PASS | issue: #15 -->

## Traceability

Test-engineer closed the four gaps backend-dev reported in the manifest's G4 evidence
(G4-15-13, 18, 22, 26) with new tests in the existing test projects, listed inline below.
No `src/` file was changed. `dotnet build -warnaserror` (0 warnings) and
`dotnet test --no-build --filter-not-trait "Category=Integration"` (244/244) both pass;
`gitleaks detect` is clean.

All test classes are in `Decisya.ServiceDefaults.Tests`, `Decisya.Api.Tests` or
`Decisya.AppHost.Tests` unless noted. "Lane" is the trait the test carries for
`--filter-trait`/`--filter-not-trait "Category=…"`: **Unit** (no explicit trait; this is
what the CI unit step and the manifest's re-run both call "Unit" for this issue, since
#15 adds no Testcontainers-backed Integration test) or **AppHost** (`[Trait("Category",
"AppHost")]`, host-only per ADR-0010, excluded from CI by `ci.yml`'s
`--filter-not-trait "Category=AppHost"`, G4-15-32).

### Stories 1-5 (Gherkin acceptance criteria)

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | The OTLP exporter activates when the AppHost supplies an endpoint | `OtlpExporterSelectionTests.Is_enabled_when_the_endpoint_is_set` | Unit |
| 1 | No exporter is wired when no OTLP endpoint is configured | `OtlpExporterSelectionTests.Is_disabled_when_the_endpoint_is_blank`, `.Is_disabled_when_the_endpoint_is_absent` | Unit |
| 1 | No vendor-specific exporter package is referenced | `PackageClosureTests.Decisya_Api_deps_json_lists_no_vendor_observability_package`; `ServiceDefaultsBoundaryTests.No_type_depends_on_a_vendor_observability_namespace`, `.Only_ServiceDefaults_depends_on_OpenTelemetry_among_the_assemblies_this_issue_touches`; `ApiBoundaryTests.Decisya_Api_does_not_depend_on_OpenTelemetry_directly` | Unit |
| 1 | Outgoing HTTP calls propagate trace context | `HttpClientPropagationTests.An_outgoing_call_made_inside_an_activity_carries_traceparent` | Unit |
| 2 | `/alive` reports liveness using only checks tagged "live" | `HealthEndpointTests.Alive_and_health_return_200_in_Development_when_every_check_is_healthy`; `.Health_returns_503_with_the_default_body_and_alive_still_returns_200` (an unhealthy, untagged check fails `/health` but not `/alive`, proving the "live"-only predicate) | Unit |
| 2 | `/health` reports readiness across every registered check | `HealthEndpointTests.Alive_and_health_return_200_in_Development_when_every_check_is_healthy` | Unit |
| 2 | `/health` reflects an unhealthy dependency | `HealthEndpointTests.Health_returns_503_with_the_default_body_and_alive_still_returns_200` | Unit |
| 2 | Health endpoints are not exposed outside Development by default | `HealthEndpointTests.Health_endpoints_return_404_outside_Development`, `.No_endpoint_is_mapped_outside_Development` | Unit |
| 3 | A log written during a traced request carries matching identifiers | `DecisyaJsonConsoleFormatterTests.Trace_and_span_ids_match_the_active_activity` | Unit |
| 3 | A log written outside any active trace carries no fabricated identifiers | `DecisyaJsonConsoleFormatterTests.Trace_and_span_ids_are_null_outside_any_activity` | Unit |
| 3 | Every stdout log line is well-formed JSON | `DecisyaJsonConsoleFormatterTests.The_line_is_exactly_one_well_formed_json_object_even_with_injection_attempts`, `.Non_finite_double_values_never_throw_and_still_produce_one_line`, `.A_lone_surrogate_never_throws_and_still_produces_one_parseable_line` | Unit |
| 4 | A field marked `[Sensitive]` is masked in the emitted log line | `SensitiveDataMaskingProcessorTests.A_sensitive_typed_value_is_masked_whole`; `DecisyaJsonConsoleFormatterTests.A_sensitive_value_is_masked_and_never_appears_raw`; `MaskingLogRecordProcessorTests.A_sensitive_value_is_masked_before_it_reaches_the_next_processor` | Unit |
| 4 | Every log record carries `tenant_id` and `user_id` fields, populated or not | `DecisyaJsonConsoleFormatterTests.Tenant_and_user_are_present_but_null_with_no_enrichment` | Unit |
| 4 | `user_id` is never logged raw | `LogEnrichmentContextTests.Begin_stores_the_tenant_and_a_hash_of_the_user_id_never_the_raw_id`; `UserIdHasherTests.Output_never_contains_the_input`; `DecisyaJsonConsoleFormatterTests.Tenant_and_user_are_populated_from_the_enrichment_context` | Unit |
| 4 | The masking hook fails closed | `SensitiveDataMaskingProcessorTests.A_getter_that_throws_is_masked_and_the_rest_of_the_object_survives`, `.ProcessValue_never_throws_and_reports_masked_on_failure`, `.Unstructured_state_masks_the_message_and_flags_the_record` | Unit |
| 5 | A call to the skeleton's `/alive` produces a trace visible in the Aspire dashboard | **manual** — Marco, on the host, per the G1 note: "Verifying 'the dashboard shows a trace' visually is a human/exploratory step." Automated proxy: `Decisya.AppHost.Tests.AppHostResourceTests.The_AppHost_injects_OTLP_configuration_and_a_health_call_produces_a_correlated_trace_and_log_line` (real AppHost, DCP and dashboard); `Decisya.Api.Tests.TracingTests.A_call_to_alive_produces_a_completed_server_span` (in-process proxy, no dashboard needed) | manual; proxies: AppHost, Unit |
| 5 | The AppHost supplies OTLP configuration without manual host setup | `AppHostResourceTests.The_AppHost_injects_OTLP_configuration_and_a_health_call_produces_a_correlated_trace_and_log_line` (asserts `OTEL_EXPORTER_OTLP_ENDPOINT`/`OTEL_SERVICE_NAME`/`OTEL_EXPORTER_OTLP_HEADERS` presence, no hash key); `AppHostConfigurationTests.AppHost_cs_adds_only_the_decisya_api_project_resource_with_no_secret_environment` (nothing hardcoded) | AppHost, Unit |
| 5 | The trace and the structured log agree | `AppHostResourceTests.The_AppHost_injects_OTLP_configuration_and_a_health_call_produces_a_correlated_trace_and_log_line` (same test; watches the resource's console stream for the known `trace_id`) | AppHost |
| 5 | The skeleton host carries no business surface yet | `HealthEndpointTests.Only_health_and_alive_are_exposed_and_neither_requires_authentication` (endpoint set, no auth). "No database connection": **manual/N/A** — `Decisya.Api` references no persistence package at all in #15 (there is nothing a test could assert against yet); #22 adds the first `DbContext` and the NetArchTest rule that would catch a regression | Unit; manual for the DB clause |

### G3 threat-model requirements that call for a test (G4-15-01 to 35)

| Requirement | Test(s) or manual reason | Lane |
| --- | --- | --- |
| G4-15-01 (endpoint set) | `HealthEndpointTests.Only_health_and_alive_are_exposed_and_neither_requires_authentication`; `.No_endpoint_is_mapped_outside_Development`; `.Health_endpoints_return_404_outside_Development` | Unit |
| G4-15-02 (health body) | `HealthEndpointTests.Health_returns_503_with_the_default_body_and_alive_still_returns_200` | Unit |
| G4-15-03 (Api launch profile) | `LaunchProfileAndConfigurationTests.Every_launch_profile_is_a_plain_localhost_project_profile_with_no_OTEL_or_Decisya_variable` | Unit |
| G4-15-04 (Api configuration files) | `LaunchProfileAndConfigurationTests.No_appsettings_file_carries_an_observability_or_console_formatter_override`, `.AllowedHosts_is_either_absent_or_the_wildcard` | Unit |
| G4-15-05 (no unsecured dashboard) | `AppHostConfigurationTests.No_AppHost_file_contains_an_unsecured_dashboard_setting`, `.Every_url_in_launchSettings_binds_to_localhost`, `.AppHost_cs_adds_only_the_decisya_api_project_resource_with_no_secret_environment`, `.No_mcp_configuration_file_exists_under_src_or_the_repo_root` | Unit |
| G4-15-06 (OTLP auth is on; host only) | `AppHostResourceTests.The_AppHost_injects_OTLP_configuration_and_a_health_call_produces_a_correlated_trace_and_log_line` — presence-only assertions on `OTEL_EXPORTER_OTLP_HEADERS`, never the value | AppHost |
| G4-15-07 (two sinks on the real host) | `Decisya.Api.Tests.LoggingProvidersTests.Exactly_one_console_and_one_OpenTelemetry_logger_provider_are_registered` | Unit |
| G4-15-08 (effective formatter) | `LoggingProvidersTests.The_console_formatter_never_switches_away_from_decisya_json` (in-process). The layer-4 clause ("every line in the `decisya-api` console log stream parses as JSON with the fixed field set") is **manual** — Marco checks *Console logs* in the dashboard (G2 note, step 4); `AppHostResourceTests` only pattern-matches one known line, not every line | Unit; manual for layer 4 |
| G4-15-09 (OTLP scopes stay off) | `ServiceDefaultsLoggingWiringTests.A_configuration_override_cannot_turn_OTLP_log_scopes_back_on` (PostConfigure). The "begin a scope with a canary, check it's absent from `/v1/logs`" e2e bullet has **no dedicated test** — flagged for G6, see Notes below | Unit; gap noted |
| G4-15-10 (processor order) | `MaskingLogRecordProcessorTests.A_sensitive_value_is_masked_before_it_reaches_the_next_processor`; `DecisyaJsonConsoleFormatterTests.A_sensitive_value_is_masked_and_never_appears_raw`. These are in-process harnesses standing in for the literal `/v1/traces`/`/v1/logs`/`/v1/metrics` network bodies the threat model describes (by design — see `MaskingLogRecordProcessorTests`'s own header comment on why no `OpenTelemetry.Exporter.InMemory` package is added) | Unit |
| G4-15-11 (collections) | `SensitiveDataMaskingProcessorTests.A_collection_of_sensitive_carrying_elements_is_masked_whole` (list/array/dictionary/enumerable/ArrayList), `.A_collection_of_scalars_passes_through` | Unit |
| G4-15-12 (no framework getters) | `SensitiveDataMaskingProcessorTests.A_framework_type_nested_in_a_Decisya_wrapper_is_masked_in_place_never_rendered` (representative framework type: `ArrayList`, not the literal `DefaultHttpContext`/`ClaimsPrincipal`/`HttpRequestMessage` examples — flagged for G6); `.A_getter_is_read_at_most_once_per_log_call`; `.ToString_on_a_type_the_processor_renders_is_never_called` | Unit; gap noted |
| G4-15-13 (bounds) — **closed this gate** | `SensitiveDataMaskingProcessorTests.Nesting_three_deep_still_masks_the_sensitive_leaf` (baseline, pre-existing); **new:** `.Nesting_one_level_beyond_the_depth_cap_masks_the_branch_whole_without_rendering_it` (depth), `.More_than_32_members_on_one_object_are_capped_and_the_remainder_becomes_mask` (member cap), `.More_than_256_rendered_nodes_in_one_record_exhausts_the_shared_budget` (node budget), `.A_string_longer_than_the_max_length_is_truncated_with_a_marker_in_the_shared_core` (8192-char truncation, SHOULD) | Unit |
| G4-15-14 (`AnyMasked`) | `SensitiveDataMaskingProcessorTests.AnyMasked_is_true_when_a_value_is_rendered_even_without_a_sensitive_hit`, `.AnyMasked_is_false_when_every_value_passes_through_unchanged` | Unit |
| G4-15-15 (attribute lookup) | `SensitiveDataMaskingProcessorTests.A_derived_record_overriding_a_sensitive_property_without_restating_the_attribute_still_masks` (override), `.A_sensitive_struct_held_as_nullable_is_masked` (`T?`), `.An_interface_member_marked_sensitive_masks_through_the_implementation` (interface). No test for "a `[Sensitive]` base class held in a property declared as `object`" specifically — flagged for G6 | Unit; gap noted |
| G4-15-16 (`Uri`) | `SensitiveDataMaskingProcessorTests.A_uri_loses_its_userinfo_query_and_fragment` | Unit |
| G4-15-17 (value-shape masking) | `SensitiveDataMaskingProcessorTests.A_JWT_shaped_substring_is_masked_even_under_an_innocent_key`, `.A_bearer_shaped_substring_is_masked_even_under_an_innocent_key` — both at the shared core, which both sinks call | Unit |
| G4-15-18 (deny-list) — **closed this gate** | Pre-existing: `SensitiveDataMaskingProcessorTests.A_denied_key_is_masked_in_state` (15 entries, state position), `.An_empty_query_string_is_not_counted_as_masked`, `.A_non_empty_query_string_is_masked`. **New:** `.A_denied_key_is_masked_in_scope_position` (same 15 entries, scope-key position via `ProcessValue`), `.A_denied_key_is_masked_as_a_nested_member_name` (14 entries, nested-member position via a dedicated probe type) | Unit |
| G4-15-19 (exception seam) | `SensitiveDataMaskingProcessorTests.The_exception_seam_masks_token_shapes_in_the_message_and_full_text_but_keeps_the_type_and_trace`, `.An_exception_passed_as_a_plain_state_value_is_masked_whole`; `MaskingLogRecordProcessorTests.The_exception_is_moved_into_masked_attributes_and_cleared_from_the_record` (OTLP side, `LogRecord.Exception == null`). No dedicated stdout-side test that runs a real `Exception` through `DecisyaJsonConsoleFormatter`'s `exception` field — flagged for G6 | Unit; gap noted |
| G4-15-20 (formatter never throws) | `DecisyaJsonConsoleFormatterTests.Non_finite_double_values_never_throw_and_still_produce_one_line`, `.A_lone_surrogate_never_throws_and_still_produces_one_parseable_line`. Neither test actually reaches `FormatFallback` (both are handled earlier, in `WriteValue`/`SafeToString`); no test exercises the top-level catch-all fallback line (`"attributes":{"decisya.masking_error":"formatter"}`) itself — flagged for G6 | Unit; gap noted |
| G4-15-21 (log injection) | `DecisyaJsonConsoleFormatterTests.The_line_is_exactly_one_well_formed_json_object_even_with_injection_attempts` — covers the message and a state value; the requirement also asks for the category, a key, the scope and the exception message as injection surfaces, not separately exercised — flagged for G6 | Unit; gap noted |
| G4-15-22 (masking errors visible) — **closed this gate** | **new:** `SensitiveDataMaskingProcessorTests.A_masking_error_increments_the_error_counter` (via `MeterListener` on `Decisya.ServiceDefaults`/`decisya.observability.masking_errors`). "Never logged through `ILogger`" is not separately tested; verifiable by inspection — `ObservabilityDiagnostics.cs` has no `ILogger` dependency at all | Unit |
| G4-15-23 (fail closed outside Development) | `DecisyaObservabilityOptionsValidatorTests` (all 6 tests); `ServiceDefaultsLoggingWiringTests.The_host_fails_to_start_outside_Development_without_a_hash_key` | Unit |
| G4-15-24 (Development key) | `UserIdHashKeyProvisionerTests` (all 5 tests); `ServiceDefaultsLoggingWiringTests.Development_generates_a_per_process_key_when_none_is_configured`. Two bullets are **not tested**: "after start, `IConfiguration[...]` is null" and "the warning appears exactly once" (`EphemeralUserIdHashKeyWarning`) — flagged for G6 | Unit; gap noted |
| G4-15-25 (hasher) | `UserIdHasherTests` (all 5 tests) | Unit |
| G4-15-26 (key hygiene) — **closed this gate** | **new:** `DecisyaJsonConsoleFormatterTests.A_DecisyaObservabilityOptions_instance_is_masked_end_to_end_on_stdout`, `MaskingLogRecordProcessorTests.A_DecisyaObservabilityOptions_instance_is_masked_end_to_end_on_OTLP` — a real `DecisyaObservabilityOptions` instance, key set, logged through each sink's real pipeline; the key never appears, `***` does. "The options type is a class, not a record" is verifiable by reading `DecisyaObservabilityOptions.cs`, not separately unit-tested | Unit |
| G4-15-27 (no key literals) | Tooling, not a unit test: `Canaries.HashKey()`/`.ShortHashKey()` generate at run time; `gitleaks detect` run this gate, clean | manual (tooling: gitleaks) |
| G4-15-28 (enrichment context) | `LogEnrichmentContextTests` (all 4 tests). The G6 check ("no code in `src/` reads `ILogEnrichmentContext`/`Baggage`/`tracestate` for authorization or tenant resolution") is a code-review item, not a test | Unit; G6 review item |
| G4-15-29 (spans stay minimal) | `TelemetryConfigurationTests.Extensions_cs_adds_no_span_enrichment_or_experimental_switch` (static); `TracingTests.A_call_to_alive_produces_a_completed_server_span` (loopback proxy). The literal `GET /alive?token=<canary>` — canary-absent-from-traces-and-logs assertion is **not present**; `TracingTests` doesn't send a query canary — flagged for G6 | Unit; gap noted |
| G4-15-30 (filter and prefix) | `TelemetryConfigurationTests.The_source_wildcard_is_Decisya_dot_star`, `.Extensions_cs_adds_no_span_enrichment_or_experimental_switch` (asserts no `.Filter =`) | Unit |
| G4-15-31 (export configuration never logged) | **manual/G6**, per the manifest's own G4 evidence ("G4-15-31: read only, for G6") | manual (G6) |
| G4-15-32 (CI filter) | **manual/G6** — the `ci.yml` diff size and the trait's scope are a diff-review item (`git diff`), not a unit test. The per-class-not-assembly trait deviation is recorded in `AssemblyInfo.cs` and the manifest's "API divergences" for G6 | manual (G6 diff review) |
| G4-15-33 (new package) | **manual** — `Directory.Packages.props` (single pin, test-only project) and the PR-body justification are reviewed by hand; the orchestrator's G4 re-run already recorded this in the manifest | manual |
| G4-15-34 (host review) | **manual by design** — a process control (`host-review.py` + `git diff` before F5/build/commit), not a code test | manual (process, ADR-0010) |
| G4-15-35 (docs) | **manual** — `docs/GETTING-STARTED.md` wording is a doc-review item | manual (doc review) |

### Notes for G6

The rows above marked "gap noted" are requirements with partial but not complete coverage
against the G3 threat model's literal test text. None of them was in backend-dev's four
reported gaps (G4-15-13, 18, 22, 26), which are the ones this gate was asked to close, and
none is a High in the threat model. Listed together here so G6's diff review can decide
whether any needs a follow-up issue rather than a blocking finding:
- G4-15-09: no e2e test begins a real `ILogger` scope carrying a canary and checks it is
  absent from an OTLP log record (only the `PostConfigure` unit test exists).
- G4-15-12: the "no framework getters" proof uses a representative framework type
  (`System.Collections.ArrayList`) rather than the threat model's named examples
  (`DefaultHttpContext`, `ClaimsPrincipal`, `HttpRequestMessage`).
- G4-15-15: no test for a `[Sensitive]` base class held in an `object`-typed property.
- G4-15-19: the exception seam is tested on the OTLP side and at the shared core, but not
  by running a real `Exception` through `DecisyaJsonConsoleFormatter`'s stdout `exception`
  field.
- G4-15-20: no test actually reaches `DecisyaJsonConsoleFormatter.FormatFallback` (the
  catch-all path); the two existing "never throws" tests are both handled earlier in
  `WriteValue`/`SafeToString`.
- G4-15-21: log-injection coverage is proven for the message and a state value, not
  separately for the category, a key, the scope or the exception message.
- G4-15-24: no test asserts `IConfiguration["Decisya:Observability:UserIdHashKey"]` stays
  `null` after a generated key, or that `EphemeralUserIdHashKeyWarning` logs exactly once.
- G4-15-29: no test sends `GET /alive?token=<canary>` and checks the canary is absent from
  the trace and log output of a real request.

<!-- gate: G5 | verdict: PASS | issue: #15 -->
