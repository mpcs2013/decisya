# Requirements: tenant safety before the first Wolverine use (#231)

Register items: C-13 (Wolverine tenant and handler discovery) and C-14 (tenant-keyed Redis or object-storage key); origin #27 L-3, #32 F-3 and F-4, #22 G3. Governing ADRs: ADR-0001 (every non-EF store carries the tenant), ADR-0005 (the tenant crosses the bus on the envelope; a message without a tenant fails unless its handler is `[AllowCrossTenant]`), ADR-0012 (cross-tenant admin commands), ADR-0013 (audit).

## Who needs this, and what changes for them

- **Tenants of the platform** (personal-finance users): their ledger, budget and alert data must never be read or written under another tenant's identity through the bus, the cache or object storage. They see no screen.
- **Developers who add the first handlers** (Marco, agents): they need a bus and a key builder that make the unsafe path fail loudly, so a missing tenant is a red test and never a leak.
- **Behaviour change for existing users:** none. No project references Wolverine today, no tenant-keyed Redis or storage key exists (the ticket store keys by session), so nothing anyone relies on changes. No announcement is needed. The new rules bind every later message handler and key; the PR body states them for the Ledger work (D-012 point 2).
- **Entitlement feature key:** none. This is platform infrastructure, not a plan-gated feature. It is not offered or denied per subscription plan.
- **Out of scope:** AI features and financial amounts (no disclaimer story applies); the Wolverine transport and the outbox mechanism itself beyond the decision in Story 4.

Terms: "the envelope tenant" is the tenant identifier Wolverine carries with each message. "The key builder" is the single place that turns a `TenantId` into a Redis or object-storage key. The envelope field name and the key layout are options each application sets and are not fixed here.

---

## Story 1: a message without a tenant is refused

As a tenant of the platform, I want every message on the bus to carry a valid tenant, so that no handler ever runs a command or reaction on my data under a missing, empty or other tenant's identity.

Plan gate: none.

```gherkin
Feature: Envelope tenant is mandatory

  Scenario: A message carrying a valid tenant is handled under that tenant
    Given Wolverine is configured with System.Text.Json
    And a message is published with the envelope tenant of tenant A
    When its handler runs
    Then the ambient tenant inside the handler is tenant A
    And the ambient tenant is set before any tenant-scoped DbContext resolves

  Scenario: An envelope with no tenant is refused
    Given a message is published with no envelope tenant
    When the bus receives it
    Then the handler is not invoked
    And no DbContext is resolved
    And the message is discarded: it is not retried, not stored and not moved to a dead-letter store
    And no row for it exists in any outbox, inbox or dead-letter table
    And one structured log event records the message type, the message id, the reason and the trace id
    And that event holds no payload, no raw tenant value and no message body
    And a refusal metric on the messaging Meter is incremented once, with the reason as its only attribute (no tenant, user id or amount)

  Scenario Outline: A malformed envelope tenant is refused
    Given a message is published with the envelope tenant "<value>"
    When the bus receives it
    Then the handler is not invoked and no DbContext is resolved
    And the message is discarded and not stored
    And one structured log event records the message type, message id, reason and trace id, without the raw value or payload
    And the refusal metric is incremented once

    Examples:
      | value                                 |
      | (empty string)                        |
      | (whitespace)                          |
      | 00000000-0000-0000-0000-000000000000  |
      | not-a-guid                            |
      | (a valid GUID with trailing padding)  |

  Scenario: An [AllowCrossTenant] handler always takes an explicit target tenant
    Given a handler type carries [AllowCrossTenant] with a justification
    And a message for it carries an explicit target tenant
    When the bus receives the message
    Then the handler runs with the target tenant as its scope
    And an audit record is appended in the same transaction (ADR-0013)

  Scenario: An [AllowCrossTenant] handler gets no tenant-less message
    Given a handler type carries [AllowCrossTenant]
    When a message for it arrives with no envelope tenant and no explicit target tenant
    Then the handler is not invoked
    And the message is discarded and logged as in the first scenario

  Scenario: No platform handler runs without a tenant
    Given any handler type, with or without [AllowCrossTenant]
    When a message for it arrives without a tenant
    Then the handler is not invoked
```

## Story 2: a serializer can never build a default tenant

As a tenant of the platform, I want message deserialization to fail on an absent or invalid tenant, so that a serializer never quietly produces an uninitialised `TenantId` that a handler then treats as a value.

Plan gate: none.

```gherkin
Feature: Serializer allow-list

  Scenario: Wolverine uses System.Text.Json with the TenantId converter
    Given the Wolverine host is built
    When its configured serializers are listed
    Then System.Text.Json is the default serializer
    And the TenantId JSON converter is the one in effect for TenantId

  Scenario: A payload whose tenant property is missing is refused
    Given a message type with a TenantId property
    And a payload with that property absent
    When the payload is deserialized by the bus
    Then deserialization fails
    And no message object holding default(TenantId) reaches a handler

  Scenario: A payload whose tenant property is the empty GUID is refused
    Given a payload whose TenantId property is the all-zero GUID
    When the payload is deserialized by the bus
    Then deserialization fails

  Scenario: A non-System.Text.Json serializer is not registered
    Given the composed Wolverine host
    When a startup check lists serializers and message-type converters
    Then no other JSON, binary or reflection-based serializer is registered
    And adding one makes an architecture or startup test fail
```

