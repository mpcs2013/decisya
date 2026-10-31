---
name: otel-instrumentation
description: "Adds OpenTelemetry traces, metrics and structured logging to a component following OTel semantic conventions, without ever recording a token, cookie, secret or claim, and with the names the component publishes for its consumers. Use whenever code is added that handles requests, token calls, sessions, rate limiting or other security events, or when the user mentions logging, tracing, metrics or \"observability\"."
---
# otel-instrumentation

Part of the dotnet pack. A component emits telemetry; the consuming application's host collects and exports it.

## Prerequisites
Run `python .claude/scripts/prereqs.py otel-instrumentation` from the repository root. It checks the project's `prereqs.otel-instrumentation` entries in `.claude/project.json`. If anything is missing, report the listed issues and stop.
A listed agent cannot run `python` (core denies interpreters to listed agents), so it never runs this step: the orchestrator (the main session) runs it before the agent starts, or the agent reports that it is needed.

## Conventions
- One `ActivitySource` and one `Meter` per component, named after the component and published as public constants so a host can subscribe to them (`AddSource`, `AddMeter`).
- Instrument and event names an application owns are options of the component, not constants.
- Span names: `<Component>.<Operation>`. Attributes follow semantic conventions (`http.request.method`, `url.scheme`, `error.type`).
- Metrics: the unit in the instrument's `unit` field, not the name; counters for outcomes carry a `result` attribute (`ok|error`) or a closed reason code. Never put a user id, session key, token or claim in a metric attribute.
- Logs: `ILogger` with source-generated `LoggerMessage` methods, fixed templates and a closed list of fields; never string-interpolate, and never pass an exception parameter that could carry a token. User identity reaches a record only through the host's enrichment hook (hashed).

## Steps
1. Wrap the operation in `using var activity = <Telemetry>.ActivitySource.StartActivity("<Component>.<Operation>")`; record failures with `activity?.SetStatus(ActivityStatusCode.Error)` and a reason code, never the exception text.
2. Add a duration histogram and an outcome counter around the same boundary.
3. Mark options that hold secrets with the project's sensitive-data marker, and prove with a log-capture test that no token, cookie, secret or claim value reaches any record.
4. Check the spans in a test with an `ActivityListener` on the component's source.

## Never log
Tokens (access, refresh, ID, logout), cookies, session keys, client secrets, passwords, authorization headers, claims dictionaries, raw subject identifiers.
