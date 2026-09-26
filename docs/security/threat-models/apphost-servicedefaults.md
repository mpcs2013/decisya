<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #15 -->
# Threat delta: Aspire AppHost + ServiceDefaults (OTel, health, JSON logs) (issue #15)

- Scope: what #15 adds: the `Decisya.Api` skeleton, `/health` and `/alive` (Development only), OTLP export, the `decisya-json` stdout formatter, `SensitiveDataMaskingProcessor` (fail closed), the `user_id` HMAC key, OTLP logs without scopes, removal of the template's health-trace filter, the AppHost resource wiring, and the `ci.yml` trait filter.
- Mode: threat delta, done **before** implementation. "Requirements for G4" is design input for backend-dev (G4) and test-engineer (G5). G6 checks each requirement against the diff.
- Inputs: `docs/requirements/phase-0/apphost-servicedefaults.md` (G1, Stories 1-5), NFR-10 to 12 in `docs/requirements/nfr.md`, `docs/architecture/apphost-servicedefaults.md` (G2), ADR-0001, 0003, 0005, 0009, 0010, the Security and Observability principles in `CLAUDE.md`, and `docs/security/threat-models/agent-sandbox.md` (host-executed files).
- Baseline: none for the application. This is the first application threat model; #27 (0.15) writes the baseline and should absorb it. Threat ids are T-xx and requirement ids are G4-15-xx, both local to this file.
- ASVS: 5.0, Level 2. Mapped at **section** level (for example V16.4 log protection, V13.3 secret management, V13.4 unintended information leakage), as in the earlier models. Check the requirement numbers against the official 5.0 text before copying them into a compliance artefact. V6 and V7 (Level 3 chapters) are not touched: #15 adds no authentication and no session.
- Reviewer: security-reviewer agent, 2026-09-25.

## Verdict

PASS-WITH-NOTES. No High is left without a mitigation. The four Highs (T-07, T-08, T-09, T-10) are all gaps in the masking design that would defeat NFR-11 as soon as a module logs a PII-bearing type. Each is closed by a requirement in #15 (G4-15-07 to 17). No High is deferred.

The G2 design is sound in structure: one masking core for both sinks, providers cleared, scopes kept out of OTLP, a keyed hash, and fail closed outside Development. G3 changes six details of it. See "Changes to the G2 design", which backend-dev follows where it differs from the architecture note:
1. collections at the top level of the state bypass rule 4 (T-09);
2. rule 4 calls getters on framework types (T-10);
3. `Uri` counted as a safe scalar (T-12);
4. `AnyMasked` must cover every value the processor renders, not only `[Sensitive]` hits (T-09);
5. the OTLP `IncludeScopes = false` can be overridden from configuration (T-08);
6. the formatter itself must never throw (T-06).

On the architect's four questions:
1. **Exception text is not masked.** Agreed that it is a gap, but #20's error middleware is **not** the control for it. That middleware decides what the *client* sees. The principle sends full detail to the *log*, so the log keeps the exception text. The real controls are: (a) no PII in exception messages, (b) data-access settings that keep row values out of exceptions (#22), and (c) a small masking seam for token-shaped strings in exception text, added in #15 (G4-15-19). Rated Medium today and High once Postgres lands, so #22 carries the configuration rules (T-14).
2. **The hash key is a new secret.** Accepted. Outside Development the host fails closed without it, so nothing can be deployed with a missing key. #29 (0.17) must provision it (T-15, follow-up F-3).
3. **The per-process Development key.** Accepted, with conditions: CSPRNG, never persisted or logged, and only when the key is *absent*. A present but invalid key must still fail (T-17, G4-15-24).
4. **The key deny-list is only a backstop.** Agreed. It is widened, applied to scope keys and nested member names, and paired with value-shape masking for JWTs and bearer strings (G4-15-17, 18). Plain-string PII logged under an innocent key name stays a residual risk. G6 review covers it for now, and an analyzer is a follow-up (T-13, F-7).

## Evidence (2026-09-25, code reading)

- `src/Decisya.ServiceDefaults/Extensions.cs` (the template as it stands):
  - OTLP logs have `IncludeScopes = true` today, so raw scope values reach OTLP. G2 turns this off.
  - The health-path `Filter` is present.
  - The Azure Monitor block is commented out.
  - `MapDefaultEndpoints` maps the health endpoints only under `IsDevelopment()`.
  - Health checks use the default response writer, which returns the status word only.
- `src/Decisya.AppHost`:
  - `AppHost.cs` adds no resource.
  - `launchSettings.json` binds the dashboard, OTLP and resource-service URLs to `localhost`.
  - The `http` profile has no `ASPIRE_ALLOW_UNSECURED_TRANSPORT`, and no committed file disables dashboard or OTLP authentication. G4-15-05 keeps it that way.
  - `UserSecretsId` is present. Agents never read `secrets.json`.
- `.github/workflows/ci.yml`:
  - line 104 is the unit step, which G4 changes;
  - line 111 is the integration step, `--filter-trait "Category=Integration"`, so `Category=AppHost` tests never run there either;
  - lines 96-100 are the vulnerable-package gate, which also covers `Aspire.Hosting.Testing`;
  - line 72 runs gitleaks.
