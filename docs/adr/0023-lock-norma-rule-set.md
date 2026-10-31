# 0023. Lock the shared Norma rule set into `.claude/`

- Status: Accepted (Marco, 2026-10-31)
- Date: 2026-10-31
- Deciders: Marco
- Tags: security, tooling, supply chain

## Context and problem statement

Decisya's agent machinery has been maintained by hand in this repository since Phase 0: the hooks in `.claude/hooks/`, the scripts in `.claude/scripts/` (gate checker, lint, roster, pre-push, prereqs), the process skills and the role agents. Marco's second application keeps its own copy, and fixes found in one copy have had to be carried to the other by hand (#217 and #218 went to Norma through the exchange as D-007 and N-014).

Norma is Marco's own project. It ships one shared copy of that machinery as a signed release: a core (hooks, scripts, process skills, role agents, shared rules, a base settings floor, and from 0.3.0 the usage capture `capture.py`) plus opt-in data packs (`dotnet`, `npm`, `python`). A consuming project installs a release and locks every installed file in `.claude/core.lock.json`. `lock.py verify` checks the installed copy against a signed tag and manifest (Norma 0.3.0 `docs/architecture.md`, "Units" and "How a release travels"). Decisya locks 0.3.0 first (#246, Marco 2026-10-31).

Locking it changes three things: who owns the rules in `.claude/`, how they change (every core change arrives as a version bump), and what Decisya trusts (a release signer line).

## Decision drivers

- One copy of each guardrail for both projects; no drift between copies.
- Decisya never ends up with weaker guardrails than it has today. A Decisya rule that is stricter than Norma's core must survive the lock and every bump.
- What runs as a hook or a gate check is pinned and verifiable offline, in the spirit of ADR-0014.
- The signer line is a trust root and changes only after Marco's own check, the same rule as ADR-0019 amendment A2.

## Considered options

1. **Lock Norma releases (core plus the `dotnet` and `npm` packs), adopt them into the existing project files, and keep Decisya's own rules in the project layer** (chosen).
2. **Keep the hand-maintained copy and port Norma's fixes by hand.** Rejected: two copies drift, and each fix is made twice.
3. **Vendor Norma's files without the lock.** Rejected: nothing would show that a core file was edited, and bumps would be hand merges.
4. **Let bootstrap write fresh project files.** Rejected: Decisya's `settings.json` and `boundaries.json` carry lanes, Docker allow-lists and `ask` entries that a fresh file would not have.

## Decision outcome

Chosen option: **1**, because it leaves one copy of each guardrail, makes every core change a reviewed bump, and keeps Decisya's own settings and rules.

1. **Ownership.**
   - Files listed in `.claude/core.lock.json` belong to Norma. Decisya never edits them, and a bump overwrites them.
   - `.claude/hooks/` and `.claude/scripts/` hold only release files.
   - Decisya owns the project layer: `.claude/project.json`, `.claude/boundaries.json`, `.claude/settings.json`, `CLAUDE.md`, its own agents and skills, and its own tests (consumer guide, "What you may change").
2. **First install by adoption.** Decisya already has its own `settings.json` and `boundaries.json`, so the first install runs bootstrap with `--adopt`, after `--adopt --plan` (consumer guide, "Settings for a project that has its own").
   - It only adds, at the end of each list: the base `deny` entries and the two core hook registrations to `settings.json`, and the packs' build-logic globs and the usage capture folder to the `deny` list of `boundaries.json`.
   - It never removes, reorders or replaces an entry. So Decisya's own `allow`, `ask` and `deny` entries, its lanes and its Docker allow-lists stay.
   - A file it cannot merge cleanly is merged by hand, the same way. Today's shell-form hook registration is such a case.
   - Later bumps add new entries by hand, as their CHANGELOG lines say; a locked project never runs a script from a bundle.
3. **Stricter local rules.** A Decisya rule that Norma's core does not hold is kept in one of these places:
   - as project data: a `deny` glob or lane in `boundaries.json`, an extra `deny` or `ask` entry in `settings.json`, or an extra entry in a `project.json` floor (`gates.review_required_paths`, `secret_guard.blocked_folders`);
   - as a Decisya guard hook outside the core folders (for example `.claude/guards/`), registered in `settings.json` next to the core hooks;
   - as a rule in `CLAUDE.md`, or in a Decisya-owned agent or skill.

   Each such rule is pinned by a Decisya regression test that runs against the installed core, so a bump that weakens it fails Decisya's own suite. Where only the core can hold a rule, Decisya asks Norma for a core option through the exchange (a draft that Marco approves). It never patches a locked file.
4. **Signer line.**
   - It lives in its own file, `.github/allowed-signers/norma` (ADR-0019 A2: a second signer gets its own file), named by `lock.signers_file`.
   - The file holds exactly one `norma-release` line with `namespaces="git"`, ends in LF, and `.gitattributes` keeps it byte-exact.
   - It changes only through a Decisya PR in which Marco has checked the fingerprint against the key on his own machine.
   - A key change follows the two-PR sequence of Norma's bump runbook ("When the release key changes").
5. **Usage capture.**
   - `usage.capture_folder` is `.local.usage` and `usage.project_id` is `GL-0001`, the id delivery tracking assigned. Decisya never makes one up.
   - `/.local.usage/` is ignored by git as a folder, and `.local.usage/**` is in the shared `deny` list, both in place before the first capture.
   - The folder is written only by the core `capture.py`, which the `issue` skill runs at G0 (`start`) and after the merge (`finish`). Nobody edits, moves or deletes a capture or the ledger.
   - It holds token counts only, read from the usage fields of this repository's Claude Code session records (consumer guide, "Usage capture"). Captures leave the machine only through Marco's own copy job, never through the repository.
   - Decisya runs under two Windows accounts, so an issue worked under both is finished with `--other-accounts` under each account, in that account's clone. Each capture then says `partial`, and the copy job lists both capture folders.
6. **Blocked folders.**
   - `secret_guard.blocked_folders` never names `.local.usage`, any `.local.u…` folder, `.local.docs` or any `.local.d…` folder.
   - Decisya's `ops.local/` becomes `.local.ops/` in its own issue, with the guard entry first. Until then the list adds nothing; today's guard does not block `ops.local/` either.
7. **Bumps.**
   - Marco runs the first-install check and every install in his own terminal, outside a Claude Code session, with the bundle kept outside the repository.
   - Each document beside the bundle is checked against the release notice before it is followed.
   - Each bump is its own issue at the full tier (G3 and G6). It runs on the host, since `.claude/**` is read-only in the sandbox (ADR-0010, ADR-0011), and it acts on every `ACTION REQUIRED:` line and on the `dotnet` and `npm` pack lines.
   - A bump is never auto-merged.
8. **Build logic.**
   - The packs' build-logic globs are in the shared `deny` list, so no listed agent writes project or build files. The 0.3.0 `issue` skill makes denied files the main session's to change.
   - The owner per glob group is recorded in `boundaries.json` `routing.G4` and in the rendered `docs/ai/README.md` (#246, `docs/architecture/246-norma-lock.md`).

### Consequences

- Good:
  - One maintained copy of the hooks, gate checker and lint for both projects; Decisya gets Norma's fixes by bump.
  - Adoption keeps every Decisya entry in `settings.json` and `boundaries.json`.
  - An edited, missing or extra core file fails `lock.py verify` in the pre-push check.
- Bad:
  - **Lock check in CI.** Until the CI lock check with the base branch's checker lands (backlog #243, item 246-1), the lock is checked only by the pre-push hook, which `git push --no-verify` skips, and by review.
  - **Fixes wait.** A Decisya-only fix to a core file waits for a Norma release. Until then a local guard, a settings entry or a rule in `CLAUDE.md` holds it.
  - **Decisya text is replaced.** Norma's versions replace the Decisya-specific text in the core skills and agents: the issue skill's local rules, and this repository's architect and security-reviewer texts. That text has to move to the project layer.
  - **Not authentication.** The lock is tamper evidence under review (Norma `docs/architecture.md`, "Guardrails, not a boundary"). It does not cover the CI workflow, a reviewed change to the checker or signer line, the project files, or code that runs as Marco.
  - **Accepted residual risk**, as in ADR-0019: the signed tag, the manifest and the bundle all come from Marco's key and account. A compromise of both produces a consistent but malicious release.
  - **Session records.** The usage capture reads Marco's Claude Code session records (usage fields only, as documented), and its counts are `partial` when two accounts or a sandbox worked on an issue.
  - **Build logic.** No listed agent writes `*.csproj`, `*.props`, `*.targets` or other build logic any more; that work moves to the main session at G4.
- Enforced by:
  - `lock.py verify` in the pre-push check (in CI once #243 item 246-1 lands);
  - `floors.py`;
  - `lint.py`, which fails on an unignored capture folder and warns on a missing build-logic or capture `deny` entry;
  - Norma's review-required floor, which covers the signer file;
  - the loader, which refuses a capture folder that the secret guard blocks;
  - Decisya's regression tests for its stricter rules.

Related: [ADR-0010](0010-agent-sandbox-devcontainer.md), [ADR-0011](0011-host-development-default.md), [ADR-0014](0014-ci-supply-chain-pinning.md), [ADR-0019](0019-ostium-identity-packages.md) (the per-signer file rule, A2).
