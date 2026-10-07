# Deployable stack (#120): acceptance traceability

- Issue: #120, 0.17b Deployable stack (Compose from `aspire publish`, Caddy, production config and secrets).
- G1 was skipped (no user-facing behaviour; Marco, 2026-10-05). The acceptance criteria are the issue's Done-when and Scope in `docs/ai/pipeline/120.md`, the G3 MUSTs G4-120-01 to 05 (`docs/security/threat-models/deployable-stack.md`), T120-01 to 05, and ADR-0016 R9 (`docs/architecture/phase-0-hosting.md`; `docs/architecture/deployable-stack.md` D10).
- Split: the NAS bring-up (DSM checks, deploy, first F1 to F4 run, browser trust) is #132. #120 proves the stack on a local Linux/Docker host.
- Test lanes:
  - .NET: `Unit`, `Integration`, `AppHost` traits.
  - Python (stdlib `unittest`, `deploy/tests/`): the D10 static guards, run by the CI job `deploy-guards` (Docker required).
  - The main session ran `deploy/tests` at 88 OK and 2 skipped (POSIX file modes on Windows).
- G5 additions in this run, all in `tests/Decisya.Infrastructure.Migrator.Tests/`:
  - `RoleAttributeCheckTests.cs` (new).
  - `StackMigratorRoleIntegrationTests.cs`: the elevated-role test and the PUBLIC CONNECT test.
- G5 evidence:
  - `dotnet build -warnaserror`: 0 warnings, 0 errors.
  - Migrator.Tests: 65 passed, 0 failed (37 unit, 28 Integration).
  - Integration lane: 28 passed.

## Traceability

Paths are relative to the repository root. `T:` is a .NET test class. `P:` is a Python test file in `deploy/tests/`.

### Done when

| Criterion | Test or evidence |
| --- | --- |
| From a clean clone, `aspire publish` plus the documented steps start the full stack on a Linux host with only the prepared secrets and local configuration | manual: `deploy/tests/stack_smoke.py` on a Linux Docker host (D10 item 12; not in CI, run by Marco; Windows has no POSIX modes). The parts that are testable without a daemon are automated: drift between the built AppHost's publish and `deploy/compose/docker-compose.yaml` (P: `test_ci_workflow.py::test_the_drift_check_runs_the_built_apphost_and_compares_with_the_committed_file`; P: `test_stackctl.py::AssembleTests::test_drift_between_published_and_committed_stops_assemble`), the assembled stack passing every guard (P: `test_compose_guards.py::test_assembled_stack_passes_every_guard_with_fixture_values`), and the publish model (T: `tests/Decisya.AppHost.Tests/ComposeStackPublishTests.cs`, 18 methods) |
| Every blocker in Scope is met, with tests where code changes | the Scope rows below |
| No home-network address is in the repository | P: `test_no_home_addresses.py` (`AddressScanTests::test_the_repository_holds_no_address_outside_the_allow_list`, `ScannerDetectsTests::*`); T: `ComposeStackPublishTests.The_generated_file_holds_no_address_literal_beyond_the_two_fixed_Caddy_addresses_and_loopback`; P: `test_stackctl.py::FileContentTests::test_the_environment_template_is_all_keys_with_empty_values`; the scan runs on every PR, docs-only included (P: `test_ci_workflow.py::test_the_address_scan_runs_first_and_alone`) |

### Scope

