# Architecture note – <title> (issue #<n>)

## Context
<What the issue changes, in two or three sentences. Links: requirements, ADRs.>

## C4 excerpt
```mermaid
flowchart LR
  %% containers/components touched by this issue
```

## Boundaries and contracts
- <module / Contracts types / messages / endpoints added or changed>

## Decisions
- <decision> — ADR-NNNN or "no ADR needed because …"

## NetArchTest rules to add
| Rule | Assemblies | Test class |
| --- | --- | --- |

<!-- gate: G2 | verdict: PASS | issue: #<n> -->

---
N/A form (use instead of everything above when nothing structural changes):

# Architecture note – <title> (issue #<n>)
No new module, public contract, cross-module dependency or data flow: <one-sentence reason>. Existing decisions that apply: ADR-NNNN.

<!-- gate: G2 | verdict: N/A | issue: #<n> | reason: <one-sentence reason> -->
