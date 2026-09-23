---
name: otel-instrumentation
description: "Adds OpenTelemetry traces, metrics and structured logging to a module or handler following OTel semantic conventions, with a [Sensitive] masking test. Use whenever code is added that handles requests, messages, database calls, external providers or AI calls, or when the user mentions logging, tracing, metrics, dashboards or \"observability\"."
---
# otel-instrumentation

## Conventions
- One `ActivitySource("Decisya.<Module>")` and one `Meter("Decisya.<Module>")` per module, registered in `<Name>Module.cs` and added to the ServiceDefaults tracing/metrics builders.
- Span names: `<Module>.<UseCase>` (e.g. `Ledger.ImportStatement`). Attributes follow semantic conventions: `db.system`, `http.request.method`, `messaging.system=wolverine`, `gen_ai.system`, `gen_ai.request.model`, plus `decisya.tenant_id`.
- Metrics: `decisya.<module>.<noun>.<unit>` histograms for durations (`s`), counters for outcomes with a `result` attribute (`ok|error`). Never put a user id or amount in a metric attribute.
- Logs: `ILogger` with message templates and named placeholders; scopes carry `TenantId`; never string-interpolate.

## Steps
1. Wrap each handler in `using var activity = Source.StartActivity("<Module>.<UseCase>")`; record exceptions with `activity.AddException` and set status.
2. Add a duration histogram and outcome counter around the same boundary.
3. Mark PII properties with `[Sensitive]`; confirm the `SensitiveDataMaskingProcessor` in ServiceDefaults masks them by adding a test that logs the object and asserts the output contains `***`.
4. Run the AppHost and confirm the trace appears in the Aspire dashboard (CLI: `dotnet run --project src/Decisya.AppHost`; VS 2026: set AppHost as startup project and press F5).

## Never log
Raw transaction descriptions, account numbers, tokens, cookies, passwords, full prompts to AI models (log a hash and token count instead).