| Scope blocker | Test or evidence |
| --- | --- |
| The stack: Compose from `aspire publish`, #119 images by digest, Caddy `tls internal`, `app`/`id` hosts on 8443, bind address and allow-lists from local configuration | T: `ComposeStackPublishTests` (`The_generated_file_has_exactly_the_expected_services`, `The_generated_file_pins_each_third_party_image_by_tag_and_digest`, `The_generated_file_uses_a_placeholder_for_each_release_image`, `The_publish_model_has_no_parameter_at_all_so_no_secret_parameter`, `The_publish_model_holds_exactly_the_expected_services_and_no_dashboard`); P: `test_caddyfile.py` (`CaddyTextRules`, `CaddyAdaptRules::test_adapted_config_equals_the_edge_table`); P: `test_stackctl.py::ValueGuardTests` (the local values are validated); the first start is manual (stack smoke) |
| Networks: back-channel network (Caddy, BFF, Api only), separate `internal: true` Caddy-Keycloak network (B-6); Keycloak admin paths workstation-only; port 9000 never routed | P: `test_compose_guards.py::test_each_network_and_port_rule_can_fail`, `test_the_overlay_publishes_exactly_one_port_with_required_references`, `test_committed_files_pass_every_guard_without_interpolation`; P: `test_caddyfile.py::CaddyAdaptRules::test_each_edge_rule_can_fail`; source addresses on the published port: manual (stack smoke, recorded) |
| R9 guards: no host network, privileged, Docker socket or personal mounts; memory limits of about 3.5 GB; key ring read-write in the BFF and read-only in backup only | P: `test_compose_guards.py` (`test_each_isolation_rule_can_fail`, `test_each_mount_rule_can_fail`, `test_committed_yaml_text_has_no_banned_key_or_socket`, `test_assembled_stack_passes_every_guard_with_fixture_values`); see the R9 rows below |
| Image scan: Caddy and the collector join the ADR-0015 targets; every Compose digest is scanned | P: `test_stackctl.py::FileContentTests::test_the_image_scan_covers_caddy_and_the_collector`; T: `ComposeStackPublishTests.Third_party_services_carry_the_ContainerImages_pin`; `.claude/tests/test_release.py` (the `ALIASES` assertion); T: `tests/Decisya.Identity.Tests/ContainerImageParityTests.cs` |
| Root trust: root exported into `decisya-p0`, mounted read-only into BFF and Api, trusted per back-channel client (B-3) | T: `tests/Decisya.Bff.Tests/Trust/BackchannelTrustTests.cs`, `tests/Decisya.Api.Tests/Authentication/JwksTrustTests.cs`, `tests/Decisya.Bff.Tests/Trust/CaddyRootTrustTests.cs` (Integration); see G4-120-05 |
| C-20: secret store and rotation | P: `test_stackctl.py` (`SecretGenerationTests`, `SecretsCommandTests`, `CanaryTests`); T: `tests/Decisya.ServiceDefaults.Tests/Production/SecretFilesTests.cs`; see G4-120-01 |
| C-06: migrator not a superuser, per-module roles, PUBLIC CONNECT revoked | T: `StackMigratorRoleIntegrationTests.The_non_superuser_stack_migrator_can_run_MigrationRunner_and_provision_the_module_roles`; T: `StackMigratorRoleIntegrationTests.A_pre_existing_module_role_with_an_elevated_attribute_fails_the_run_closed`; T: `StackMigratorRoleIntegrationTests.PUBLIC_has_no_CONNECT_and_each_role_connects_only_to_its_own_database_in_the_stack`; T: `RoleAttributeCheckTests` (unit); T: `tests/Decisya.Identity.Tests/PostgresInitScriptSourcingTests.cs` (the init script runs when sourced without the exec bit; the migrator is created and owns `decisya`; no migrator password with `DECISYA_STACK=1` fails closed). Module roles with no DDL: `tests/Decisya.Infrastructure.Migrator.Tests/MigrationRunnerIntegrationTests.Decisya_tenancy_role_has_no_DDL_privilege_and_cannot_read_the_migrations_history_table`. See G4-120-04 |
| C-11: internal transport; exposure and authentication of Redis and Postgres | P: `test_compose_guards.py` (only Caddy publishes a port; per-store internal networks); P: `test_stackctl.py::SecretGenerationTests::test_redis_acl_comes_from_the_one_policy_and_holds_only_a_hash`, `FileContentTests::test_redis_conf_directives`; accepted residual per G3. Redis and Postgres refusing an unauthenticated TCP login on the live stack: manual (stack smoke), because no Redis integration test mounts the stack's ACL file |
| Production configuration (#15): environment pinning, `AllowedHosts`, health on an internal port, trace sampling, `UserIdHashKey` per environment, OTLP to the collector | T: `ComposeStackPublishTests.The_web_hosts_are_pinned_to_Production_with_two_listeners_and_a_trusted_proxy`; T: `tests/Decisya.ServiceDefaults.Tests/Production/AllowedHostsTests.cs`; T: `tests/Decisya.ServiceDefaults.Tests/Production/ManagementHealthTests.cs`; T: `tests/Decisya.ServiceDefaults.Tests/Telemetry/TelemetryConfigurationTests.cs`; P: `test_stackctl.py::FileContentTests::test_the_collector_exports_nowhere_and_stays_at_basic_verbosity`; the `UserIdHashKey` file is covered by the G4-120-01 consumer-set guard. Trace sampling is covered by the telemetry test only to the extent it exists there; the live OTLP path is manual (stack smoke) |
| D-1: image-scan exceptions re-assessed | P: `test_stackctl.py::FileContentTests::test_the_libc6_exceptions_on_the_request_path_expire_in_30_days`; the zlib and pcre2 justifications still say NEEDS CONFIRMATION (the G4 flag) and are a G6 and Marco item, not a test |
| ADR-0001 amendment (Azure Container Apps review trigger) | manual: a documentation change (`docs/adr/0001-multi-tenant-single-instance.md`), reviewed in G6 and the PR |

### G3 MUSTs

| MUST | Test or evidence |
| --- | --- |
| G4-120-01 Secrets reach containers only as files, only to their listed consumers; none in `docker inspect`, argv, a log or an image | P: `test_compose_guards.py::test_each_secret_rule_can_fail` (exact consumer sets, `env_file`/`build`/`include`/`extends` banned, `HEADERS` and userinfo patterns); T: `ComposeStackPublishTests.No_environment_name_that_looks_like_a_credential_carries_anything_but_a_secret_file_path`, `.The_generated_file_has_no_dashboard_and_no_Aspire_dashboard_or_OTLP_header_variable`, `.Healthchecks_use_the_probe_argument_or_a_credential_free_command`; P: `test_stackctl.py` (`ScramTests`, `SecretsCommandTests::test_apply_db_sends_a_verifier_over_stdin_and_never_the_password_in_argv`, `SecretGenerationTests::test_connection_strings_never_carry_error_detail_or_persisted_security_info`, `FileContentTests::test_both_init_scripts_keep_the_password_out_of_argv_and_the_log`, `CanaryTests`); T: `SecretFilesTests`; the no-secret-in-`docker inspect`/log canary on the running stack is manual (stack smoke) |
| G4-120-02 The edge admits exactly the D2 table; nothing internal reachable through it | P: `test_caddyfile.py` (`CaddyTextRules`, `CaddyAdaptRules`); P: `test_stackctl.py::ValueGuardTests`; T: `ManagementHealthTests` (`Health_is_404_on_the_application_port_outside_Development`, `A_Host_header_naming_the_management_port_gets_no_health_response_on_the_application_port`, `The_management_port_ignores_AllowedHosts_and_serves_nothing_else`); T: `tests/Decisya.ServiceDefaults.Tests/Production/ForwardedHeadersTests.cs`; T: `tests/Decisya.ServiceDefaults.Tests/Production/HealthProbeTests.cs`; live edge behaviour (403 on `/admin/` and `/realms/master/`) is manual (stack smoke) |
| G4-120-03 What runs is what was guarded: isolation, image provenance, guards that cannot be skipped | P: `test_compose_guards.py` (merged-output guards, `test_generated_file_carries_no_overlay_key`, `test_generated_file_guard_can_fail`); P: `test_stackctl.py::StaticCheckTests`; P: `test_ci_workflow.py::DeployGuardsJobTests` (no `paths:`, never skipped, Docker required, SHA-pinned actions); `stackctl.py verify` against running containers is manual (stack smoke). Whether Caddy runs under `cap_drop: ALL`: manual (first start) |
| G4-120-04 Database principals are least privilege (C-06) | the C-06 row above (two migrator tests, the PUBLIC CONNECT test, `PostgresInitScriptSourcingTests`); P: `test_compose_guards.py` bans `POSTGRES_HOST_AUTH_METHOD`; T: `ComposeStackPublishTests.Postgres_reads_its_passwords_from_files_and_fails_the_init_without_the_migrator_password`. SCRAM on TCP: `MigrationRunnerIntegrationTests.The_computed_SCRAM_verifier_authenticates_with_the_original_plaintext_password_and_is_stored_in_verifier_form`. "A TCP login without a password fails" is manual (stack smoke) |
| G4-120-05 Back-channel trust is per client, pinned to the mounted root, fails closed | T: `BackchannelTrustTests` (`The_mounted_root_is_trusted`, `A_foreign_root_is_rejected`, `A_certificate_from_the_mounted_root_for_another_name_is_rejected`, `A_certificate_issued_by_a_system_trusted_root_is_rejected_under_the_pinned_policy`, `Start_up_fails_for_a_bad_trusted_root_file`); T: `JwksTrustTests`; T: `CaddyRootTrustTests.The_exported_caddy_root_is_trusted_and_a_second_caddys_root_is_not` (Integration); T: `tests/Decisya.Bff.Tests/Trust/CertificateValidationSourceRuleTests.No_production_source_overrides_or_disables_certificate_validation` |

### G3 Medium findings

| Finding | Test or evidence |
| --- | --- |
| T120-01 The generated file can run without the overlay | P: `test_compose_guards.py::test_generated_file_carries_no_overlay_key`; T: `ComposeStackPublishTests.The_generated_file_carries_no_key_the_overlay_owns`; P: `test_stackctl.py::FileContentTests::test_no_file_in_the_compose_folder_has_an_env_style_name`, `StaticCheckTests::test_the_runner_always_passes_both_files_a_project_directory_and_the_environment_file`. Running the generated file alone from a Container Manager project is a #132 G3 decision |
| T120-02 `RequireHost("*:8081")` matches the Host header | T: `ManagementHealthTests.A_Host_header_naming_the_management_port_gets_no_health_response_on_the_application_port` and `.Health_is_404_on_the_application_port_outside_Development` |
| T120-03 Secrets are not tied to their consumers | P: `test_compose_guards.py::test_each_secret_rule_can_fail`; P: `test_stackctl.py::SecretGenerationTests::test_every_secret_file_is_produced_by_exactly_one_credential`; T: `tests/Decisya.ServiceDefaults.Tests/Architecture/AppHostConfigurationTests.cs` (the Api gets only the tenancy and entitlements strings) |
| T120-04 The stack's environment-file values are not validated | P: `test_stackctl.py::ValueGuardTests` (by type, host-network overlap, three hosts differ, no problem text repeats a value); P: `test_caddyfile.py::CaddyAdaptRules::test_fixture_allow_lists_overlap_no_docker_network` |
| T120-05 `deploy-guards` can be skipped | P: `test_ci_workflow.py::DeployGuardsJobTests::test_the_workflow_has_no_paths_filter` and `::test_the_job_is_never_skipped`; the required-check entry is manual (Marco, after merge) |

### ADR-0016 R9 (NAS isolation)

| Rule | Test or evidence |
| --- | --- |
| R9.1 Dedicated DSM user | manual: DSM, #132 |
| R9.2 Dedicated shared folder, restricted | manual: DSM, #132. The folder layout and modes are asserted on the stack side by P: `test_stackctl.py::AssembleTests::test_modes_on_a_posix_host` (skipped on Windows) and `SecretsCommandTests::test_a_world_writable_secrets_folder_or_file_is_flagged` |
| R9.3 No Docker socket | P: `test_compose_guards.py::test_committed_yaml_text_has_no_banned_key_or_socket`, `::test_each_mount_rule_can_fail` |
| R9.4 No personal folder, `/volume*` root, `/` or DSM path mounted | P: `test_compose_guards.py::test_each_mount_rule_can_fail` (bind sources only relative `./config/` and `./trust/` files) |
| R9.5 No privileged, `cap_add`, `devices`, host network or pid; `no-new-privileges`; `cap_drop` | P: `test_compose_guards.py::test_each_isolation_rule_can_fail`; Caddy under `cap_drop: ALL` at runtime: manual (first start) |
| R9.6 Only Caddy publishes a port; internal networks; member lists as the D2 table | P: `test_compose_guards.py::test_each_network_and_port_rule_can_fail`, `::test_the_overlay_publishes_exactly_one_port_with_required_references` |
| R9.7 No port-forward or UPnP | manual: router, #132 |
| R9.8 DSM firewall; Caddy `remote_ip` allow-lists | P: `test_caddyfile.py::CaddyAdaptRules` (the allow-lists, admin paths workstation-only, no upstream on 9000 or 8081); DSM firewall and "no container reaches DSM ports 5000, 5001 and 22": manual, #132 |
| R9.9 DSM hardening | manual: DSM, #132 |
| R9.10 DSM and Container Manager kept updated; password SSH refused | manual: DSM, #132 |
| R9.11 End date of DS918+ security updates recorded | manual: Marco, #132 |
| R9.12 Per-container memory, CPU and PID limits; rotated logs | P: `test_compose_guards.py::test_each_isolation_rule_can_fail` (limits, sum at most 3.5 GB, `cpus`, `pids_limit`, log rotation) |
| R9.13 QuickConnect disabled | manual: DSM, #132 |
| R9.14 The other backup-target project kept separate | manual: DSM, #132 |

### Manual rows

| Item | Reason | Where recorded |
| --- | --- | --- |
| Clean-clone stack smoke run on a Linux Docker host (`deploy/tests/stack_smoke.py`) | needs a Linux daemon and the published release images; not in CI by design (D10 item 12) | #132 or a local Linux host; the result in `docs/ai/pipeline/120.md` |
| First-start items: Caddy under `cap_drop: ALL`; the BFF writing `/home/app/keyring`; source addresses kept on the published port; unauthenticated Redis and Postgres TCP login refused; back-channel routing; `stackctl.py verify`; no secret value in `docker inspect`, `compose config` or logs | runtime behaviour of containers; no CI daemon runs the full stack | the stack smoke run |
| `deploy-guards` added to the ruleset's required checks | repository setting; only after the job has passed on `main` | Marco, after merge (`docs/runbooks/main-ruleset.md`) |
| R9.1, 2, 7, 8 (DSM part), 9, 10, 11, 13, 14 | DSM and router settings on the NAS | #132 |
| ADR-0001 amendment | a documentation change | G6 and the PR |
| D-1 zlib and pcre2 justifications (NEEDS CONFIRMATION) | needs Grype match details and Marco's decision | G6 and Marco |

### Gaps and notes

- Not run by this gate: the Python `deploy/tests` (run by the main session, 88 OK, 2 skipped on Windows) and the `Category=AppHost` suite (main session, 34 passed, 1 skipped for #70, 2026-10-06).
- The PUBLIC CONNECT and cross-database test was added by G5 because no test covered G4-120-04's "PUBLIC has no CONNECT on postgres, template1, decisya or keycloak" or "Keycloak's role cannot connect to decisya". It checks `pg_database` ACLs and `has_database_privilege`. It does not test a live login.
- `PostgresInitScriptSourcingTests` was not red-run against the old script (never committed); the red evidence is the 2026-10-06 AppHost log.

<!-- gate: G5 | verdict: PASS | issue: #120 -->
