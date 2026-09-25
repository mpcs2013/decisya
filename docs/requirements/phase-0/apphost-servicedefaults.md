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
