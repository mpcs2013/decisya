# 0024. Wolverine tenant safety: the envelope tenant at both ends, and no envelope entity in any EF model

- Status: Accepted (Marco, 2026-11-01)
- Date: 2026-11-01
- Deciders: Marco
- Tags: security, data, module
- Refines: [ADR-0001](0001-multi-tenant-single-instance.md) (the tenant on every non-EF store), [ADR-0005](0005-modular-monolith-wolverine.md) (the envelope tenant and its `[AllowCrossTenant]` exception, the EF Core outbox), [ADR-0012](0012-cross-tenant-admin-commands-target-tenant-scope.md) (cross-tenant work for message handlers)
- Architecture note: [`docs/architecture/231-wolverine-tenant-safety.md`](../architecture/231-wolverine-tenant-safety.md)

## Context and problem statement

#231 adds the first project reference to Wolverine (`WolverineFx` 6.39.1, already pinned). Register item C-13 names three gaps: a serializer could build `default(TenantId)`, handler discovery could reach internal admin handlers, and Wolverine's outbox entities are not `ITenantScoped`.

The third gap concerns ADR-0005's "EF Core transactional outbox". Wolverine's EF Core integration (`WolverineFx.EntityFrameworkCore`, not pinned) can map its envelope tables into a `DbContext` (`MapWolverineEnvelopeStorage()`). `TenantDbContext` refuses to build a model with an entity that is not `ITenantScoped` (`TenantIsolationViolation.UnscopedEntityType`; NFR-28, `TenantModelRule`). The envelope rows of every tenant share one table. If those rows sat in a tenant model, a module context could reach them without the tenant filter.

Forces: the #22 filter and guard stay the only enforcement, with no escape hatch in `TenantDbContext` (an ADR-0012 driver). We want the smallest issue that is safe (#84). We add no package without Marco's approval. Marco answered on 2026-11-01: a tenant-less message is discarded, with a log event and a metric and no dead-letter row, and a cross-tenant message always names an explicit target tenant.

## Decision drivers

- Tenant isolation is non-negotiable, and every rule fails closed.
- NFR-28 stays exception-free: every entity in every `TenantDbContext` model is `ITenantScoped`.
- Solo developer: no second EF context per unit of work and no allow-list to maintain.

## Considered options

1. **Map the envelope entities into each module's `TenantDbContext` and allow their exact types in an ADR-0001 amendment.** Rejected. It adds the first exception to `TenantDbContext` and to NFR-28, and every tenant's envelopes become an unfiltered set that a module context can reach by key.
2. **A separate, non-tenant `WolverineEnvelopeDbContext` that maps the envelope entities.** Rejected. It needs a second context on the same connection and transaction in every unit of work. Any type that injects it can read every tenant's envelopes, so it needs its own rule. Wolverine's batched path also expects the mapping in the same context that saves the business change, so the separate context gains nothing over option 3.
3. **No envelope entity in any EF model.** Wolverine's own Postgres message store (`WolverineFx.Postgresql`) owns the envelope tables in schema `wolverine`, and the migrator creates them. When EF Core transaction integration arrives, it enlists the store's writes in the module context's own transaction without the mapping. **Chosen.**

## Decision outcome

Chosen option: **3**, because it keeps the tenant model free of exceptions and needs no allow-list, no new context and no change to `TenantDbContext`.

1. **Outbox entities.** `MapWolverineEnvelopeStorage` is never called. No Wolverine type is mapped into any EF model, so the exact allow-list is empty and stays empty. The envelope tables (Wolverine 6 names `wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes`, `wolverine_dead_letters`, plus its node tables) belong to Wolverine's message store in schema `wolverine`. The migrator creates them, and the API runs with auto-provisioning off outside local development (ADR-0005). Every tenant's rows share these tables, so module roles get INSERT on the outgoing envelope table only. Reading, updating and deleting belong to a separate dispatcher role. No module role can read another tenant's message bodies or caller ids, or delete its messages.
2. **Phasing.** #231 runs Wolverine in-process only. Durable mode is off on the composed host: `DurabilityMode.MediatorOnly`, or the equivalent setting in the pinned version. A startup test inspects the composed runtime under the Production environment, after every extension Wolverine applies from referenced assemblies, not the configuration code. It fails unless the only message store is the null store, the only transport is local, there is no listener, no durable local queue and no resource auto-setup or storage auto-build, and System.Text.Json is the only serializer. The issue that first enables durable storage (expected: the first Ledger cross-module message; backlog #243 item 231-1) must:
   - add `WolverineFx.EntityFrameworkCore` with Marco's approval;
   - verify on the pinned version that the integration enlists without the mapping and works with INSERT-only module roles, and stop and revise this ADR if it does not;
   - design retention and erasure of the caller ids and bodies stored in envelope and dead-letter rows, under its own G3;
   - deliver the #231 G1 Story 4 scenarios 5 and 6, and the "no outbox, inbox or dead-letter row" line of Story 1, against the real store.
