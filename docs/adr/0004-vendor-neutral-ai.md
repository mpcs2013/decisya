# 0004. Vendor-neutral AI via Microsoft.Extensions.AI

- Status: Proposed
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
2. **Semantic Kernel** — heavier abstraction than needed today.
3. **Microsoft.Extensions.AI (IChatClient, IEmbeddingGenerator)** — thin, free, swappable by DI.

## Decision outcome

Chosen option: **Microsoft.Extensions.AI; Ollama in dev; hosted API with a monthly cap plus BYO-key in prod; embeddings local with pgvector**

### Consequences

- Good: Provider swap is a DI registration; zero dev cost
- Bad: Quality differs between local and hosted models; needs an eval set before Phase 5
- Enforced by: Architecture test: no vendor AI namespace referenced outside Decisya.Infrastructure.Ai