## Story 3: handler discovery cannot reach admin or other modules' handlers

As a tenant of the platform, I want only explicitly registered handlers to receive messages, so that an internal admin handler or another module's handler is never reachable by a message I could cause.

Plan gate: none.

```gherkin
Feature: Handler discovery is explicit

  Scenario: Discovery is limited to the named assemblies
    Given the Wolverine host is configured
    When the discovered handlers are listed
    Then every handler belongs to an assembly the composition root names explicitly
    And no handler is found by scanning all loaded assemblies

  Scenario: Cross-tenant admin handlers are not message-reachable by default
    Given a handler type carries [AllowCrossTenant]
    When the discovered handlers are listed
    Then it is listed only if its module registers it explicitly
    And an unregistered [AllowCrossTenant] handler receives no message

  Scenario: A module cannot register another module's handler
    Given module A's registration code
    When it names a handler type from module B's implementation assembly
    Then the architecture test fails (ADR-0005)
```

## Story 4: the outbox does not weaken the tenant filter

As a tenant of the platform, I want the Wolverine outbox storage to sit outside the tenant-scoped model by a recorded and tested decision, so that the outbox entities (which are not `ITenantScoped`) can never be reached through a tenant context or return another tenant's message.

Plan gate: none.

```gherkin
Feature: Outbox entities and the tenant filter

  Scenario: The decision is recorded
    Given the architect's G2 design for issue 231
    When the repository documents are read
    Then the choice between a separate context and an exact-type allow-list is recorded in an ADR or an amendment to ADR-0001
    And it names the exact outbox entity types and the context that owns them

  Scenario: The architecture rule still holds for tenant-scoped models
    Given the rule that every entity in a TenantDbContext-derived model implements ITenantScoped (NFR-28)
    When the architecture tests run
    Then the rule passes with the outbox entities present in the solution
    And no outbox entity type appears in any module's TenantDbContext model

  Scenario: The rule fails if an outbox entity is added to a tenant model
    Given a deliberate fixture that maps a Wolverine outbox entity into a TenantDbContext model
    When the architecture tests run
    Then the tenant-model rule fails and names the entity type

  Scenario: An exception to the rule is exact, never a pattern
    Given the decision allows an allow-list
    When an entity type outside the listed exact types is mapped without ITenantScoped
    Then the architecture tests fail
    And a namespace, wildcard or base-type match in the allow-list is rejected by a test

  Scenario: A message stored in the outbox keeps its tenant
    Given tenant A handles a command that publishes a message in the same transaction
    When the outbox row is read back by the dispatcher
    Then the envelope tenant is tenant A
    And a dispatcher run for tenant B's messages never delivers tenant A's message under tenant B

  Scenario: Module roles stay least-privilege
    Given the migrator has created the outbox storage
    When the API role attempts CREATE, ALTER or DROP in it
    Then the statement fails
    And auto-provisioning is off outside local development (ADR-0005)
```

## Story 5: every tenant-keyed key goes through one builder

As a tenant of the platform, I want every Redis and object-storage key that holds my tenant built by one builder from a validated `TenantId`, so that no key is ever built from raw claim text, an empty tenant or a path another tenant can reach.

Plan gate: none.

```gherkin
Feature: One tenant key builder

  Scenario: The builder produces a key from TenantId.Value only
    Given an initialised TenantId for tenant A
    When the builder creates a Redis key and an object-storage key for it
    Then each key starts with the canonical lowercase hyphenated text of tenant A's id
    And two different tenants never produce the same key for the same resource name

  Scenario: The builder refuses a default tenant
    Given default(TenantId)
    When the builder is asked for any key
    Then it throws or returns a failed Result
    And no key string is produced

  Scenario Outline: The builder refuses a resource segment that could escape the tenant prefix
    Given an initialised TenantId for tenant A
    When the builder is given the resource segment "<segment>"
    Then no key is produced

    Examples:
      | segment                      |
      | ../tenant-b/file             |
      | (segment containing a slash) |
      | (segment containing a colon) |
      | (empty string)               |
      | (control character)          |
      | (segment longer than the limit the builder documents) |

  Scenario: A key cannot be built from raw claim text
    Given the builder's public surface
    When its parameters are listed
    Then no overload accepts string, Guid or object in place of TenantId

  Scenario: Keys are built nowhere else (negative architecture test)
    Given production code in every project
    When the architecture tests scan for string concatenation, interpolation or formatting that builds a Redis key or an object-storage key containing a tenant
    Then the only match is inside the builder
    And a deliberate fixture that builds a tenant key by hand makes the test fail

  Scenario: The first storage client cannot be added without the builder
    Given the architecture rule is in force and no storage client exists yet
    When a project adds an object-storage client that takes a plain string for a tenant-keyed object
    Then the architecture test fails
    # The wrapper scenario (the wrapper's key parameter is the builder's key type) is a recorded criterion for the storage issue.

  Scenario: The session ticket store is not flagged
    Given the Redis session ticket store keys by session and holds no tenant
    When the architecture tests run
    Then the key-builder rule does not flag it

  Scenario: Two tenants cannot read each other's keys (two-tenant isolation)
    Given tenant A stored a value under a builder key
    And tenant B stored a different value for the same resource name
    When each tenant reads that resource
    Then each reads only its own value
    And a read for tenant B never returns tenant A's value
    And a delete for tenant A leaves tenant B's value intact
```

