# 0004. Vendor-neutral AI via Microsoft.Extensions.AI

- Status: Accepted
- Date: 2026-09-20
- Deciders: Marco
- Tags: ai

## Context and problem statement

AI features must not couple the product to one model provider or a paid dev loop.

## Decision drivers

- Solo developer; minimise operating cost before revenue
- Financial data: security and tenant isolation are non-negotiable
- Easy migration to a more robust hosting model later

## Considered options

1. **Provider SDK directly** — fastest start; lock-in.
2. **Semantic Kernel / Microsoft Agent Framework** — orchestration heavier than needed today; both build on Microsoft.Extensions.AI, so adopting one later does not reverse this decision.
3. **Microsoft.Extensions.AI (IChatClient, IEmbeddingGenerator)** — thin, free, swappable by DI.

## Decision outcome

Chosen option: **Microsoft.Extensions.AI (IChatClient, IEmbeddingGenerator) as the only AI surface for application code; provider packages (including OllamaSharp) only in Decisya.Infrastructure.Ai; Ollama on the host in dev; in prod a hosted provider with a Decisya-metered monthly cap plus tenant BYO key; embeddings generated locally and stored in Postgres with pgvector, introduced by the first issue that needs embeddings (it changes the pinned Postgres image, ADR-0007)**

### Consequences

- Good: Provider swap is a DI registration; zero dev cost
- Bad: Quality differs between local and hosted models; needs an eval set before Phase 5
- Bad: A hosted provider receives financial data: provider, region and retention are recorded per provider, tenants opt in, prompts carry the minimum data, and transaction text is treated as untrusted input
- Bad: BYO keys are tenant secrets, encrypted at rest and marked [Sensitive]
- Bad: pgvector is not in the official postgres image; the embedding issue switches the image for AppHost, Testcontainers and the sandbox list together
- Bad: The agent sandbox cannot reach any model; AI tests there use a fake IChatClient (ADR-0010)
- Invariants: CLAUDE.md invariants 2, 3 and 6 are the rules of this ADR
- Enforced by: Decisya.ArchitectureTests: no assembly except Decisya.Infrastructure.Ai references a vendor AI assembly (deny list kept in the test: OpenAI, Azure.AI.*, Anthropic*, OllamaSharp, Google.GenAI, Mistral*, AWSSDK.BedrockRuntime); the deterministic modules (ledger, budgets, forecasts, alerts) do not reference Microsoft.Extensions.AI*. Per AI feature: a test with IChatClient unavailable asserting the degraded response, and a test that the response carries the standard disclaimer
