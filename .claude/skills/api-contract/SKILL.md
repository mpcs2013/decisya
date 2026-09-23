---
name: api-contract
description: "Defines or updates a module's OpenAPI 3.1 contract, regenerates the TypeScript client for the SPA, and adds a contract test. Use whenever an endpoint is added or changed, a request/response shape moves, or the frontend needs new data, even if the user only says \"expose X to the UI\"."
---
# api-contract

Contract first: the OpenAPI file is the source of truth; the API and the SPA client are both checked against it.

## Prerequisites
Run `python .claude/scripts/prereqs.py api-contract` from the repository root (VS 2026: *View → Terminal*). If anything is missing, report the listed issues and stop.

## Steps
1. Edit `src/Modules/<Name>/Decisya.Modules.<Name>.Contracts/openapi/<schema>.yaml`.
   - Paths under `/api/<schema>/...`; operationIds `<Module>_<Verb><Noun>`.
   - Money is `{ "amountMinor": int64, "currency": "EUR" }`; instants are RFC 3339 strings. (The wire format is not yet fixed by an ADR; write one with `adr-writer` the first time Money crosses the API.)
   - Every error response is `application/problem+json` with the shared `ProblemDetails` schema; no internal detail.
   - Security scheme is the BFF cookie; document the antiforgery header on non-GET operations.
   - Mark each operation with its entitlement feature key in `x-decisya-feature`, so the capability manifest and the endpoint policy stay aligned.
2. Implement or adjust the minimal API endpoints so the runtime-generated document matches the file.
3. Contract test in `tests/Decisya.ContractTests`: fetch `/openapi/<schema>.json` from a test host and diff against the yaml (fail on any path or schema drift).
4. Regenerate the client and run the checks:

| Visual Studio 2026 | CLI |
| --- | --- |
| *View → Terminal*: `cd src/Decisya.Web; npm run gen:api` | `cd src/Decisya.Web && npm run gen:api` |
| *Test Explorer → Run All* | `dotnet test --filter-trait "Category=Contract" --minimum-expected-tests 1` |

Commit the generated client with the contract.

## Output
Contract file, endpoints, contract test and generated client; record them in the manifest under G4.