- `docs/GETTING-STARTED.md:38` still says the Resources page is empty. G1 decision 1 corrects it.
- Framework behaviour the design relies on. G5 tests turn each of these into evidence:
  - `WebApplication` adds the developer exception page automatically in Development.
  - `ConsoleLoggerProvider` falls back to the built-in simple formatter when `FormatterName` doesn't match a registered formatter.
  - `ILoggingBuilder.AddOpenTelemetry` registers `OpenTelemetryLoggerOptions` for binding from `Logging:OpenTelemetry`.
  - The OTel .NET default propagator carries W3C `baggage` as well as `traceparent`.
  - The OTel ASP.NET Core and HttpClient instrumentations redact URL query values by default.

## Data flow and trust boundaries

```mermaid
flowchart LR
  subgraph HOST["Marco's Windows host (loopback only in #15)"]
    user(["Marco: Firefox or curl.exe"])
    subgraph AH["Decisya.AppHost (Aspire 13.5.4)"]
      dcp["DCP: launches resources,<br/>captures stdout"]
      dash["Aspire dashboard<br/>UI (browser token) + OTLP receiver (API key)"]
    end
    subgraph API["Decisya.Api skeleton (Development)"]
      hc["/health, /alive<br/>anonymous, status word only"]
      mel["ILogger → 2 providers"]
      fmt["decisya-json formatter<br/>(stdout)"]
      mlp["MaskingLogRecordProcessor → OTLP exporter"]
      core["SensitiveDataMaskingProcessor<br/>(single core)"]
      lec["ILogEnrichmentContext<br/>tenant_id, HMAC(user_id)"]
    end
    env[("Process env / config<br/>OTEL_*, Decisya__Observability__UserIdHashKey")]
  end
  user -- "TB1: HTTP GET, untrusted headers<br/>(traceparent, baggage, query)" --> hc
  user -- "TB4: dashboard login token" --> dash
  dcp -- "TB5: env incl. OTLP endpoint + API-key header" --> env --> API
  mel --> fmt --> core
  mel --> mlp --> core
  fmt -. reads .-> lec
  mlp -. reads .-> lec
  fmt -- "TB3: stdout JSON lines" --> dcp --> dash
  mlp -- "TB2: OTLP traces/metrics/logs" --> dash
  subgraph SBX["Agent sandbox (ADR-0010)"]
    g4["G4/G5 write src/, tests/, ci.yml"]
  end
  g4 -- "TB6: code executed on host<br/>(AppHost test, F5)" --> AH
  subgraph CI["GitHub Actions"]
    unit["unit step<br/>--filter-not-trait Category=AppHost"]
  end
```

