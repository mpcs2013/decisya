---
name: traceability
description: "Maps every Gherkin acceptance criterion of an issue to the tests that prove it and records the result for gate G5. Use after implementation (G4) and whenever someone asks which tests cover a story."
---
# traceability

## Steps
1. Read the G1 requirements file named in `docs/ai/pipeline/<n>.md`; list every `Scenario:`.
2. List the tests with the project's test-listing command (it comes from the stack pack or the test-engineer addendum).
3. Map each scenario to one or more tests by name. A scenario with no test: write the test (test-engineer) or mark it `manual` with a reason (for example, it needs a human visual check).
4. Append to the requirements file:

```markdown
## Traceability

| Story | Scenario | Test(s) | Lane |
| --- | --- | --- | --- |
| 1 | A valid input is accepted | `InputTests.A_valid_input_is_accepted` | Unit |

<!-- gate: G5 | verdict: PASS | issue: #<n> -->
```

Use `verdict: BLOCK` while any scenario has neither a test nor a `manual` reason.

## Done when
Every scenario has a row and the verdict line is PASS.
