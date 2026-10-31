# otel-instrumentation addendum (Decisya)

Read with the dotnet pack's `otel-instrumentation` skill: Decisya's names and wiring (#246, ADR-0023). Where the pack's skill is stricter (no exception text in spans, source-generated logging), the skill wins.

## Triggers
Code that handles requests, messages, database calls, external providers or AI calls.

## Names
- One `ActivitySource("Decisya.<Module>")` and one `Meter("Decisya.<Module>")` per module, declared in `<Name>Module.cs` (the `module-scaffold` skill creates them) and added to the ServiceDefaults tracing and metrics builders.
- Span names `<Module>.<UseCase>` (e.g. `Ledger.ImportStatement`). Attributes follow the semantic conventions: `db.system`, `http.request.method`, `messaging.system=wolverine`, `gen_ai.system`, `gen_ai.request.model`, plus `decisya.tenant_id`.
- Metrics `decisya.<module>.<noun>` with the unit in the instrument's `unit` field (histogram `decisya.ledger.import.duration`, unit `s`); outcome counters carry `result` (`ok|error`). Never a user id or an amount in a metric attribute.

## Never log
Raw transaction descriptions, account numbers, tokens, cookies, passwords, full prompts to AI models (log a hash and a token count instead).

## Masking test
Mark PII properties with `[Sensitive]`; prove the `SensitiveDataMaskingProcessor` in ServiceDefaults masks them with a test that logs the object and asserts the output contains `***`.

## See the trace
| Visual Studio 2026 | CLI |
| --- | --- |
| Set `Decisya.AppHost` as startup project, press F5; the dashboard opens in the browser | `dotnet run --project src/Decisya.AppHost`; open the dashboard URL (with its login token) printed in the console |
