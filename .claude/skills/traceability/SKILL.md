---
name: traceability
description: "Maps every Gherkin acceptance criterion of an issue to the tests that prove it and records the result for gate G5. Use after implementation (G4) and whenever someone asks which tests cover a story."
---
# traceability

## Steps
1. Read the G1 requirements file named in `docs/ai/pipeline/<n>.md`; list every `Scenario:`.
2. List the tests (VS 2026: *Test Explorer*, grouped by class; CLI: `dotnet test --no-build --list-tests`).
3. Map each scenario to one or more tests by name. A scenario with no test: write the test (test-engineer) or mark it `manual` with a reason (e.g. needs a human visual check).
4. Append to the requirements file:

```markdown
## Traceability

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A known ISO 4217 code resolves to the correct exponent | `CurrencyTests.A_known_ISO4217_code_resolves_to_the_correct_exponent` | Unit |

<!-- gate: G5 | verdict: PASS | issue: #<n> -->
```

Use `verdict: BLOCK` while any scenario has neither a test nor a `manual` reason.

## Done when
Every scenario has a row and the verdict line is PASS.
