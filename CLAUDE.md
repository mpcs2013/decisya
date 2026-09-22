# Decisya – Intelligent Financial Hub

AI-powered personal finance decision platform. Solo developer (Marco) plus Claude Code agents.
Stack: .NET 11 (RC1 go-live now, GA 10 Nov 2026), Aspire 13.5, Wolverine, EF Core 11, Postgres, Redis, Keycloak, MinIO, React (Vite), xUnit, Testcontainers, Playwright.
Architecture: Clean Architecture inside a hybrid modular monolith (`Modules.<Name>` + `Modules.<Name>.Contracts`), schema-per-module, NetArchTest-enforced boundaries.

Read `docs/adr/` before changing anything structural. Phase plan and gates: `docs/PHASE-0.md`.

## Platform invariants (tests enforce these; do not argue with them, propose an ADR)

1. Every persisted aggregate implements `ITenantScoped` and carries `TenantId`. Global query filters apply it. No cross-tenant query ever bypasses the filter without an `[AllowCrossTenant]`-attributed, audited admin handler.
2. Deterministic features (ledger, budgets, forecasts, alerts) never depend on the AI lane. Every AI feature degrades gracefully when `IChatClient` is unavailable.
3. No vendor AI SDK is referenced outside `Decisya.Infrastructure.Ai`. Application code targets `Microsoft.Extensions.AI` only.
4. Money is `Money` (decimal, minor units, ISO 4217). Time is NodaTime. `double` for amounts and `DateTime`/`DateTimeOffset.Now` are banned by analyzer.
5. No secrets in source. Configuration comes from environment variables or user-secrets. `.env` files are git-ignored.
6. Decisya gives no financial advice. AI output is informational, carries the standard disclaimer, and never emits buy/sell instructions for a specific instrument.

## Security principles (apply to every generated line)

- Session: BFF pattern. Cookie `HttpOnly; Secure; SameSite=Strict`; tokens live in the Redis ticket store; the SPA never receives a token. OIDC correlation/nonce cookies are `SameSite=Lax` (Strict breaks the callback).
- Internal JWTs: short-lived (≤ 5 min), RS256/ES256 only, validated for `iss`, `aud`, `exp`, and algorithm allow-list.
- Input: schema-based server-side validation on every endpoint; object-level authorization on every read and write (BOLA).
- Least privilege: one DB role per module schema; the API process cannot `DROP` or `ALTER`.
- Errors: generic message to the client, full detail to the structured log with trace id.

## Observability principles

- OpenTelemetry only (OTLP exporter); no vendor SDK.
- Logs are JSON to stdout with `trace_id`, `span_id`, `tenant_id`, hashed `user_id`.
- Mark PII with `[Sensitive]`; the log processor masks it. Never log raw transaction descriptions, tokens, cookies or passwords.
- Every module registers an `ActivitySource` and `Meter` named `Decisya.<Module>` following OTel semantic conventions.

## Working agreements

- One issue per PR, one phase per GitHub milestone, Conventional Commits.
- Tests ship in the same PR as the code they cover. Testcontainers for anything touching Postgres, Redis or Keycloak.
- When a build or test fails, report the exact compiler/test error with its code (e.g. `CS8618`, `NU1605`) and stop. Do not guess a fix that hides the error.
- Never add a NuGet or npm package without a one-line justification in the PR body. Prefer packages already in `Directory.Packages.props`.
- Document every developer step twice: Visual Studio 2026 UI path and CLI path, side by side.
- Do not recommend Chrome-specific tooling; Firefox is the default browser. Playwright runs Firefox and Chromium projects.

## Commands

- Build: `dotnet build -warnaserror`
- Test: `dotnet test` (integration tests need Docker running)
- Run: `dotnet run --project src/Decisya.AppHost`
- SPA: `cd src/Decisya.Web && npm ci && npm run dev`
- Format: `dotnet format` and `npm run lint`

## Agents and skills

Agents in `.claude/agents/`, skills in `.claude/skills/`. Standard order per issue:
product-owner → architect → security-reviewer (threat delta) → backend-dev / frontend-dev → test-engineer → security-reviewer (diff) → Marco reviews and merges.