Trust boundaries:
- **TB1: client → API.** Unauthenticated, Development only, loopback. Untrusted input: path, query, headers, `traceparent`, `baggage`. Later the internet → BFF → API path replaces it (#18, #19, #20).
- **TB2: API → OTLP receiver.** In #15 the receiver is the Aspire dashboard on loopback with API-key auth. Later it is any collector (#29), which is a new data store holding PII-adjacent data.
- **TB3: API stdout → DCP → dashboard console.** Later stdout goes to container logs.
- **TB4: Marco's browser → dashboard.**
- **TB5: configuration and environment → API.** This path carries the OTLP endpoint and headers and the hash key.
- **TB6: code written in the sandbox and executed on the host** (agent-sandbox T-03).
- **CI runner.** Not a new boundary. Only the test selection changes.

## Threats

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 id | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T-01 | `/health`, `/alive` | I | Monitoring endpoints outside Development would disclose dependency state. A custom response writer would leak check names and exception text. | Low | Mapped only under `IsDevelopment()`; default writer, status word only; 404 in other environments (G4-15-01, 02). #29 decides production probes. | V13.4 | Mitigated in #15; prod probes → #29 |
| T-02 | Environment switch | E, I | `ASPNETCORE_ENVIRONMENT=Development` in a deployed environment turns on three things at once: the health endpoints, the automatic developer exception page (stack traces to the client), and the ephemeral hash key. | Medium | An unset environment defaults to Production. The key check is `!IsDevelopment()`, so any other name, `Staging` or `Test` included, fails closed (G4-15-23). #29 pins the environment and adds a deployment check (F-3). | V13.4 | Mitigated for the key; rest → #29 |
| T-03 | `Decisya.Api` skeleton | S, E | The host has no authentication. When #20 adds auth, a missing fallback policy would leave business endpoints anonymous, or health would break behind auth. | Low | #15 exposes only the two health endpoints, and a test pins the endpoint set (G4-15-01). #20 adds a deny-by-default fallback policy with `AllowAnonymous` on health (F-1). | V8.2 | → #20 |
| T-04 | `launchSettings.json`, csproj written in the sandbox | T, E | The sandbox writes files that the host executes: F5 runs the launch profile, VS loads the csproj, Marco runs the AppHost test (agent-sandbox T-03). | Medium | Constrained launch profile (G4-15-03), host-review plus `git diff` before any host run (G4-15-34), ADR-0010 process. | V15.2 | Mitigated (process) |
| T-05 | stdout formatter | T, R | Log injection: CR/LF, U+2028/2029, U+0085 or ANSI escapes in a message, key, category or exception forge extra lines, spoof a top-level `level`/`trace_id`, or drive the terminal. | Medium | `Utf8JsonWriter` with `JavaScriptEncoder.Default`; every field goes through the writer; attributes nested; one key per name (G4-15-21). | V16.4 | Mitigated by requirements |
| T-06 | stdout formatter | D, R | The formatter throws on a non-finite `double` or an invalid UTF-16 string. MEL then throws an `AggregateException` into the calling code (the request fails) and the line is lost. | Medium | The formatter never throws and writes a fallback line (G4-15-20). | V16.5, V15.3 | Mitigated by requirements |
| T-07 | Logging providers | I | A third, unmasked sink appears: a provider added after `AddServiceDefaults`, a formatter-name mismatch that falls back to the simple formatter, or a configuration switch. One such path defeats NFR-11 for every record. | High | Exactly two providers asserted on the **real** `Decisya.Api` host; the effective formatter resolved and asserted, not just the option string; layer-4 check that every console line is our JSON (G4-15-07, 08). | V16.2 | Mitigated by requirements |
| T-08 | OTLP logs | I | Raw scope values bypass the masking processor. `IncludeScopes` or `IncludeFormattedMessage` could be switched back on through `Logging:OpenTelemetry` configuration or a later `Configure<OpenTelemetryLoggerOptions>`. | High | `PostConfigure` forces `IncludeScopes = false`; a configuration override test; scope canary absent from `/v1/logs` (G4-15-09, 10). | V16.2 | Mitigated by requirements |
| T-09 | Masking core, collections and `AnyMasked` | I | `List<T>`, arrays or dictionaries of types with `[Sensitive]` members pass rule 4, because their public properties (`Count`, `Capacity`) don't expose the elements. Rule 5 then passes them through, MEL joins the elements' `ToString()`, and record `ToString()` prints the sensitive members. The same leak happens if `AnyMasked` stays false for a value the processor rendered, because the MEL-formatted message is then used. | High | Collections handled explicitly; `AnyMasked` true for every processor-rendered or altered value (G4-15-11, 14). | V16.2, V14.2 | Mitigated by requirements |
| T-10 | Masking core, reflection | D, T, I | Rule 4 calls getters on arbitrary types. On `HttpContext`, `Request.Form` does a synchronous body read and `Session` throws. EF lazy-loading navigations issue queries. `JwtSecurityToken.RawData` is a plain `string`, so the processor would print the raw token. | High | Processor rendering only for Decisya-declared, anonymous and tuple types; everything else undetermined → `***`; node caps; JWT-shaped value masking (G4-15-12, 13, 17). | V15.3, V16.2 | Mitigated by requirements |
| T-11 | Masking core, attribute lookup | I | `[Sensitive]` is missed: `PropertyInfo.IsDefined` ignores `inherit`, so an override loses the attribute; the declared type is checked instead of the runtime type; `Nullable<T>` isn't unwrapped; an attribute on an interface member is silently ignored. | Medium | G4-15-15. | V16.2 | Mitigated by requirements |
| T-12 | Masking core, `Uri` as a scalar | I | `https://user:pass@host/p?token=…` passes rule 2 unchanged. | Medium | Strip userinfo, mask the query, drop the fragment (G4-15-16). | V16.2, V14.2 | Mitigated by requirements |
| T-13 | Deny-list backstop (architect item 4) | I | PII logged as a plain `string` under an innocent key (`{Email}`, `{Description}`) is never masked. `[Sensitive]` can't go on a parameter (by design, CS0592). The deny-list misses `pwd`, `credential`, `privatekey`, `bearer`, `jwt`, `session` and our own `UserIdHashKey`. | Medium | Wider deny-list, applied to scope keys and nested member names too; JWT/bearer value masking (G4-15-17, 18). G6 reviews every log call site. Follow-up: a Roslyn analyzer for PII-like placeholder names (F-7). | V16.2, V14.2 | Partially mitigated; residual accepted |
| T-14 | Exception text (architect item 1) | I | `exception.ToString()` on stdout and `exception.*` on OTLP are unmasked. Npgsql error detail and EF sensitive-data logging put row and parameter values (emails, amounts) into messages; libraries put tokens into messages. #20's middleware controls the client response, not the log. | Medium (#15: no data access); High once #22 adds a DbContext | Single exception seam with token masking in both sinks (G4-15-19). "No PII in exception messages" coding rule (G6). #22: Npgsql `Include Error Detail` off and `EnableSensitiveDataLogging` off outside Development, with tests (F-2). #20: generic ProblemDetails to the client (F-1). | V16.5, V16.2 | Partially in #15; → #20, #22 |
| T-15 | `UserIdHashKey` (architect item 2) | I | If the key leaks, anyone holding it can reverse `user_id` hashes by hashing a dictionary of known ids (emails, usernames, subject ids). The key could leak through configuration dumps, logged options, record `ToString()`, validation messages, test literals or the dashboard's environment view. | Medium | Never in source; `[Sensitive]` on the option; validation messages without the value; runtime-generated test keys (G4-15-23, 26, 27). #29: secret store, per-environment key, least privilege, rotation procedure (F-3). | V13.3, V11.1 | Mitigated in #15; provisioning → #29 |
| T-16 | Missing key outside Development | D | The host refuses to start. This is intended fail-closed behaviour, and it is also a deployment outage risk. | Low | A clear error names the setting (G4-15-23). #29 provisions the key before the first deploy. | V16.5 | Accepted by design |
| T-17 | Per-process Development key (architect item 3) | R, I | Weak randomness; the key persisted into `IConfiguration`, a log or a file; a *present but malformed* key silently replaced; hashes not correlatable across hosts or restarts. | Low | CSPRNG, 32 bytes, in memory only; generated only when the key is absent; one warning with no key material (G4-15-24). Loss of correlation in Development is accepted. | V11.5, V13.3 | Mitigated by requirements |
| T-18 | `user_id` pipeline | I | The raw id sits in the `AsyncLocal` or is hashed without a key. An empty id hashes to a constant that looks like a real user. | Medium | Only the hash is stored; empty → `null`; HMAC only (G4-15-25, 28). | V16.2 | Mitigated by requirements |
| T-19 | `ILogEnrichmentContext` | T, E | Any code can call `Begin` and put a false `tenant_id` into logs. Code might later trust the log context, `Baggage` or `tracestate` for authorization or tenant resolution. | Low | No production caller in #15; nothing reads these for decisions (G4-15-28). Logs are not the audit trail; #24 is. | V8.2, V16.4 | Mitigated by requirements |
| T-20 | Trace context from untrusted clients | T, I | Clients choose `traceparent` (colliding with or polluting other traces) and `baggage`. Baggage is extracted and then **forwarded** on outgoing `HttpClient` calls (to Keycloak, and later to AI and aggregator vendors). A future `ParentBased` ratio sampler would honour the client's sampled flag. | Medium (no internet exposure in #15) | No decision reads baggage (G4-15-28). #19: the BFF starts a new trace root and drops browser `traceparent`, `tracestate` and `baggage` (F-4). #29: sampler choice (F-3). | V4.1, V15.3 | → #19, #29 |
| T-21 | Span attributes | I | The masking core covers **logs only**. Span attributes (`url.path`, `url.query`, `user_agent.original`, possibly `client.address`, exception events) go to OTLP unmasked. In #15 the only paths are the health paths. | Medium | No enrichment, no `RecordException`, query redaction left on; e2e canary test (G4-15-29). #20 defines the span-attribute policy for business routes: route templates, no ids in span names (F-1). | V16.2, V14.2 | Mitigated for #15; → #20 |
| T-22 | Health-trace filter removed | D | Probe traffic in production would flood the collector and the trace storage. | Low | Dev only; no `WithHttpHealthCheck` in #15 (G2); #29 decides probe exposure and sampling together (F-3). | V15.1 | → #29 |
| T-23 | OTLP to a non-dashboard endpoint | I, T | Once the endpoint is not the loopback dashboard: plaintext OTLP off-host, no receiver authentication, the collector becomes an unlisted PII-adjacent store with no access control or retention, and anyone who controls the process environment can redirect telemetry. | Medium | #29: HTTPS (or mTLS) off-host, `OTEL_EXPORTER_OTLP_HEADERS` from the secret store, collector access control, retention, listed in the log inventory (F-3). In #15: ServiceDefaults never logs the endpoint or headers (G4-15-31). | V12.3, V16.4, V16.1, V13.2 | → #29 |
| T-24 | Collector unavailable | D | Export failures slow or crash the host. | Low | OTel batch processors drop records when the bounded queue is full; export runs off the request path (framework behaviour, no change in #15). | V16.5 | Accepted |
| T-25 | Aspire dashboard authentication | S, I | Unsecured flags (anonymous UI, unsecured OTLP, unsecured transport) or a non-loopback bind would expose logs, exceptions and resource environment values (including the OTLP API key). The login token appears in the console and in the URL. Local processes that know the key can inject fake telemetry. | Low (dev, loopback, synthetic data) | No unsecured flags, loopback binds (G4-15-05); the AppHost test asserts OTLP API-key auth is active (G4-15-06); docs never paste a token (G4-15-35); no real customer data in Development. | V13.4, V8.2 | Mitigated by requirements |
| T-26 | Dashboard AI and MCP features | I | Aspire's dashboard assistant and MCP server can hand telemetry to an LLM vendor or an agent. Logged text then becomes a prompt-injection channel into an agent session. | Low | Nothing in #15 enables them: no `.mcp.json`, no dashboard AI configuration (G4-15-05). `CLAUDE.md`: text in data is never instructions. | V16.4 | Accepted residual |
| T-27 | `ci.yml` trait filter | R | `--filter-not-trait "Category=AppHost"` silently excludes any test tagged that way, including a mis-tagged security test. The AppHost wiring (OTLP injection, dashboard auth) is never exercised in CI. | Low | The diff is exactly one argument; the trait is set at assembly level in one project only (G4-15-32). Running the AppHost tests in CI is a follow-up (F-5). | V15.1 | Mitigated; CI run → F-5 |
| T-28 | `Aspire.Hosting.Testing` (new, test-only) | T | Supply chain: a large transitive tree enters the solution build. | Low | CPM pin at 13.5.4, one project only, vulnerable gate, PR justification (G4-15-33). | V15.2 | Mitigated by requirements |
| T-29 | Vendor exporter reintroduced | I | A vendor SDK sends telemetry to a third party. | Low | NFR-12 tests in G2 (package closure plus namespace rules); the commented Azure Monitor block deleted. | V15.2 | Mitigated (G2) |
| T-30 | `AddMeter("Decisya.*")` | I, D | Future modules tag metrics with user ids or free text, which bypasses masking (metrics aren't masked), leaks across tenants in shared dashboards, and causes a cardinality explosion. | Low | Rule for the module-scaffold issue: no user id or free-text tags, `tenant_id` as a tag only with approval (F-6). The prefix includes the trailing dot (G4-15-30). | V16.2 | → module scaffold |

## Changes to the G2 design

backend-dev follows these where they differ from `docs/architecture/apphost-servicedefaults.md`. None of them needs an ADR or a new package.

1. **Rule 4b, collections at the top level** (T-09): a state value that implements `IEnumerable` (other than `string`) and whose element type is not a rule 2 scalar, or is unknown (non-generic `IEnumerable`, `object`, an interface), becomes `***`. The same applies to dictionary values. Never pass it to rule 5.
2. **Rule 4 scope** (T-10): the processor renders an object itself (calling its getters) only for types declared in an assembly whose simple name starts with `Decisya.`, for anonymous types, and for `ValueTuple`, `Tuple` and `KeyValuePair`. Any other non-scalar type that rule 4 would select (graph undetermined, or containing `[Sensitive]`) becomes `***` whole. Framework and third-party types therefore never have their getters called.
3. **Rule 2, `Uri`** (T-12): render `Uri` as scheme, host, port and path. The userinfo is removed, a present query becomes `?***`, and the fragment is dropped. A relative `Uri` keeps its path and gets the same query rule.
4. **`AnyMasked`** (T-09): true whenever *any* value in the record was replaced, truncated or rendered by the processor (rules 1, 3, 4, 4b, 6, 7, the `Uri` rule and value-shape masking). It is false only when every value passed through unchanged.
5. **OTLP options** (T-08): `PostConfigure<OpenTelemetryLoggerOptions>` sets `IncludeScopes = false`, just as `PostConfigure<ConsoleLoggerOptions>` sets the formatter name.
6. **Exceptions** (T-14): one seam, `SensitiveDataMaskingProcessor.ProcessException(Exception) → (string Type, string Message, string StackTrace)`, feeds both sinks. On OTLP, the adapter writes `exception.type`, `exception.message` and `exception.stacktrace` attributes from the seam and sets `LogRecord.Exception = null`.
7. **Formatter** (T-06): wrap `Write` so that it never throws (G4-15-20).

## Requirements for G4

MUST unless marked SHOULD. Each requirement needs a test, or a G6 check where noted, and G4 records it in the manifest's G4 evidence.
- Test canaries (fake secrets, tokens, emails) are built at run time from fragments. No file in the diff contains a realistic secret literal.
- Tests that run in the sandbox (unit and loopback e2e) must not carry `Category=AppHost`. Only layer 4 does.

### Host surface: skeleton, health, launch configuration (T-01 to T-04)

- **G4-15-01 (endpoint set).** In Development, `Decisya.Api` exposes exactly `/health` and `/alive` (G2 test). In `Production`, `Staging` and a custom environment name `Test`, the endpoint data source has **zero** endpoints and both paths return 404. `Program.cs` adds no middleware beyond what G2 lists.
- **G4-15-02 (health body).** Health endpoints use the default response writer. Test: register a failing check whose exception message and description contain a canary. `/health` returns 503, and the body is exactly `Unhealthy` and doesn't contain the canary. No `ResponseWriter`, no `HealthCheckOptions.ResultStatusCodes` change, and no health-check UI package.
- **G4-15-03 (Api launch profile).** Static test over `src/Decisya.Api/Properties/launchSettings.json`:
  - every profile has `commandName` = `Project`;
  - no `executablePath`, `commandLineArgs` or `workingDirectory`;
  - every `applicationUrl` host is `localhost` (no `0.0.0.0`, `*`, `+` or `[::]`);
  - `environmentVariables` holds only `ASPNETCORE_ENVIRONMENT`;
  - no key starts with `OTEL_` or `Decisya__`.
- **G4-15-04 (Api configuration files).** Static test: `appsettings*.json` in `src/Decisya.Api` contains no `Decisya:Observability` section, no `Logging:Console:FormatterName`, no `Logging:Console:FormatterOptions` and no `Logging:OpenTelemetry` section. `AllowedHosts` is either absent or `"*"`; #29 owns the real value.

### AppHost and dashboard (T-25, T-26)

- **G4-15-05 (no unsecured dashboard).** Static test, which runs in CI (not `Category=AppHost`), over every file under `src/Decisya.AppHost` (`launchSettings.json`, `appsettings*.json`, `aspire.config.json`, `AppHost.cs`):
  - Case-insensitive, none of these appear: `ASPIRE_ALLOW_UNSECURED_TRANSPORT`, `UNSECURED_ALLOW_ANONYMOUS`, `AuthMode` (any `Dashboard:Frontend:AuthMode` or `Dashboard:Otlp:AuthMode` setting, or their env forms), `ASPIRE_DASHBOARD_MCP`, `ASPIRE_DASHBOARD_AI`.
  - Every URL in `launchSettings.json` has host `localhost`.
  - `AppHost.cs` contains only the `AddProject<Projects.Decisya_Api>("decisya-api")` resource line from G2, with no `WithEnvironment` passing a secret. No `.mcp.json` is added anywhere.
- **G4-15-06 (OTLP auth is on; layer 4, host only).** The AppHost test asserts that `decisya-api`'s environment holds `OTEL_EXPORTER_OTLP_HEADERS` (API-key auth to the dashboard is active) and holds no `Decisya__Observability__UserIdHashKey`. It asserts presence only: it never writes the header value to test output or to an assertion message. If Aspire 13.5.4 doesn't inject the header, report it and stop, and G6 records it as a finding against T-25.

### Logging pipeline integrity (T-07, T-08)

- **G4-15-07 (two sinks on the real host).** In `Decisya.Api.Tests`, using the `WebApplicationFactory<Program>` services, `IEnumerable<ILoggerProvider>` contains exactly one `ConsoleLoggerProvider` and one `OpenTelemetryLoggerProvider`. A provider the test harness itself adds may be excluded by exact type name only. This pins the rule on the host that #20 will extend, not just on ServiceDefaults.
- **G4-15-08 (effective formatter).** With configuration `Logging:Console:FormatterName` set to `simple`, then `json`, then `systemd`:
  - `IOptionsMonitor<ConsoleLoggerOptions>.CurrentValue.FormatterName == "decisya-json"`;
  - exactly one registered `ConsoleFormatter` has that name, and it is a `DecisyaJsonConsoleFormatter`;
  - `DecisyaJsonConsoleFormatterOptions` has no member that disables masking or selects the encoder.

  Layer 4 (host only): every line in the `decisya-api` console log stream during the test parses as a JSON object that holds the fixed G2 field set.
- **G4-15-09 (OTLP scopes stay off).** `PostConfigure<OpenTelemetryLoggerOptions>` forces `IncludeScopes = false`. Test:
  - set configuration `Logging:OpenTelemetry:IncludeScopes=true`, then check that the resolved options have `false`;
  - in the loopback e2e, begin a scope whose value is a canary, log inside it, and check that the canary is absent from the `/v1/logs` body;
  - the same scope value on stdout appears only as processed by the masking core.
- **G4-15-10 (processor order).** The e2e asserts that a `[Sensitive]` canary is absent from the `/v1/traces`, `/v1/logs` and `/v1/metrics` bodies and from every captured stdout line, and that `***` is present in `/v1/logs` (extends G2 layer 2).

### Masking core (T-09 to T-13)

- **G4-15-11 (collections).** Implement change 1. Tests, each with a record type that has a `[Sensitive]` member and a canary value:
  - `List<T>`, `T[]`, `Dictionary<string, T>`, `IEnumerable<T>` from a LINQ query, and a non-generic `ArrayList` all become `***`;
  - `int[]` and `List<string>` pass through;
  - the canary never appears in either sink.
- **G4-15-12 (no framework getters).** Implement change 2. Tests:
  - `DefaultHttpContext`, `ClaimsPrincipal` and `HttpRequestMessage` become `***`;
  - a Decisya-declared test type whose getter counts its calls is read at most once per log call;
  - a type whose `ToString()` returns a canary never has `ToString()` called when rule 4 applies.
- **G4-15-13 (bounds).** Processor rendering stops after 32 members per object and 256 rendered nodes per record. The remainder becomes `***`, which sets `AnyMasked`. SHOULD: string values longer than 8 192 characters are truncated with a `…[truncated]` marker, in both sinks.
- **G4-15-14 (`AnyMasked`).** Implement change 4. Test: a Decisya type with an `object`-typed property holding a non-sensitive value, and no `[Sensitive]` hit. The message is the template, not the MEL-formatted text, on both sinks.
- **G4-15-15 (attribute lookup).** Look up `[Sensitive]` on the **runtime** type with `Attribute.IsDefined(member, typeof(SensitiveAttribute), inherit: true)`, not `MemberInfo.IsDefined`. Unwrap `Nullable<T>`. Tests:
  - a derived record that overrides a `[Sensitive]` property without restating the attribute;
  - a `[Sensitive]` base class held in a property declared as `object`;
  - a `[Sensitive]` struct held as `T?`.

  `[Sensitive]` on an interface member is either honoured through the interface map or rejected by a G5 architecture test. It is never silently ignored.
- **G4-15-16 (`Uri`).** Implement change 3. Test: `https://u:p@h.example/a?token=x#f` renders as `https://h.example/a?***` in both sinks. `u`, `p`, `token=x` and `#f` appear nowhere.
- **G4-15-17 (value-shape masking).** Any string value (state, scope, nested member, exception text through G4-15-19) that contains a JWT-shaped match (`eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*`) or a `Bearer <token>` match (case-insensitive) has the match replaced by `***`. This sets `AnyMasked`. Tests: both shapes, embedded in a longer sentence, in both sinks.
- **G4-15-18 (deny-list).**
  - Add `pwd`, `credential`, `privatekey`, `signingkey`, `hashkey`, `bearer`, `jwt` and `session` to the G2 list.
  - Apply the list to state keys, scope keys, and member names inside processor-rendered objects.
  - Tests: one per entry, each in all three positions. Also check that `QueryString` from ASP.NET Core's "Request starting" line is masked when non-empty.
  - SHOULD: a null or empty value under a deny-listed key is not counted as masked, so ordinary "Request starting" lines keep their readable message.

### Exceptions and robustness (T-05, T-06, T-14)

- **G4-15-19 (exception seam).** Implement change 6.
  - The seam applies G4-15-17 to the message and to the full `ToString()` text, inner exceptions included.
  - Stdout writes the seam output in `exception`. OTLP writes the three `exception.*` attributes and has `LogRecord.Exception == null` when it reaches the exporter.
  - Test: an exception whose message and inner-exception message carry a JWT canary. The canary is absent from stdout and from `/v1/logs`, and `exception.type` and the stack trace are still present.
  - An `Exception` passed as a *state value* (not the `exception` argument) becomes `***` under G4-15-12.
  - If `LogRecord.Exception` can't be set in OpenTelemetry 1.19.x, report the exact compiler error and stop (CLAUDE.md). G6 then records T-14 as a note.
- **G4-15-20 (formatter never throws).** Any exception inside `DecisyaJsonConsoleFormatter.Write` is caught. The formatter then writes one fallback JSON line with `timestamp`, `level`, `category`, `trace_id`, `span_id`, a `null` message and `"attributes":{"decisya.masking_error":"formatter"}`. Tests, each asserting one parseable line and no exception at the `ILogger` call:
  - `double.NaN`, `double.PositiveInfinity` and `float.NaN` values;
  - a lone surrogate `"\uD800"` in the message, in a key and in a value;
  - a value whose getter throws.
- **G4-15-21 (log injection).** The formatter uses `Utf8JsonWriter` with `JavaScriptEncoder.Default` (or a stricter encoder), never `UnsafeRelaxedJsonEscaping`. Every string field (keys included) goes through the writer. Attributes are nested under `attributes`, and each key is written once (state wins over scope). Test inputs, placed in the message, a key, a value, the category, the scope and the exception message: `\r\n`, ` `, ` `, `\u0085`, `\u001b[31m`, and `"},"level":"Critical","x":{"`. Assertions:
  - the output is exactly one `\n`-terminated line and parses with `JsonDocument.Parse`;
  - the top-level `level` equals the real level;
  - the raw bytes contain no 0x1B, no U+2028/U+2029 and no U+0085;
  - no duplicate property name exists at any level.
- **G4-15-22 (masking errors visible) SHOULD.** Masking errors increment a counter `decisya.observability.masking_errors` on a `Meter` named `Decisya.ServiceDefaults`, tagged with the rule. The counter is never logged through `ILogger`.

### Hash key and enrichment (T-15 to T-19)

- **G4-15-23 (fail closed outside Development).**
  - When `!IHostEnvironment.IsDevelopment()`, a missing, blank, non-base64 or shorter-than-32-byte `Decisya:Observability:UserIdHashKey` fails `ValidateOnStart`, and the host does not start.
  - Tests: `Production`, `Staging` and `Test`, each with the four bad values.
  - The `OptionsValidationException` message, its `ToString()` and every captured log line name the setting and the rule, but never the value. Test with a canary as the bad value.
  - `FormatException` from base64 decoding is caught inside validation and reported as the generic rule.
- **G4-15-24 (Development key).**
  - Only an **absent or blank** key triggers generation. A present but invalid key fails, in Development too.
  - The key is 32 bytes from `RandomNumberGenerator`. It is held only in the options instance or the hasher, never written to `IConfiguration`, a file, an environment variable or a log.
  - The single `Warning` contains no key material.
  - Tests: after start, `IConfiguration["Decisya:Observability:UserIdHashKey"]` is null; the warning appears exactly once; two host instances hash the same id differently.
- **G4-15-25 (hasher).**
  - Use the static `HMACSHA256.HashData(key, data)`, with no shared `HMAC` instance.
  - Output: the first 16 bytes as 32 lowercase hex characters.
  - A null or empty user id gives `user_id = null`, not a hash.
  - SHOULD: prefix the input with a fixed domain label (`"decisya.user_id.v1\0"`), so the key can't be repurposed across contexts.
  - Tests: deterministic for a fixed key; changes with the key; the output never contains the input; empty → null.
- **G4-15-26 (key hygiene).** `DecisyaObservabilityOptions.UserIdHashKey` carries `[Sensitive]`. The options type is a class, not a record, or it overrides `ToString()` without the key. Test: logging the options object through the pipeline yields `***` for the key in both sinks.
- **G4-15-27 (no key literals).** Test keys are generated at run time with `RandomNumberGenerator`. No base64 key literal appears in `src/` or `tests/`, and gitleaks stays green.
- **G4-15-28 (enrichment context).**
  - `ILogEnrichmentContext` stores only the hash, never the raw id.
  - `Begin` returns a handle whose `Dispose` restores the previous values (nesting test), and two concurrent flows don't see each other's values (parallel test).
  - No production code in #15 calls `Begin`.
  - G6 check: no code in `src/` reads `ILogEnrichmentContext`, `Baggage.Current`, `Activity.Baggage`, `Activity.TraceStateString` or the `baggage`/`tracestate` headers for authorization, tenant resolution or any branch.

### Tracing and export (T-20 to T-23, T-30)

- **G4-15-29 (spans stay minimal).**
  - No `EnrichWithHttpRequest`, `EnrichWithHttpResponse` or `EnrichWithException`, no `RecordException = true`, and no custom span processor in `src/`.
  - No `OTEL_DOTNET_EXPERIMENTAL_*` variable, and no `DisableUriRedaction` or `DisableUrlQueryRedaction` switch, in any committed file.
  - Loopback e2e test: `GET /alive?token=<canary>` returns 200, and the canary is absent from the `/v1/traces` and `/v1/logs` bodies and from stdout.
- **G4-15-30 (filter and prefix).** The health `Filter` is removed and no other tracing filter is added. The source and meter wildcard is `"Decisya.*"`, from the `DecisyaTelemetry.SourcePrefix` constant that includes the trailing dot, so `DecisyaFoo` doesn't match.
- **G4-15-31 (export configuration never logged).** ServiceDefaults never logs the values of `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` or any `OTEL_*` variable. G6 check.

### CI, packages, host execution, docs (T-04, T-27, T-28)

- **G4-15-32 (CI filter).**
  - The `ci.yml` diff is exactly one added argument, `--filter-not-trait "Category=AppHost"`, on the unit step at line 104. It changes no `permissions`, trigger, secret, action or other step.
  - The trait is applied once at assembly level in `Decisya.AppHost.Tests` (`[assembly: AssemblyTrait("Category", "AppHost")]`).
  - G6 check: no other test project uses `Category=AppHost`.
- **G4-15-33 (new package).**
  - `Aspire.Hosting.Testing` is added as a `PackageVersion` at `13.5.4` in `Directory.Packages.props`, with no `Version=` in any csproj.
  - It is referenced only by `tests/Decisya.AppHost.Tests`.
  - The PR body carries the one-line justification, and the vulnerable-package step stays green.
- **G4-15-34 (host review).**
  - The new csproj files and `launchSettings.json` contain none of the patterns `host-review.py` flags (`<Exec`, `<UsingTask`, `TaskFactory`, `<Analyzer`, `Import Project=`).
  - Marco runs `host-review.py` and reads `git diff` before F5, before the layer-4 test and before any host build (ADR-0010).
  - G4 records in the manifest that layer 4 and the dashboard check are pending on the host.
- **G4-15-35 (docs).** The `GETTING-STARTED.md` update refers to "the login URL with its token from the console" without pasting a URL, port-bound token or `t=` value. It never suggests disabling dashboard authentication or `ASPIRE_ALLOW_UNSECURED_TRANSPORT`. It shows each step in both columns (VS 2026 and CLI).

## Follow-ups (link, don't implement in #15)

| Id | Issue | What it must carry from this model |
| --- | --- | --- |
| F-1 | #20 (0.08) | Deny-by-default fallback authorization policy with `AllowAnonymous` on health (T-03). Generic ProblemDetails with no exception text to the client (T-14). Span-attribute policy for business routes: route templates, no ids in span names or custom attributes (T-21). SHOULD: `AddServerHeader = false`. |
| F-2 | #22 (0.10), the first `DbContext` | Npgsql `Include Error Detail` off and `EnableSensitiveDataLogging` / `EnableDetailedErrors` off outside Development, each with a test (T-14, which becomes High here). EF command logging at `Warning`, so parameter values never reach logs. |
| F-3 | #29 (0.17) | Provision `UserIdHashKey` from the secret store: per environment, never shared with another key, least-privilege access, a documented rotation that accepts the loss of cross-rotation correlation (T-15, T-16). Pin a non-Development environment and add a startup or deployment check (T-02). Production probes, probe tracing and sampling; no client-driven `ParentBased` sampling (T-20, T-22). OTLP over HTTPS or mTLS off-host, headers as a secret, collector access control and retention, and a log inventory entry (T-23; V16.1, V16.4). `AllowedHosts`. |
| F-4 | #19 (0.07) and #18 (0.06) | The BFF drops browser-supplied `traceparent`, `tracestate` and `baggage` and starts a new root. YARP does not forward those headers from the browser (T-20). |
| F-5 | New issue (CI) or #28 (0.16) | Install the Aspire CLI in CI and run `Category=AppHost` (T-27). |
| F-6 | Module-scaffold issue / `otel-instrumentation` skill (host session) | Metric tag rule (T-30). Reword the skill's Logs line to "`ILogEnrichmentContext` carries the tenant" (G2 follow-up). |
| F-7 | New issue | A Roslyn analyzer or NetArchTest rule that flags log placeholders with PII-like names (`Email`, `Iban`, `Description`, `Name`, …) whose argument is a plain `string` (T-13). |
| F-8 | #27 (0.15) | Absorb this delta into the baseline threat model and the log inventory (V16.1). |

## Done-when testability

The literal Done-when (a trace for a health call in the dashboard) is a manual host check. Its automated proxies are:
- G2 layer 2 in the sandbox: the fake OTLP receiver gets the known trace id;
- G2 layer 4 on the host: the real AppHost injects OTLP and the stdout line carries the trace id.

The security requirements above run in layers 1-3 and in CI, except G4-15-06 and the layer-4 part of G4-15-08. Those two are host-only until F-5 lands.

## Residual risk after #15

- Plain-string PII logged under an innocent placeholder name is not masked (T-13). G6 review covers it until F-7 lands.
- Exception text is masked only for token shapes. Other PII in exception messages depends on coding discipline and on #22's data-access settings (T-14).
- Span attributes and metrics are outside the masking core (T-21, T-30). They are safe in #15 because only the health paths exist.
- A compromised `UserIdHashKey` makes stored `user_id` hashes reversible for known ids. Rotation limits future exposure but doesn't reach back (T-15).
- The AppHost wiring and the dashboard-auth assertion run only on Marco's host until F-5 (T-27).
- Code written in the sandbox runs on the host at F5 and in the layer-4 test. The control is `host-review.py` plus Marco's diff review (T-04, agent-sandbox T-03).
