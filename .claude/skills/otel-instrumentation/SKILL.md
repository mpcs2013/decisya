---
name: otel-instrumentation
description: "Adds OpenTelemetry traces, metrics and structured logging to a module or handler following OTel semantic conventions, with a [Sensitive] masking test. Use whenever code is added that handles requests, messages, database calls, external providers or AI calls, or when the user mentions logging, tracing, metrics, dashboards or \"observability\"."
---
# otel-instrumentation

## Prerequisites
Run `python .claude/scripts/prereqs.py otel-instrumentation` from the repository root (VS 2026: *View → Terminal*). If anything is missing, report the listed issues and stop; spans and metrics (steps 1–2) may still be added, the masking test (step 3) may not be faked.

## Conventions
- One `ActivitySource("Decisya.<Module>")` and one `Meter("Decisya.<Module>")` per module, declared in `<Name>Module.cs` (the `module-scaffold` skill creates them) and added to the ServiceDefaults tracing/metrics builders.
- Span names: `<Module>.<UseCase>` (e.g. `Ledger.ImportStatement`). Attributes follow semantic conventions: `db.system`, `http.request.method`, `messaging.system=wolverine`, `gen_ai.system`, `gen_ai.request.model`, plus `decisya.tenant_id`.
- Metrics: name `decisya.<module>.<noun>` with the unit in the instrument's `unit` field, not the name (histogram `decisya.ledger.import.duration`, unit `s`); counters for outcomes carry a `result` attribute (`ok|error`). Never put a user id or amount in a metric attribute.
- Logs: `ILogger` with message templates and named placeholders; scopes carry `TenantId`; never string-interpolate.

## Steps
1. Wrap each handler in `using var activity = <Name>Module.ActivitySource.StartActivity("<Module>.<UseCase>")`; record exceptions with `activity?.AddException(ex)` and set the status.
2. Add a duration histogram and an outcome counter around the same boundary.
3. Mark PII properties with `[Sensitive]`; prove the `SensitiveDataMaskingProcessor` in ServiceDefaults masks them with a test that logs the object and asserts the output contains `***`.
4. See the trace in the Aspire dashboard:

| Visual Studio 2026 | CLI |
| --- | --- |
| Set `Decisya.AppHost` as startup project, press F5; the dashboard opens in the browser | `dotnet run --project src/Decisya.AppHost`; open the dashboard URL (with its login token) printed in the console |

## Never log
Raw transaction descriptions, account numbers, tokens, cookies, passwords, full prompts to AI models (log a hash and token count instead).