3. **Receive side.** A middleware runs first in every handler chain and reads the envelope tenant. It accepts only the exact canonical text: `TenantId.TryParse` succeeds and the value's `ToString()` equals the input ordinally, which rules out padding and upper case. It then checks the caller-id header: 1 to 128 characters, no control characters, no leading or trailing whitespace. It sets the message scope's caller context with `RequestCaller.Set(TenantResolution.For(tenant), callerId, isPlatformAdmin: false, hasMfaLevel: false)` before any handler dependency runs. The caller id only attributes the work; the platform-admin and MFA facts never come from a message. If either check fails, it stops the chain with the reason `missing`, `malformed` or `no-caller`: the handler is not invoked, nothing is retried or dead-lettered, and one log event and one refusal metric are recorded. This applies to every handler, with or without `[AllowCrossTenant]`. There is no tenant-less handler.
4. **Send side.** One type is the only writer of the envelope tenant and caller id. On every send path (invoke, publish, send, schedule and cascaded messages) it reads the sender's ambient tenant and caller, never cached, and **overwrites** any tenant or caller value already on the envelope. It throws when the ambient resolution is `None` or `Invalid`, so a missing tenant fails at the sender, as `TenantIdJsonConverter.Write` does. No other code may reference any member of a `Wolverine` or `JasperFx*` type whose name contains `Tenant` (case-insensitive: `set_TenantId`, `ForTenant`, `InvokeForTenantAsync` and the like). The one exception is reading `TenantId` (`get_TenantId`) in the receive middleware. A cross-tenant message for an explicit target arrives with the first cross-tenant sender. Following ADR-0012 amendment 1 point 6, the sender passes a `TenantResolution` minted by an `[AllowCrossTenant]`, audited type and never a raw `TenantId`, and its G2 extends the send-side rule by one exact entry.
5. **Serializer and discovery.** System.Text.Json is the only serializer, with `RespectRequiredConstructorParameters` and `RespectNullableAnnotations` on, so a missing `TenantId` fails to deserialize. Conventional discovery is off. Handlers come only from the lists the composition root names explicitly. No `[AllowCrossTenant]` type is on any message handler list: the HTTP admin role and MFA policy do not apply to a message, so the exact allow-list stays empty until the first cross-tenant sender's G2 and G3 add one entry, together with an authorization check inside that handler.

### Consequences