The session ticket store keys by session, not by tenant, and stays outside the builder (Marco, question 3).

## Story 6: the rules are visible to developers and to the register

As a developer adding the first Wolverine handler, I want the tenant-safety rules enforced by tests and recorded where I read them, so that I learn of a mistake from a red test and know which register items this issue closes.

Plan gate: none.

```gherkin
Feature: Register and developer visibility

  Scenario: C-13 and C-14 are updated
    Given the issue is merged
    When the register (docs/security/threat-models/system-baseline.md) and docs/security/asvs-l2.md are read
    Then C-13 lists its three parts (serializer, discovery, outbox entities) each as closed with the test that proves it, or as open with a named holder
    And C-14 states that the builder exists and the architecture rule enforces it

  Scenario: A new message handler without a tenant test is caught
    Given a new message type is added without a two-tenant isolation test
    When the isolation-test checks run
    Then the missing coverage is reported
```

---

## Non-functional requirement (added to `docs/requirements/nfr.md`)

| Id | Category | Target | Verified by |
| --- | --- | --- | --- |
| NFR-61 | Security | 100% of messages arriving without a valid envelope tenant (absent, empty, all-zero, malformed) or without a valid caller id are refused with 0 handler invocations and 0 tenant-scoped reads or writes and 0 caller-context reads before the middleware sets `RequestCaller` (a handler dependency may be constructed but is never used; ADR-0024 amendment 2), with no exception for `[AllowCrossTenant]` handlers (ADR-0024 point 5); 0 Redis or object-storage keys containing a tenant are built outside the single builder | Wolverine test (ADR-0005) over the refusal matrix; `Decisya.ArchitectureTests` key-builder rule, proven red on a deliberate fixture violation (#231) |

I add this row to `docs/requirements/nfr.md` with this document.

## Open questions for Marco

## Answered questions (Marco, 2026-11-01)

1. **Refused messages: discard.** A message without a valid tenant is discarded and never stored. One structured log event records message type, message id, reason and trace id (no payload), and a metric counts it. No dead-letter row exists for it. Applied in Story 1.
2. **System messages: explicit target tenant.** An `[AllowCrossTenant]` handler always takes an explicit target tenant, as ADR-0012 does. No tenant-less platform handler exists. Applied in Story 1.
3. **Ticket store: stays outside the builder.** The Redis session ticket store keeps keying by session; the architecture rule must not flag it. Applied in Story 5.
4. **Object-storage keys: yes.** Story 5 covers the builder and its architecture rule now, written so the first storage client cannot be added without the builder. The storage-wrapper scenario (the wrapper's key parameter is the builder's key type) is a recorded criterion for the storage issue.
5. **Segment maximum length: G2.** The architect proposes it, with the S3 and Redis limits in view.
6. **Which project references Wolverine first: G2.** The stories do not depend on it.

No question is left open for G1. Items 5 and 6 are design decisions handed to the architect by the owner.

<!-- gate: G1 | verdict: PASS | issue: #231 -->

## Traceability

Gate G5, test-engineer, 2026-11-01. Lanes: Unit and Architecture run in the fast suite; Integration needs Docker. Test names are `Class.Method`; a `*` stands for a theory or a family named in the note. The G4 tests are in `tests/Decisya.Api.Tests/Messaging` (`Api.Tests`), `tests/Decisya.SharedKernel.Tests/Tenancy` (`SharedKernel.Tests`) and `tests/Decisya.ArchitectureTests` (`Arch`). The G5 additions are marked **(G5)**.

Written without running anything (G5 has no test command): the verdict holds only if the orchestrator's full run is green. A red new test makes this BLOCK.

### G1 acceptance criteria

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A message carrying a valid tenant is handled under that tenant | `EnvelopeTenantMiddlewareTests.A_valid_tenant_and_caller_populate_the_scope_with_the_admin_and_mfa_facts_false`; `EnvelopeChainTests.On_the_valid_path_the_early_built_dependency_sees_Invalid_at_construction_and_tenant_A_and_caller_A_at_use_time_through_the_same_instance`; `EnvelopeStampTests.InvokeAsync_arrives_as_the_senders_tenant_with_the_senders_caller_id`; `MessagingRuntimeTests.System_Text_Json_is_the_default_serializer_and_the_only_one_beyond_Wolverines_intrinsic_two`. "Set before any tenant-scoped DbContext resolves" is restated by ADR-0024 amendment 2 (G4-231-08): built early, read at use time. Proved by `TenantDbContextUseTimeTests.*` (4), `Arch ConstructionTimeAndChainlessRuleTests.R16_*` | Unit, Architecture |
| 1 | An envelope with no tenant is refused | `EnvelopeChainTests.A_refused_message_runs_no_handler_and_its_early_built_dependency_is_never_used` (rows `missing`, `empty`); `EnvelopeTenantMiddlewareTests.An_envelope_without_a_valid_canonical_tenant_is_refused_before_anything_resolves`; `EnvelopeChainRefusalTests.Every_generated_chain_refuses_a_message_with_no_tenant_before_its_handler_runs`. Log event without payload, raw value or body, and the metric with `reason` as its only tag: the same tests plus **(G5)** `EnvelopeRefusalLogScanTests.No_log_record_across_refused_and_valid_messages_carries_the_payload_a_raw_tenant_value_or_the_caller_id`, **(G5)** `EnvelopeRefusalLogScanTests.The_refusal_event_names_the_message_CLR_type_capped_at_256_characters`. Discarded, not retried, not stored: no store, no transport, no listener exist (`MessagingRuntimeTests.Durable_mode_is_off_on_the_composed_Production_runtime`, `No_network_listener_no_network_endpoint_and_no_durable_queue_exists_on_the_composed_runtime`). "No row in any outbox, inbox or dead-letter table": **deferred to #243 item 231-1** (no table exists in #231; Marco, option A, 2026-11-01) | Unit |
| 1 | A malformed envelope tenant is refused (outline, 5 examples) | `EnvelopeTenantMiddlewareTests.An_envelope_without_a_valid_canonical_tenant_is_refused_before_anything_resolves` (14 rows, empty, whitespace, all-zero, `not-a-guid`, padded, upper case, `*DEFAULT*`); `EnvelopeChainTests.A_refused_message_runs_no_handler_and_its_early_built_dependency_is_never_used` (10 rows, through real chains) | Unit |
| 1 | An `[AllowCrossTenant]` handler always takes an explicit target tenant | **Deferred** to the first cross-tenant sender (G2 decision 4; ADR-0024 point 4): no cross-tenant sender and no `[AllowCrossTenant]` handler on a message path exist in #231. The guard that keeps it so: `HandlerDiscoveryTests.A_type_that_carries_AllowCrossTenant_is_refused_as_a_message_handler`; **(G5)** `Arch MessagingBoundaryTests.G4_231_03_fails_on_a_module_that_lists_an_AllowCrossTenant_handler_and_the_allow_list_is_empty`. The send side cannot name a target: `EnvelopeStampTests.InvokeForTenantAsync_and_a_set_TenantId_are_not_supported`, **(G5)** `Arch WolverineRuleTests.R6_*` | Unit, Architecture |
| 1 | An `[AllowCrossTenant]` handler gets no tenant-less message | No such handler can be listed (the two tests above); a tenant-less message is refused for every handler: `EnvelopeChainRefusalTests.Every_generated_chain_refuses_a_message_with_no_tenant_before_its_handler_runs` | Unit, Architecture |
| 1 | No platform handler runs without a tenant | `EnvelopeChainRefusalTests.Every_generated_chain_refuses_a_message_with_no_tenant_before_its_handler_runs`; `EnvelopeChainTests.Every_routable_handler_is_a_generated_chain_with_the_envelope_tenant_middleware_first` (Wolverine's four chain-less handlers pinned by exact name; routes into them banned by R17) | Unit, Architecture |
| 2 | Wolverine uses System.Text.Json with the TenantId converter | `MessagingRuntimeTests.System_Text_Json_is_the_default_serializer_and_the_only_one_beyond_Wolverines_intrinsic_two`; `MessagingRuntimeTests.A_message_with_a_tenant_round_trips_through_the_configured_serializer` | Unit |
| 2 | A payload whose tenant property is missing is refused | `MessagingRuntimeTests.A_payload_whose_tenant_is_missing_fails_to_deserialize_for_a_constructor_parameter_and_for_a_required_member`; `MessagingRuntimeTests.A_payload_whose_tenant_is_json_null_fails_to_deserialize`; `MessagingRuntimeTests.A_null_for_a_non_nullable_member_fails_to_deserialize_because_nullable_annotations_are_respected`; `MessagingRuntimeTests.A_message_with_no_tenant_fails_at_the_sender_not_at_the_receiver` | Unit |
| 2 | A payload whose tenant property is the empty GUID is refused | `MessagingRuntimeTests.A_payload_whose_tenant_is_the_empty_guid_or_malformed_fails_to_deserialize` | Unit |
| 2 | A non-System.Text.Json serializer is not registered | `MessagingRuntimeTests.System_Text_Json_is_the_default_serializer_and_the_only_one_beyond_Wolverines_intrinsic_two` (the registry is exactly the three entries; an added one fails); `WolverineSurfaceTests.*` (3) | Unit |
| 3 | Discovery is limited to the named assemblies | `HandlerDiscoveryTests.A_conventional_handler_in_the_application_assembly_is_not_discovered_when_no_module_lists_it`; `HandlerDiscoveryTests.A_handler_a_module_lists_is_discovered_and_only_that_one`; `EnvelopeChainTests.Every_routable_handler_is_a_generated_chain_with_the_envelope_tenant_middleware_first` | Unit |
| 3 | Cross-tenant admin handlers are not message-reachable by default | `HandlerDiscoveryTests.A_type_that_carries_AllowCrossTenant_is_refused_as_a_message_handler`; **(G5)** `Arch MessagingBoundaryTests.G4_231_03_fails_on_a_module_that_lists_an_AllowCrossTenant_handler_and_the_allow_list_is_empty` (red on `CrossTenantHandlerModule`) | Unit, Architecture |
| 3 | A module cannot register another module's handler | **(G5)** `Arch MessagingBoundaryTests.R5_fails_on_a_module_that_lists_another_assemblys_handler_and_names_the_module` (red on `ForeignHandlerModule`), `R5_passes_a_module_that_lists_its_own_handler_with_an_isolation_test`, `R5_no_module_and_no_Contracts_assembly_references_the_Wolverine_assembly`, `R5_is_green_on_today_src_where_no_module_lists_a_handler_yet`; `Arch ModuleBoundaryTests` (ADR-0005) | Architecture |
| 4 | The decision is recorded | **manual**: documentation. ADR-0024 (Accepted 2026-11-01, option 3, amendments 1 and 2) names the empty exact-type list and that no EF context maps an envelope entity; read at G5, re-read at G6 | manual |
| 4 | The architecture rule still holds for tenant-scoped models | `Arch ModuleBoundaryTests.TenantModelRule_passes_over_the_current_architecture_scope`; **(G5)** `Arch WolverineRuleTests.R7_the_tenant_model_rule_passes_over_the_scope_with_the_Wolverine_hosts_included`, `R7_is_green_on_today_src` | Architecture |
| 4 | The rule fails if an outbox entity is added to a tenant model | **(G5)** `Arch WolverineRuleTests.R7_TenantModelRule_fails_on_a_context_that_maps_Wolverine_Envelope_and_names_the_context`, `R7_building_the_model_of_that_context_throws_a_TenantIsolationException_that_names_the_entity_type`, `R7_fails_on_a_call_to_MapWolverineEnvelopeStorage_and_names_the_type` (fixtures `EnvelopeMappingDbContext`, `MapsEnvelopeStorage`; the real envelope entity types live in `WolverineFx.EntityFrameworkCore`, which is not a dependency, so the nearest real Wolverine type, `Envelope`, stands in) | Architecture |
| 4 | An exception to the rule is exact, never a pattern | **(G5)** `Arch WolverineRuleTests.R7_the_tenant_model_rule_stays_exception_free_it_has_no_allow_list_parameter`: the decision has no allow-list (ADR-0024 option 3), `TenantModelRule` takes assemblies only. The exact-name discipline of every exemption list this issue adds is tested instead: `R6_exemption_is_an_exact_type_in_the_exact_assembly_never_a_namespace_or_a_same_named_type_elsewhere`, `R14_exemption_is_an_exact_type_in_the_exact_assembly_never_the_namespace`, `TenantKeyRuleTests.R10_an_exemption_that_is_a_namespace_a_wildcard_or_an_unknown_name_fails`, `ConstructionTimeAndChainlessRuleTests.R16_exemptions_are_an_exact_list_that_is_empty_in_231_and_every_entry_exists` | Architecture |
| 4 | A message stored in the outbox keeps its tenant | **Deferred** to #243 item 231-1 (durable messaging; Marco option A, 2026-11-01): no outbox exists in #231. In-process stand-in: `EnvelopeStampTests.InvokeAsync_arrives_as_the_senders_tenant_with_the_senders_caller_id`, `EnvelopeChainTests.Concurrent_messages_of_two_tenants_through_real_chains_each_see_only_their_own_tenant_caller_and_enrichment` | manual (deferred) |
| 4 | Module roles stay least-privilege | **Deferred** to #243 item 231-1 (no `wolverine` schema and no role grant in #231; T-20) | manual (deferred) |
| 5 | The builder produces a key from TenantId.Value only | `SharedKernel.Tests TenantKeyTests.A_key_starts_with_the_canonical_lowercase_tenant_text_and_joins_segments`, `Two_tenants_never_produce_the_same_key_for_the_same_resource_name` | Unit |
| 5 | The builder refuses a default tenant | `SharedKernel.Tests TenantKeyTests.default_TenantId_is_refused_and_no_key_is_produced` | Unit |
| 5 | The builder refuses a resource segment that could escape the tenant prefix (outline) | `TenantKeyTests.A_segment_that_could_escape_the_tenant_prefix_is_refused`, `No_segment_is_refused`, `A_non_ASCII_letter_or_digit_is_refused`, `A_segment_is_at_most_128_characters`, `The_whole_key_is_at_most_512_characters`, `A_refusal_message_never_carries_the_segment_value` | Unit |
| 5 | A key cannot be built from raw claim text | `TenantKeyTests.The_public_surface_takes_a_TenantId_and_never_a_string_Guid_or_object_for_the_tenant` | Unit |
| 5 | Keys are built nowhere else (negative architecture test) | **(G5)** `Arch TenantKeyRuleTests.R10_fails_on_a_key_built_by_hand_and_names_the_type_and_the_clause` (hand-built, interpolated, resolution text, claim-fed, tenant text beside a key), `R10_checks_each_method_so_a_type_with_one_compliant_method_and_one_hand_built_key_is_red_and_names_the_second_method`, `R10_passes_a_type_whose_every_sink_method_builds_its_key_with_TenantKey`, `R10_is_green_on_today_src_and_the_exemptions_are_exactly_the_three_session_types_and_exist`, `R11_every_src_project_is_in_KeyScope_except_exactly_the_AppHost_and_the_Migrator` | Architecture |
| 5 | The first storage client cannot be added without the builder | **(G5)** `Arch TenantKeyRuleTests.R10_fails_on_a_key_built_by_hand_and_names_the_type_and_the_clause` (row `PlainStringKeyCache`: a sink with a plain string key is red). The wrapper criterion (key parameter is `TenantKey`) is recorded for the storage issue (G1 note under the scenario) | Architecture |
| 5 | The session ticket store is not flagged | **(G5)** `Arch TenantKeyRuleTests.R10_is_green_on_today_src_and_the_exemptions_are_exactly_the_three_session_types_and_exist`, `R10_without_its_exemptions_the_rule_does_flag_the_Redis_session_types_so_the_green_run_is_not_vacuous`, `R10_a_session_keyed_store_is_red_unless_it_is_on_the_exemption_list_by_exact_name`, `R10_an_exempt_type_still_may_not_read_the_tenant_id_claim_or_reference_tenant_text` | Architecture |
| 5 | Two tenants cannot read each other's keys (two-tenant isolation) | **(G5)** `Bff.Tests TenantKeyRedisIsolationTests.Two_tenants_storing_the_same_resource_name_each_read_only_their_own_value_and_a_delete_for_one_leaves_the_other` (real Redis container) | Integration |
| 6 | C-13 and C-14 are updated | **manual**: the register (`docs/security/threat-models/system-baseline.md`) and `docs/security/asvs-l2.md` are edited by security-reviewer at G6 (G2 note). Open at G5 | manual (G6) |
| 6 | A new message handler without a tenant test is caught | **(G5)** `Arch MessagingBoundaryTests.Story6_a_new_message_handler_without_an_isolation_test_is_reported` (red on `UntestedHandlerModule`), `Story6_a_file_that_is_not_an_isolation_test_for_that_handler_does_not_count` (3 rows), `Story6_a_matching_isolation_test_clears_the_report_for_that_handler`, `R5_is_green_on_today_src_where_no_module_lists_a_handler_yet` | Architecture |
| NFR-61 | 100% of invalid envelope tenants refused with 0 handler runs; 0 tenant keys built outside the builder | The Story 1 refusal rows above (Wolverine test over the matrix); the Story 5 `R10` rows (rule proven red on a deliberate violation) | Unit, Architecture |

### Architecture rules (G2 R1 to R17)

| Rule | Test(s) | Red fixtures | Green fixtures |
| --- | --- | --- | --- |
| R1 refusal matrix, construction order | `Api.Tests EnvelopeTenantMiddlewareTests.*`, `EnvelopeChainTests.*`, `EnvelopeChainRefusalTests.*`, `TenantDbContextUseTimeTests.*` (G4, mapped not duplicated) | n/a (behaviour) | n/a |
| R2 send stamp | `Api.Tests EnvelopeStampTests.*` (15), `EnvelopeStampInnerBusTests.*` (5) (G4) | n/a | n/a |
| R3 serializer, durable mode off | `Api.Tests MessagingRuntimeTests.*` (13), `WolverineSurfaceTests.*` (G4) | n/a | n/a |
| R4 explicit handler set | `Api.Tests HandlerDiscoveryTests.*`, `EnvelopeChainTests.Every_routable_handler_...` (G4) | n/a | n/a |
| R5 module lists, no Wolverine in modules | **(G5)** `Arch MessagingBoundaryTests.R5_*`, `G4_231_03_*`, `Story6_*` | `ForeignHandlerModule`, `CrossTenantHandlerModule`, `UntestedHandlerModule`; `SetsEnvelopeTenant`'s assembly for the reference check | `CompliantHandlerModule` |
| R6 tenant members only in the stamp | **(G5)** `Arch WolverineRuleTests.R6_*` (5 red rows, green, exact exemption, getter-only exemption) | `SetsEnvelopeTenant`, `SetsDeliveryOptionsTenant`, `InvokesForAnotherTenant`, `SetsBusTenant`, `ReadsEnvelopeTenantOutsideTheReceiveMiddleware` | `BusOnlyUser` (reused) |
| R7 no envelope entity in a model | **(G5)** `Arch WolverineRuleTests.R7_*` | `EnvelopeMappingDbContext` (maps `Wolverine.Envelope`, fails `TenantModelRule`), `MapsEnvelopeStorage` (stand-in named `MapWolverineEnvelopeStorage`) | the existing `CompliantDbContext` |
| R8 Contracts do not depend on Wolverine | **(G5)** `Arch ContractsBoundaryTests.No_Contracts_assembly_depends_on_a_persistence_or_web_framework` (now bans the assembly prefix `Wolverine`, and `JasperFx`; it banned the package id `WolverineFx` before, which matched nothing), `The_Wolverine_ban_matches_the_real_assembly_name_and_a_reference_to_it_would_be_caught` | n/a (the prefix is proved to match the real assembly name) | n/a |
| R9 minter list | `Api.Tests ApiBoundaryTests.Only_CallerContextMiddleware_and_EnvelopeTenantMiddleware_mint_a_tenant_resolution_in_Decisya_Api` (G4) | n/a | n/a |
| R10 key sinks. Option A (Marco, 2026-11-01): a nested type counts as its outermost declaring type for the exemption (compiler-generated types already did), so `RedisRefreshLock+LockHandle` is covered by the exact name `RedisRefreshLock`; the list stays the three names | **(G5)** `Arch TenantKeyRuleTests.R10_*`, including `R10_a_nested_type_counts_as_its_outermost_type_for_the_exemption_and_a_nested_type_of_a_non_exempt_type_is_still_checked` | `HandBuiltKeyCache`, `InterpolatedKeyCache`, `ResolutionTextKeyCache`, `PlainStringKeyCache`, `MixedKeyCache` (per method), `ClaimFedKeyCache`, `TenantTextBesideKeyCache`, `ExemptReadsTenantClaim`, `ExemptReadsTenantText`, `NestedExemptOuter`+`Inner` (exempt via outer), `NestedPlainOuter`+`Inner` (red) | `TenantKeyedCache`, `SessionKeyedCache` (exempt by exact name) |
| R11 KeyScope | **(G5)** `Arch TenantKeyRuleTests.R11_*`; `ConstructionTimeAndChainlessRuleTests.KeyScope_is_every_src_project_but_the_AppHost_and_the_Migrator` (G4) | n/a | n/a |
| R12 `TenantKey` | `SharedKernel.Tests TenantKeyTests.*` (G4, 38 cases) | n/a | n/a |
| R13 Redis round trip | **(G5)** `Bff.Tests TenantKeyRedisIsolationTests.*` | n/a | n/a |
| R14 unstamped bus, S-09 | **(G5)** `Arch WolverineRuleTests.R14_*` (4 red rows, namespace case, green, exact exemption) | `TakesMessageContext`, `HoldsMessageContextField`, `TakesMessageBus`, `CallsHostMessageBus`, `MisnamedBusUser` (in `Decisya.Api.Messaging`) | `BusOnlyUser` |
| R15 runtime compiler out of `src`, S-11 | **(G5)** `Arch MessagingBoundaryTests.R15_*` | synthetic project and lock texts (the real package is a test-project-only dependency) | the real `src` tree |
| R16 construction-time reads | `Arch ConstructionTimeAndChainlessRuleTests.R16_*` (G4) | 4 fixtures | 2 fixtures |
| R17 chain-less routes, S-12 | `Arch ConstructionTimeAndChainlessRuleTests.R17_*` (G4) | 4 fixtures | `BusOnlyUser` |

### G3 requirements

| Item | Test(s) |
| --- | --- |
| G4-231-01 stamp, one writer (T-03, T-04, T-05) | `EnvelopeStampTests.*`, `EnvelopeStampInnerBusTests.*`; **(G5)** `WolverineRuleTests.R6_*` |
| G4-231-02 receive middleware (T-01, T-02, T-06 to T-08) | `EnvelopeTenantMiddlewareTests.*` (30), `EnvelopeChainTests.Every_routable_handler_is_a_generated_chain_with_the_envelope_tenant_middleware_first`, `EnvelopeChainTests.Concurrent_messages_of_two_tenants_through_real_chains_each_see_only_their_own_tenant_caller_and_enrichment` |
| G4-231-03 no `[AllowCrossTenant]` handler on a message path (T-09) | `HandlerDiscoveryTests.A_type_that_carries_AllowCrossTenant_is_refused_as_a_message_handler`; **(G5)** `MessagingBoundaryTests.G4_231_03_fails_on_a_module_that_lists_an_AllowCrossTenant_handler_and_the_allow_list_is_empty` |
| G4-231-04 durable mode off on the built runtime (T-11, T-12) | `MessagingRuntimeTests.Durable_mode_is_off_on_the_composed_Production_runtime`, `No_network_listener_no_network_endpoint_and_no_durable_queue_exists_on_the_composed_runtime`, `System_Text_Json_is_the_default_serializer_and_the_only_one_beyond_Wolverines_intrinsic_two` |
| G4-231-05 R10 corrections (T-15, T-16) | **(G5)** `TenantKeyRuleTests`: item 1 `R10_an_exempt_type_still_may_not_read_the_tenant_id_claim_or_reference_tenant_text` and `R10_is_green_on_today_src_...`; item 2 the `ResolutionTextKeyCache` row of `R10_fails_on_a_key_built_by_hand_...`; item 3 `R10_checks_each_method_...` |
| G4-231-06 `MediatorOnly` tripwire, all overloads | `EnvelopeStampTests.A_cascaded_message_is_refused_under_MediatorOnly_and_its_handler_never_runs`, `StreamAsync_arrives_as_the_senders_tenant`, `PreviewSubscriptions_either_carries_the_senders_tenant_or_throws`, `A_send_throws_the_stamps_exception_on_every_overload_when_the_ambient_caller_was_never_set`, `A_send_throws_the_stamps_exception_on_every_overload_when_the_ambient_has_no_tenant`, `Publish_send_and_scheduling_are_refused_by_the_in_process_only_mode` |
| G4-231-07 chain walk through real chains | `EnvelopeChainTests.The_test_hosts_only_Wolverine_override_is_TypeLoadMode`, `Every_routable_handler_is_a_generated_chain_with_the_envelope_tenant_middleware_first`, `A_refused_message_runs_no_handler_and_its_early_built_dependency_is_never_used`, `Concurrent_messages_of_two_tenants_...`; `EnvelopeStampTests.A_message_invoked_from_a_real_handler_is_refused_as_no_caller_because_the_caller_header_is_not_carried` (nested invoke pinned) |
| G4-231-08 construction proofs (a) to (e) | (a), (c), (e): `EnvelopeChainTests.A_refused_message_runs_no_handler_and_its_early_built_dependency_is_never_used`, `On_the_valid_path_the_early_built_dependency_sees_Invalid_at_construction_and_tenant_A_and_caller_A_at_use_time_through_the_same_instance`, `Concurrent_messages_...`; (b): `TenantDbContextUseTimeTests.*` (4) |
| G4-231-09 no construction-time read (proof d) | `ConstructionTimeAndChainlessRuleTests.R16_*` |
| S-01 segment matrix, null segment, `ToString` | `TenantKeyTests.A_non_ASCII_letter_or_digit_is_refused`, `A_null_segment_is_refused_with_ArgumentNullException`, `ToString_is_the_fixed_type_name_and_never_the_key` |
| S-02 `TenantId` message members are `required` or constructor parameters | `MessagingRuntimeTests.A_payload_whose_tenant_is_missing_fails_to_deserialize_for_a_constructor_parameter_and_for_a_required_member` shows the behaviour. The statement that handlers never scope by a message member is **manual**: XML doc in `DecisyaMessagingExtensions` and `EnvelopeFacts`; G3 says no analyzer is needed in #231; checked again at G6 |
| S-03 exact list of Wolverine's own handlers | `EnvelopeChainTests.Every_routable_handler_is_a_generated_chain_with_the_envelope_tenant_middleware_first` (four `message type => handler type` pairs by full name) |
| S-04 refusal event logs the CLR message type, capped | `EnvelopeTenantMiddlewareTests` (the `MessageType=` assertion); **(G5)** `EnvelopeRefusalLogScanTests.The_refusal_event_names_the_message_CLR_type_capped_at_256_characters` |
| S-05 Warning floor, Wolverine telemetry unsubscribed | `MessagingRuntimeTests.The_Warning_floor_holds_for_the_Wolverine_and_JasperFx_categories_whatever_the_configuration_says`, `Wolverines_own_meter_and_activity_source_are_not_subscribed_but_Decisya_ones_are`; **(G5)** the floor assertion in `EnvelopeRefusalLogScanTests.No_log_record_across_...` (every other category at Debug) |
| S-06 `TenantKey` doc: identifiers, never personal data | **manual**: the `TenantKey` XML remarks (checked at G5, again at G6) |
| S-07 Api-side Redis ACL user and key namespace | **Deferred** to backlog #243 (nothing in #231 connects the Api to Redis) |
| S-08 lock diff, vulnerability audit, no `RunJasperFxCommands` | **G6 check**; **(G5)** `MessagingBoundaryTests.R15_is_green_on_the_real_src_tree_and_on_every_scope_assembly` keeps the runtime compiler out of the `src` graph |
| S-09 R14 exemption: exact type names, empty | **(G5)** `WolverineRuleTests.R14_exemption_is_an_exact_type_in_the_exact_assembly_never_the_namespace`, `R14_fails_on_a_type_in_the_namespace_Decisya_Api_Messaging_declared_in_another_assembly`, `R14_is_green_on_today_src_and_its_exemption_list_is_empty` |
| S-10 stamp over a recording inner bus | `EnvelopeStampInnerBusTests.*` (5) |
| S-11 R15 also scans `src/**/packages.lock.json` | **(G5)** `MessagingBoundaryTests.R15_fails_on_a_lock_file_that_lists_the_runtime_compiler_even_transitively`, `R15_passes_a_lock_file_that_lists_WolverineFx_but_not_the_runtime_compiler`, `R15_is_green_on_the_real_src_tree_and_on_every_scope_assembly` |
| S-12 no route into the four chain-less handlers (R17) | `ConstructionTimeAndChainlessRuleTests.R17_*` |

### Notes

- **Deferred by Marco's decision (option A, 2026-11-01), owner #243 item 231-1:** Story 1 "no row in any outbox, inbox or dead-letter table", Story 4 scenarios "A message stored in the outbox keeps its tenant" and "Module roles stay least-privilege". The refusal is proven in-process only (ADR-0024 consequences).
- **Deferred to the first cross-tenant sender:** Story 1 "An `[AllowCrossTenant]` handler always takes an explicit target tenant" (G2 decision 4).
- **Open until G6:** Story 6 scenario 1 (C-13 and C-14 in the register); S-08.
- **`LogScanTests`:** the only one is `Decisya.Bff.Tests/LogScanTests` (Keycloak, Integration). The Bff has no Wolverine, so the refusal event cannot appear in it. The scan for the new event is `EnvelopeRefusalLogScanTests` in `Decisya.Api.Tests`, built the same way (one capturing provider, Debug forced, every record scanned).
- **Isolation-test convention (Story 6):** `MessageHandlerListRule` accepts a file named `*IsolationTests.cs` under `tests/` that names the handler as a whole word and carries the `Integration` trait. The `isolation-test` skill's template path is `tests/Modules/Decisya.Modules.<Name>.Tests/<Name>IsolationTests.cs`, which fits. The convention is G5's; the first module that lists a handler may refine it under its own G2.
- **Rule limits (tripwires, not proofs):** R10 reads names and IL, not data flow; R6 matches member names containing `Tenant`. The behaviour tests and the storage wrapper (a `TenantKey` parameter, recorded for the storage issue) are the lasting controls.

<!-- gate: G5 | verdict: PASS-WITH-NOTES | issue: #231 -->
