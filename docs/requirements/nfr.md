# Non-functional requirements (ISO/IEC 25010)

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-01 | Performance efficiency | p95 API latency < 300 ms at 50 concurrent users on the reference VPS | k6 baseline (Phase 1) |
| NFR-02 | Reliability | RPO 24 h, RTO 4 h | Restore drill (0.18) |
| NFR-03 | Security | ASVS L2 with zero High | asvs-checklist |
| NFR-04 | Usability | WCAG 2.2 AA | axe-core in Playwright |
| NFR-05 | Maintainability | Architecture tests green; no module cycles | NetArchTest |
| NFR-06 | Portability | Deploys unchanged on Compose and K3s | Phase 0 exit / later drill |
| NFR-07 | Maintainability | ≥ 95% line coverage on `Decisya.SharedKernel` Money/Currency/Clock types | Microsoft.Testing.Platform code coverage (`Microsoft.Testing.Extensions.CodeCoverage`, `dotnet test --coverage`) report (0.04) |
| NFR-08 | Functional suitability | `Money.Allocate` output sums to exactly the input amount (zero-drift) across ≥ 10,000 randomized property-based cases | Property-based test suite (0.04) |
| NFR-09 | Reliability | Money/Currency arithmetic and formatting results are identical regardless of `CurrentCulture`/`CurrentUICulture` | Locale-matrix unit test (0.04) |
| NFR-10 | Reliability | Every stdout log line written by a `Decisya.ServiceDefaults`-instrumented host during an active OpenTelemetry `Activity` carries a `trace_id`/`span_id` matching that `Activity` (zero uncorrelated log lines within a trace) | Correlation integration test (0.03) |
| NFR-11 | Security | Every log field marked `[Sensitive]` is masked in 100% of emitted stdout log lines (zero plaintext occurrences), failing closed when maskability can't be determined | `SensitiveDataMaskingProcessor` unit/integration test (0.03) |
| NFR-12 | Portability | 100% of exported telemetry (traces, metrics, logs) leaves each `Decisya.ServiceDefaults`-instrumented host through the OTLP exporter only; zero vendor-specific exporter or observability-SDK package references | Package/assembly-reference check (0.03), extended repo-wide by `Decisya.ArchitectureTests` (#22) |
| NFR-13 | Maintainability | ≥ 95% line coverage on `Decisya.SharedKernel`'s `TenantId`, `Result`, `Result<T>` and `DomainError` types | Microsoft.Testing.Platform code coverage (`Microsoft.Testing.Extensions.CodeCoverage`, `dotnet test --coverage`) report (0.04b) |
| NFR-14 | Functional suitability | `TenantId` round-trips (`New()`/`From`/`Parse` → `ToString` → `Parse`) losslessly, with zero mismatches, across ≥ 10,000 randomized property-based cases | Property-based test suite (0.04b) |
