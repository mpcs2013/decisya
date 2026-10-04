# Phase 0 – Contain project agents: Bash allow-list, freeze on deny, stop-and-report (T-02)

## Issue #114 — chore(claude): contain project agents

### Scope note (role framing)

No tenant-facing capability. The only actor is **Marco as maintainer**, who runs project agents
through the gates with `/issue <n>`. **Entitlement plan: N/A** for every story (tooling and
agent configuration, no `module.feature` key applies), as in `docs/requirements/phase-0/roster-docs.md`.

Scope: the hook in `.claude/` enforces each listed project agent's declared `Bash(...)` patterns;
the first deny freezes that agent run; `devops.md` carries a stop-and-report rule; both incidents
(#28, #113) and T-02's new status are recorded. Out of scope: OS-level isolation (deferred to the
Phase 0 exit, #83 comment 5978529569). Mechanism (state storage, pattern matching) is G2's choice.

Facts (data, from the manifest): no permission rule allows `python` for agents; the `tools:` line
grants tool names only; subagents run under the main session's permission mode; hooks receive
`agent_type` and a per-run `agent_id`.

"Listed agent" means a project agent defined in `.claude/agents/`. "Agent run" means one
invocation of an agent, identified by its `agent_id`.

---

### Story 1 — A listed agent cannot run a Bash command outside its declared patterns

As Marco (maintainer), I want the hook to deny any Bash command a project agent runs that does not
match that agent's declared `Bash(...)` patterns, so that an agent cannot do more than its
definition says. (Done when 1)

```gherkin
Feature: Bash allow-list per project agent

  Scenario: A command outside the declared patterns is denied
    Given a project agent whose definition declares only "Bash(git status)" and "Bash(git diff:*)"
    When that agent runs the Bash command "python .claude/scripts/gates.py 114"
    Then the hook denies the call
    And the command does not execute

  Scenario: A command matching a declared pattern is allowed
    Given a project agent whose definition declares "Bash(git diff:*)"
    When that agent runs "git diff --stat"
    Then the hook does not deny the call

  Scenario: The deny reason tells the agent to stop and report
    Given a project agent whose Bash call was denied by the hook
    When the agent reads the deny reason
    Then the reason names the denied command class and the agent
    And it instructs the agent to stop and report to the caller
    And it instructs the agent not to retry, rephrase or work around the command

  Scenario: A listed agent with no Bash patterns declared cannot run Bash
    Given a project agent whose definition declares no "Bash(...)" pattern
    When that agent runs any Bash command
    Then the hook denies the call

  Scenario: Chained or substituted commands do not slip past a pattern
    Given a project agent whose definition declares "Bash(git diff:*)"
    When that agent runs "git diff && python evil.py" or "git diff $(python evil.py)"
    Then the hook denies the call

  Scenario: The main session is unaffected
    Given the main session (no agent_id in the hook input)
    When it runs any Bash command that is permitted today
    Then the hook does not deny it on allow-list grounds

  Scenario: Built-in and plugin agents are unaffected, as today
    Given an agent that is not defined in .claude/agents/
    When it runs a Bash command
    Then the allow-list does not apply to it
    And the hook's behaviour for it is the same as before this issue
```

---

### Story 2 — After one deny, the same agent run is frozen

As Marco, I want an agent run to be frozen after its first deny, so that a blocked agent cannot
keep probing for another way around the boundary. (Done when 2)

```gherkin
Feature: Freeze per agent run after a deny

  Scenario: The next Bash call after a deny is denied
    Given a project agent run whose Bash call was denied by the hook
    When the same run issues another Bash command, including one that matches a declared pattern
    Then the hook denies it
    And the reason says the run is frozen and tells the agent to stop and report

  Scenario: The next write after a deny is denied
    Given a project agent run whose Bash call was denied by the hook
    When the same run attempts a Write or Edit, including to a path it may normally write
    Then the hook denies it
    And the reason says the run is frozen and tells the agent to stop and report

  Scenario: A write-boundary deny also freezes the run
    Given a project agent run that attempted a Write outside its declared write boundary
    And the hook denied it
    When the same run issues a Bash command matching a declared pattern
    Then the hook denies it as frozen

  Scenario: A new agent run starts unfrozen
    Given a project agent run that is frozen
    When Marco (or the orchestrator) starts a new run of the same agent with a new agent_id
    Then that run's first allowed Bash command and allowed write are not denied
    And the earlier run stays frozen

  Scenario: Freezing one run does not affect another concurrent run
    Given two concurrent runs of different agents
    When one is denied and frozen
    Then the other run's allowed calls are still allowed

  Scenario: The main session is never frozen by an agent's deny
    Given a project agent run that is frozen
    When the main session runs a Bash command or writes a file
    Then the hook does not deny it because of the freeze

  Scenario: A frozen run still hands back its report
    Given a frozen project agent run
    When it finishes by calling SubagentHandback
    Then the call is not denied
```

---

### Story 3 — devops.md carries the stop-and-report rule

As Marco, I want `devops.md` to state that the agent must stop and report on any deny, so that the
agent's own instructions match what the hook enforces, and a later edit cannot silently drop it.
(Done when 3)

```gherkin
Feature: Stop-and-report rule in the devops agent

  Scenario: The rule is present
    Given .claude/agents/devops.md
    When it is read
    Then it states that after any hook or permission deny the agent stops, reports the
      denied command and the reason to its caller, and does not retry, rephrase or
      work around it

  Scenario: Removing the rule fails the suite
    Given the .claude test suite
    When the stop-and-report text is removed from devops.md
    Then a test fails and names the missing rule

  Scenario: Reworded but equivalent text is judged by the test, not by the maintainer
    Given the test pins the rule by a fixed marker
    When devops.md keeps the marker and rule
    Then the test passes
```

---

### Story 4 — Incidents and T-02 status are recorded

As Marco, I want incidents #28 and #113 and T-02's new status written down, so that the threat
register shows what happened and what is now mitigated versus deferred. (Done when 4)

```gherkin
Feature: Incident and threat-register records

  Scenario: Both incidents are recorded against T-02
    Given the threat model or register that holds T-02
    When it is read
    Then it lists incident #28 and incident #113 as findings against T-02
    And each states what the agent did, why it was possible (root cause above) and the date

  Scenario: T-02's status reflects this issue
    Given the same record
    When T-02's status is read
    Then it states what is now mitigated on the host (allow-list, freeze, stop-and-report)
    And it states what remains open: OS-level isolation, deferred to the Phase 0 exit
      with a reference to #83 comment 5978529569
```

---

### Story 5 — The existing checks stay green

As Marco, I want the repository checks to pass with the new hook behaviour, so that I can merge
without a regression in the guardrails. (Done when 5)

```gherkin
Feature: Checks pass

  Scenario: Lint and tests pass
    Given the change is complete
    When "python .claude/scripts/lint.py" and
      "python -m unittest discover -s .claude/tests -p test_*.py" run
    Then both exit 0

  Scenario: The pre-push check passes
    Given the change is committed on the branch
    When the pre-push hook runs
    Then it exits 0

  Scenario: Red cases exist for Stories 1 to 3
    Given the new tests
    When the hook change is reverted locally in a scratch copy (not git stash)
    Then at least one test per story fails
```

---

## Non-functional requirements

No new measurable target; no row added to `docs/requirements/nfr.md`.

## Open questions

None.

## Notes for G2/G3 (non-blocking)

- Behaviour fixed here: freeze is per `agent_id`; the main session has no `agent_id` and is never
  restricted; unlisted agents are unchanged. How the freeze persists and how patterns match is G2's
  choice, but compound commands (`&&`, `;`, `|`, `$(...)`, backticks, redirects) must not widen a
  pattern (Story 1, fifth scenario).
- The exemption for `SubagentHandback` in Story 2 is required so a frozen agent can report. G2
  to confirm it is the only exemption.
- Fail-closed expectation: if freeze state cannot be read for a listed agent, the call is denied.
  G3 to confirm.

<!-- gate: G1 | verdict: PASS | issue: #114 -->

## Traceability

Tests are in `.claude/tests/test_agent_containment.py` unless noted. Gherkin scenarios of Stories 1 to 3 are covered by the grouped tests below (Python unittest, no Gherkin-verbatim names). Suite result from G4 evidence: 274 tests OK; `lint.py` OK. This G5 run executed no Bash by instruction, so results are taken from G4 evidence and by reading the tests.

| Acceptance criterion | Test(s) |
|---|---|
| S1: command outside declared patterns is denied | `AllowListGrammarTests.test_red_cases_denied`, `RealAgentTests.test_denied` |
| S1: command matching a pattern is allowed | `AllowListGrammarTests.test_green_cases_allowed`, `test_legacy_colon_star_and_exact_patterns`, `RealAgentTests.test_allowed` |
| S1: deny reason says stop and report, no retry/rephrase | `FreezeTests.test_one_deny_freezes_the_run_and_only_that_run` (asserts `FREEZE_NOTICE`: "Stop now. Report to your caller ... Do not retry, rephrase, split it, or work around it"); `AllowListGrammarTests.test_reason_never_echoes_the_command` |
| S1: no Bash patterns means no Bash | `AllowListGrammarTests.test_no_patterns_means_no_bash`, `FreezeTests.test_agent_file_without_tools_line_fails_closed` |
| S1: chained or substituted commands denied | `AllowListGrammarTests.test_red_cases_denied`, `test_pattern_grammar` |
| S1: main session and unlisted agents unaffected | `FreezeTests.test_one_deny_freezes_the_run_and_only_that_run` (main session, general-purpose) |
| S2: next Bash/write after a deny is denied, with frozen reason | `FreezeTests.test_one_deny_freezes_the_run_and_only_that_run` |
| S2: write-boundary deny freezes the run | `FreezeTests.test_write_lane_deny_freezes_bash`, `test_secret_guard_deny_freezes_the_run` |
| S2: new run unfrozen; concurrent runs independent | `FreezeTests.test_one_deny_freezes_the_run_and_only_that_run`, `test_concurrent_marker_creation_is_success` |
| S2: main session never frozen | `FreezeTests.test_secret_guard_deny_freezes_the_run`, `test_one_deny_freezes_the_run_and_only_that_run` |
| S2: frozen run can still call SubagentHandback | `RealAgentTests.test_matcher_covers_every_side_effect_tool` (SubagentHandback not hooked) |
| S2 / G3 fail-closed: missing id or unreadable state denies | `FreezeTests.test_missing_or_invalid_agent_id_is_denied`, `test_unreadable_freeze_state_denies`; `GitignoreAndDenyTests.test_freeze_markers_are_outside_every_lane` |
| S3: rule present in devops.md; removal fails; marker pinned | `RealAgentTests.test_devops_stop_and_report_rule` |
| S4: incidents #28 and #113 recorded against T-02 | manual: read `docs/security/threat-models/claude-config.md` Incidents section (lines 90-93), findings T114-10 and T114-11 in `agent-containment.md`; documentation, no executable behaviour |
| S4: T-02 status (mitigated on host; OS isolation open, #83 comment 5978529569) | manual: read T-02 row (line 70) of `claude-config.md`; documentation |
| S5: lint and suite pass | `LintContainmentTests` (`test_good_agent_passes`, `test_red_frontmatter`); G4 evidence (274 OK, lint OK) |
| S5: pre-push check passes | manual: needs a clean committed tree; confirmed when Marco pushes |
| S5: red cases exist for Stories 1 to 3 | tests above include red (deny) cases for each story; scratch-copy revert not re-run in this Bash-free run (manual, from G4 evidence) |
| Done when 2 in a real run | manual: G4 live check in `docs/ai/pipeline/114.md`; the earlier G5 run was frozen after using `sed` outside its patterns and stopped as instructed |

<!-- gate: G5 | verdict: PASS | issue: #114 -->
