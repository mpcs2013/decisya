# Signal catalogue

One row per ActivitySource span name, metric and log event. Maintained by backend-dev via the `otel-instrumentation` skill.

| Module | Signal | Type | Name | Attributes | Unit |
| --- | --- | --- | --- | --- | --- |
| Bff | span | trace | Bff.Login | decisya.tenant_id | — |
| Bff | metric | counter | decisya.bff.auth.failures | result | {failure} |
| Tenancy | span | trace | Tenancy.CreateTenant | decisya.tenant_id | — |
