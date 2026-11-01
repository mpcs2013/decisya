# issue addendum (Decisya)

Read with Norma's `issue` skill. It adds context, inputs and checks; it never weakens a gate (#246, ADR-0023).

## Context
- Approver: Marco. A skip approval reads `approved: Marco <YYYY-MM-DD>`.
- Backlog: the current phase backlog issue (Phase 1: #243), reviewed once at the phase exit.
- The full tier also covers `.devcontainer/**` (the agent sandbox boundary, ADR-0010).

## Change classes
- ci-tooling: `.github/`, `Directory.Build.props`, `global.json`, `dotnet-tools.json`, the pre-commit configuration.
- dependency: `Directory.Packages.props` and `package.json` only.

## G1
Requirements written before the Norma lock live under `docs/requirements/<phase>/`; new ones use the core path.

## G4
- Routing: the roster in `docs/ai/README.md` (generated from `routing.G4` in `.claude/boundaries.json`).
- Build logic and dependency manifests belong to the main session, never to a lane agent: project and solution files (`*.csproj` and the other forms), `Directory.Build.*`, `*.props` (including `Directory.Packages.props`), `*.targets`, `NuGet.config`, `global.json`, `*.runsettings`, `package.json`, `.npmrc`, test-runner configs, and `.github/allowed-signers/**`. The shared `deny` in `.claude/boundaries.json` enforces it; the main session writes them at G4 before or after the lane agent runs, and Marco approves each write. The `main-session` row in `routing.G4` records it in the roster (#261); the signer files stay out of that row and are written only with Marco's approval.
- Checks the orchestrator re-runs:

| Visual Studio 2026 | CLI |
| --- | --- |
| *Build → Rebuild Solution*; *Test Explorer → Run All* | `dotnet build -warnaserror` then `dotnet test --no-build` |

## G7
- One line per package added in `Directory.Packages.props`, `dotnet-tools.json` or `package.json`, with its justification.
- Rollback names any data or config step, such as a volume reset.
- Only `Closes #<n>` uses a closing keyword; never write "close #n", "fix #n" or "resolve #n" for anything else.