- Good: NFR-28 and `TenantModelRule` keep working unchanged with no exception. A tenant model that maps a Wolverine type still fails its build.
- Good: #231 adds no package and no schema. Durable messaging arrives later with the tests that need it.
- Bad: Without the mapping, Wolverine writes envelopes with one extra command per save instead of batching them into `SaveChanges`. This is negligible at Decisya's volume.
- Bad: The decision depends on Wolverine keeping the unmapped EF path. If a later version drops it, option 2 comes back through a new ADR, never through a silent mapping.
- Bad: Until durable storage exists, the "no stored row" refusal is proven only in-process. The enabling issue must re-run the refusal matrix against the store.
- Bad: A message with no HTTP caller (a scheduled job, a startup task) is refused as `no-caller` until the issue that adds such a sender defines a service actor id under its own G3. A made-up actor is never used.
- Changes ADR-0005: its exception "a message without a tenant fails unless its handler is `[AllowCrossTenant]`" is removed. Every message needs a valid envelope tenant and caller id, and no `[AllowCrossTenant]` handler is on a message path until the first cross-tenant sender's review.
- Invariants: no `CLAUDE.md` invariant changes.
- Enforced by (#231, `docs/architecture/231-wolverine-tenant-safety.md` rules R1 to R10, with the G3 corrections G4-231-01 to G4-231-05 in `docs/security/threat-models/231-wolverine-tenant-safety.md`):
  - in `Decisya.ArchitectureTests`:
    - a rule that no assembly references `MapWolverineEnvelopeStorage`, and a `TenantModelRule` fixture that maps a Wolverine type and fails naming it;
    - the tenant-setter rule over every `Wolverine` and `JasperFx*` member whose name contains `Tenant`, with red and green fixtures;
    - a rule that no `[AllowCrossTenant]` type is on any message handler list, with an empty exact allow-list and a red fixture;
    - the key-sink rule (R10) checked per method, with a fixture type that mixes one compliant method and one hand-built key and must be red;
  - in `Decisya.Api.Tests`:
    - the refusal matrix, including `*DEFAULT*` and `no-caller`, asserting that the platform-admin and MFA facts are false;
    - a test that walks every handler chain and finds the tenant middleware first;
    - the send-stamp tests over every send path, scheduled and cascaded messages included, and a pre-set foreign tenant that arrives as the sender's tenant or throws;
    - a concurrent two-tenant test in which each message sees only its own tenant and log enrichment;
    - the serializer test and the explicit handler set;
    - the durable-mode test on the composed Production runtime (point 2).

## Amendments

<!-- Dated, attributed additions only; earlier text is never rewritten. -->

### Amendment 1 (2026-11-01, #231 G4, Marco): in line with what WolverineFx 6.39.1 allows

Marco answered "yes" on 2026-11-01: the design follows what the pinned WolverineFx 6.39.1 allows, as G4 found it (build and fast tests green; code in `src/Decisya.Api/Messaging/`, tests in `tests/Decisya.Api.Tests/Messaging/`). Points 2 and 4 read with these changes. Everything else in them stands.

1. **Send paths: invoke only while durable mode is off (changes point 4).**
   - Under `DurabilityMode.MediatorOnly`, 6.39.1 allows only `InvokeAsync`, with and without a response. `PublishAsync`, `SendAsync`, delayed and scheduled sends, and cascaded messages throw `InvalidOperationException` ("not allowed … MediatorOnly").
   - Point 4's list "invoke, publish, send, schedule and cascaded messages" therefore reads "invoke" until durable mode is on.
   - Publish, send, schedule and cascade return with the durable-messaging issue (backlog #243 item 231-1). That issue must re-establish the stamp on each of them, with a send-stamp test per path (the ambient tenant and caller arrive, a pre-set foreign tenant and caller are overwritten, a `None` or `Invalid` ambient throws), before any code uses them.
   - Until then a test asserts that each of them throws. A Wolverine version that starts allowing one fails that test; it never sends an unstamped envelope unnoticed.
   - Consequence: every in-process message is handled inside the sender's call, and a handler failure reaches the sender. There is no fire-and-forget message until 231-1.
2. **The stamp is the scope's message bus, not an envelope rule (changes point 4's "one type").**
   - The one writer is `Decisya.Api.Messaging.EnvelopeTenantStamp`: the `IMessageBus` every DI scope resolves. It wraps Wolverine's own scoped bus and is wired in `MessageCallerBindings.DecorateScopedBus`. The root provider cannot resolve `IMessageBus`.
   - Why: 6.39.1 never applies an `IEnvelopeRule` on `InvokeAsync`, the one path that works here. A G4 probe showed a foreign `DeliveryOptions.TenantId` and a forged caller-id header passing through unchanged. A rule would therefore stamp nothing.
   - On every call it reads the scope's `RequestCaller`, never cached, and overwrites the tenant and the caller-id header in the `DeliveryOptions` (creating them when the caller passed none). It throws `InvalidOperationException` for a `None` or `Invalid` ambient. It throws `NotSupportedException` for `InvokeForTenantAsync`, for setting `TenantId`, for `EndpointFor` and for `BroadcastToTopicAsync`.
   - Inside a handler, a nested invoke inherits the handled envelope's tenant, which the receive middleware (point 3) has already validated.
3. **The `IMessageContext` and `MessageContext` bypass, and the rule that closes it.**
   - Wolverine's own `IMessageBus` factory resolves `Wolverine.Runtime.MessageContext`, so `Wolverine.IMessageContext` and `MessageContext` stay resolvable from a scope and carry no stamp. `Wolverine.Runtime.MessageBus` and `Wolverine.HostBuilderExtensions.MessageBus(IHost)` also build an unstamped bus.
   - A tenant-less invoke through them is refused by the receive middleware as `missing`. A chosen tenant through them needs a Wolverine member whose name contains `Tenant`, which point 4's rule (R6) bans outside the stamp, and modules do not reference Wolverine at all (R5).
   - A new rule closes the path itself: **R14** in the architecture note. No type in any KeyScope assembly references `Wolverine.IMessageContext`, `Wolverine.Runtime.MessageContext`, `Wolverine.Runtime.MessageBus` or `Wolverine.HostBuilderExtensions.MessageBus(IHost)`, except types in the exact namespace `Decisya.Api.Messaging` of the `Decisya.Api` assembly. It has red fixtures in `Fixtures/TenancyViolations` and a green fixture in `Fixtures/TenancyCompliant`.
   - The first handler that brings pre-generated code extends R14 by one exact entry, the generated namespace `Internal.Generated.WolverineHandlers` of the `Decisya.Api` assembly, under its own G2. Never by a prefix or a wildcard.
4. **The composed-runtime test allows exactly the inert internals (changes point 2).** 6.39.1 always registers some internals that cannot be turned off. With `MediatorOnly`, the null message store and no listener, they are inert. The test on the composed Production runtime pins them, and anything beyond them still fails:
   - transports: exactly `local`, `stub` and `tcp`. Every endpoint and every active listener has the `local` or `stub` scheme, so no network endpoint or listener exists;
   - durable endpoints: at most Wolverine's two reserved local queues, `durable` and `scheduled`;
   - serializers: System.Text.Json is the default, and the registry holds exactly `application/json`, `binary/envelope` and `binary/wolverine`;
   - the rest of point 2 is unchanged: `MediatorOnly`, the null store as the main store with no ancillary store, no storage auto-build, and external listeners off.
   Adding to any of these sets is a change under G2 and G3, not a test update.
5. **Code generation: static in the Api, the runtime compiler only in the test project.**
   - 6.39.1 ships without its runtime compiler. The Api runs `TypeLoadMode.Static`, so no Roslyn runs in the Api process. The host starts because #231 adds no handler.
   - The first handler needs pre-generated handler code for the Api. Backlog #243 item "dynamic in development, static for release images" is now a prerequisite of the first handler's issue. That issue decides it with Marco and records it here.
   - Marco approved `WolverineFx.RuntimeCompilation` 6.39.1 for `tests/Decisya.Api.Tests` only, so the tests can run real handler chains (the receive middleware first in every chain). No `src` project references it. **R15** in the architecture note enforces this.
6. **Enforced by (addition).** Besides the list above: R14 and R15 in `Decisya.ArchitectureTests`. In `Decisya.Api.Tests`, the send-stamp tests cover the invoke paths and assert that publish, send, delayed and scheduled sends and a cascaded message throw under `MediatorOnly`. The durable-mode test pins the sets in item 4.

### Amendment 2 (2026-11-01, #231 G4, Marco): construction order, the nested-invoke caller gap and Wolverine's chain-less handlers

Marco approved option A on 2026-11-01. The G4-231-07 real-chain tests (`WolverineFx.RuntimeCompilation` in `tests/Decisya.Api.Tests` only) showed three WolverineFx 6.39.1 behaviours, which G3 reviewed in "G4 delta 2" of `docs/security/threat-models/231-wolverine-tenant-safety.md`. Point 3 reads with item 1, and amendment 1 item 2's nested-invoke line reads with item 2. Everything else in points 1 to 5 and in amendment 1 stands.

1. **Point 3's "before any handler dependency runs" reads as G4-231-08's control.**
   - What 6.39.1 does: the generated handler code locates `ICurrentTenant` and constructs the handler's scoped dependencies at the top of the chain, before `EnvelopeTenantMiddleware.Before` runs, on refused messages too. On a refusal the handler itself never runs.
   - Why this is a control-ordering gap and not a data exposure: `TenantDbContext` keeps the `ICurrentTenant` reference and never copies the resolution. Its query filter reads the tenant per query and its `SaveChanges`/`SaveChangesAsync` read it per save, and under `Invalid` both throw `TenantIsolationException(InvalidTenant)` before any SQL is sent. A refused message leaves the scope's `RequestCaller` at `Invalid`. Before `Set`, `UserId` throws and the platform-admin and MFA facts are false, so a value copied at construction can only lose privilege, never gain it.
   - The control: a refused message runs no handler, and no tenant-scoped read or write and no read of the caller context happens before the middleware sets the scope's `RequestCaller`. Tenant-dependent services keep the `ICurrentTenant` / `ICurrentCaller` reference and read it at use time, never in their constructor or in a DI factory. Constructing a dependency on a refused message is allowed; using it is not.
   - Proved by G4-231-08's real-chain tests (R1 as restated in the architecture note), and by **R16**: no constructor (field initializers included) and no DI `IServiceProvider` factory in a KeyScope assembly reads `ICurrentTenant.Resolution`, `ICurrentCaller.UserId` or `IsPlatformAdmin`, a `RequestCaller` getter or `IMessageBus.TenantId`. A lambda or a `Lazy` stored for use later is the approved use-time pattern and is not flagged. The exemption list is exact and empty.
   - Constructors stay free of I/O, because they run for refused messages too. The first handler's review checks this. The backlog item on the code-generation mode (a prerequisite of the first handler, amendment 1 item 5) re-runs these proofs against the pre-generated Static code, whose frame order is what ships.
2. **A nested invoke loses the caller id: refused as `no-caller` until backlog #243 item 231-5.**
   - In 6.39.1, a message invoked from inside a handler through Wolverine's injected `IMessageContext` keeps the handled envelope's tenant but not the `decisya-caller-id` header. The receive middleware refuses it as `no-caller`, and the inner handler never runs. This is fail-closed, and a test pins it. Amendment 1 item 2's "a nested invoke inherits the handled envelope's tenant" holds for the tenant; the message is still refused for its missing caller.
   - Hard prerequisite of the first handler that sends a nested message (backlog #243 item 231-5). That issue's G2 and G3 establish which `IMessageBus` a handler and its dependencies receive inside a generated chain, and carry the handled envelope's caller id onto the nested envelope through the one writer (the stamp, or an equivalent reviewed single writer). A forged caller header on a nested send stays overwritten. The pinned test is replaced by one that asserts the inner message arrives with both the outer tenant and the outer caller id. The fix is never a relaxed caller check, a made-up actor or a fallback to `system`.
3. **Wolverine's four chain-less handlers, and R17.**
   - 6.39.1 routes four message types to hand-written handlers that are not generated chains and run no middleware: `Wolverine.Envelope` (scheduled-send forwarding), `Wolverine.Runtime.Agents.IAgentCommand` (any implementing type, routed by interface), `Wolverine.Runtime.RemoteInvocation.Acknowledgement` and `Wolverine.Runtime.RemoteInvocation.FailureAcknowledgement`. Point 5's explicit handler set holds them as R4's exact list of Wolverine's own handlers, as `message type => handler type` pairs by full name, and the chain walk fails on a fifth.
   - Modules cannot reach them: they reference no Wolverine (R5). Giving a forwarded envelope another tenant needs `Envelope.TenantId`'s setter, which point 4's rule (R6) bans outside the stamp.
   - **R17** turns "no Decisya code sends these types" from an assumption into a check: no type in a KeyScope assembly implements `IAgentCommand` or creates (`newobj`) `Envelope`, `Acknowledgement` or `FailureAcknowledgement`. Exact names, an empty exemption list and one red fixture per target. The first issue that needs one of them changes R17 under its own G2 and G3.
4. **Enforced by (addition).** Besides the list above and amendment 1 item 6:
   - in `Decisya.ArchitectureTests`, written at G4: **R16** (no tenant or caller read at construction time) and **R17** (no route into Wolverine's chain-less handlers), each with red and green fixtures;
   - in `Decisya.Api.Tests`, G4-231-08's real-chain tests: on every refusal reason a dependency constructed before the check records `Invalid`, makes no use-time read, and the handler never runs; on the valid path and in the two-tenant concurrent test it reads its own message's tenant and caller at use time through the same `ICurrentTenant` instance the middleware set; a real `TenantDbContext` throws under `Invalid` and works as the tenant once that same instance is set;
   - the pinned `no-caller` refusal of a nested invoke, until 231-5 replaces it.
