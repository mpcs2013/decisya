<!-- gate: G3 | verdict: PASS-WITH-NOTES | issue: #135 -->
# Threat delta: roster ignores untracked local skills (issue #135)

- Scope: how `.claude/scripts/roster.py` `read_sources` chooses its skill inputs, and the extra commit that records `deploy-guards` in `.github/rulesets/main.json`. Tooling only. No Decisya application trust boundary changes.
- Baselines: `roster-docs.md` (#76: T76-xx, G4-76-xx, NFR-19), `claude-config.md` (cc:T-xx). New threats use T135-xx. Requirements use G4-135-xx.
- Boundary: this is a **host** run (manifest). The hooks and lint are guardrails, not a security boundary (cc:T-01, #56 H-1).
- ASVS 5.0 is mapped by analogy at section level, as in the baselines: V2.2 input validation, V15.2 dependencies and architecture, V15.3 defensive coding, V16.5 error handling. Check the numbers against the official 5.0 text before copying them into a compliance artefact.
- Reviewer: security-reviewer agent, 2026-10-07. Mode: G3, before G4.

## Verdict

**PASS-WITH-NOTES. Use option A (read the Git index with the standard library), for skills only.** There is no High. The roster is documentation plus a staleness check. No hook, lint rule or gate uses it to discover skills or agents (evidence below). Hiding a skill from the roster therefore takes away no enforcement, as long as lint keeps scanning every skill on disk (G4-135-04).

One Medium is fix-now, and it is in the extra commit: **the working-tree `.github/rulesets/main.json` is invalid JSON** (T135-06, G4-135-05).

## Evidence (2026-10-07, code reading)

- `roster.py` `read_sources` globs `.claude/agents/*.md` and `.claude/skills/*/SKILL.md` from disk. Its output goes only to the two marked blocks (`docs/ai/README.md` and the routing block in `issue/SKILL.md`). The routing block is built from `routing.G4` alone (G4-76-11), so skills never reach it.
- `lint.py` `main()` builds its own on-disk lists (lines 356-358). It does not call the roster for discovery. On every on-disk `SKILL.md`, lint enforces `RISKY_INSTRUCTIONS`, the frontmatter key allow-list, the name grammar and local references (lines 387-404). It uses the on-disk skill set for `check_skill_refs`. It calls `roster.check()` only as the staleness check (line 407).
- `_hooklib.frontmatter_names()` reads `.claude/agents/*.md` from disk. It is the hook's fallback when `boundaries.json` fails to load. It does not depend on the roster.
- `gates.py` `REVIEW_REQUIRED_PATHS` matches changed file paths. It does not depend on the roster.
- `test_roster.py` builds its trees in a temporary directory without `.git`. `OfflineTests` patches `subprocess.run`, `Popen` and `socket.socket`.
- CI `claude-config` runs `lint.py` and `roster.py --check` on a fresh checkout. There, files on disk and tracked files are the same set, so the CI result does not change under any of the options. **CI stays the authority. Only the local pre-commit and pre-push result changes.**
- Ruleset: the diff adds the `deploy-guards` object after the `image-scan` object with no comma between them (lines 67-68). `test_ruleset.py` `real_inputs()` runs `json.loads` on the file, which raises. Any apply step that uses the file would fail too. The `deploy-guards` job in `ci.yml` has no `needs`, no `if` and no path filter, so it is a valid required check under G4-80-04.

## Ruling on the options

| Option | Ruling | Reason |
| --- | --- | --- |
| **A. Read the Git index (stdlib)** | **Chosen, skills only** | It matches Git's real meaning of "tracked". A staged new skill counts, which matches what the commit will contain. Ignore rules cannot hide a tracked file. NFR-19 still holds. The risks are local to the parser and are handled by G4-135-01 to 03. |
| B. Honour the ignore files | Rejected | Gitignore semantics would be partial. It also inverts Git's rule: an ignore pattern does **not** untrack a tracked file. A local `.git/info/exclude` entry could then hide a tracked skill locally. |
| C. `git ls-files` subprocess (amend NFR-19) | Rejected | It amends a #76 MUST when A works without that. It brings Git config and the hook's `GIT_*` environment into a check that is hermetic today. |
| D. Frontmatter `local: true` | Rejected | It is a hiding switch that works **in CI too**. A tracked skill marked `local: true` would drop out of the committed README while Claude Code still loads it. Lint's `SKILL_FRONTMATTER_KEYS` would also have to be widened. |

Answers to the open questions on option A:
- **Unreadable, unknown-version, split or sparse index:** fall back to everything on disk (today's behaviour). Never raise an error. A fallback can at worst produce a false "stale" locally, which is today's bug. It never removes a check, and CI is unchanged (G4-135-02).
- **Worktrees (`.git` is a file):** SHOULD resolve one `gitdir: <path>` line. A missing or odd target falls back to disk (G4-135-02, S-2).
- **A staged but uncommitted new skill** counts as tracked. So does any index entry, whatever its flags (skip-worktree, assume-unchanged, intent-to-add).
- **Spoofing by un-tracking (`git rm --cached`):** the commit then deletes `SKILL.md`. The deletion and the removed README row both show in the PR diff. On CI the skill no longer exists, so nothing is hidden from what CI and other machines load. A skill that only exists locally is loaded by Claude Code on that machine whatever the roster says. The roster was never the control for that. Lint is (G4-135-04).

## Threats

| Id | Element / flow | STRIDE | Threat | Severity | Mitigation | ASVS 5.0 id | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T135-01 | `.git/index` → roster skill set | T, I | Index-derived path strings used as file paths or as rendered text would bring a new input into the README and into file opens (traversal, injection). | Low | Use the index only as a membership filter on paths the existing glob already found. Never open or render index strings (G4-135-01). | V2.2, V15.3 | Mitigated by requirements |
| T135-02 | Index parser | D, R | A truncated, corrupt, huge, SHA-256-format, split (`link`) or sparse index makes the parser raise (lint then reports "roster check failed" and blocks every commit), hang, or return a **partial** set. | Low | Bounded parse with a checksum. Fall back to disk on any doubt. Never return a partial set (G4-135-02). | V15.3, V16.5 | Mitigated by requirements |
| T135-03 | NFR-19 | E | A "convenience" call to `git`, or reading `GIT_DIR`/`GIT_INDEX_FILE` from the environment, reopens the #76 concern. | Low | Stdlib only, read-only, and `OfflineTests` cover the index path (G4-135-03). | V15.2 | Mitigated by requirements |
| T135-04 | Skill discovery reuse | E | G4 reuses the new "tracked only" filter in `lint.py`. Then an untracked local skill, which Claude Code still loads, skips the `RISKY_INSTRUCTIONS` and frontmatter checks. | Medium (if built that way) | Lint and hooks keep discovering from disk. The filter lives in `roster.py` only (G4-135-04). | V15.3 | Mitigated by requirements |
| T135-05 | Un-tracking or marking to hide | R | Someone hides a real skill from the README. | Low (A) / Medium (D) | With A, hiding needs a deletion visible in the diff, and CI is unchanged. D is rejected. | V15.3 | Accepted (A) |
| T135-06 | `.github/rulesets/main.json` (extra commit) | D, R | The file is invalid JSON, with a missing comma between the `image-scan` and `deploy-guards` objects. `test_ruleset.py` fails, so `claude-config` goes red. Re-applying the ruleset from the file would fail. The live ruleset is not affected (it fails safe), but the "ruleset as code" record (#80) is broken. | Medium (fix-now) | Add the comma. `test_ruleset.py` is green (G4-135-05). | V15.3 | Open: fix in this PR |

## Requirements for G4 (main session)

MUST. Tests go in `.claude/tests/`. Record red and green runs in the manifest's G4 evidence.

- **G4-135-01 (membership filter only).** `read_sources` keeps globbing `.claude/skills/*/SKILL.md` from the working tree and reads each file's content from disk, as now. When the index is usable, a skill is kept when its exact path `.claude/skills/<dir>/SKILL.md` is an index entry at any stage and with any flags. Index strings are never opened, joined into paths or rendered. Agents are not filtered; they stay as they are today. *Check:* tests in a temporary tree with a fixture index:
  - an ignored local skill on disk but not in the index makes `check()` return `[]` (Done-when 1);
  - a tracked skill that is missing from the README is still stale (Done-when 2);
  - a skip-worktree entry and a staged new skill count as tracked;
  - `test_committed_blocks_are_current` is unchanged (Done-when 3).
- **G4-135-02 (fail toward disk, never partial, never raise).** Fall back to every skill on disk when any of these holds:
  - there is no `.git`;
  - `.git` is a file with no single resolvable `gitdir:` directory;
  - the index is missing or unreadable, or larger than a fixed cap (for example 64 MiB);
  - the signature is not `DIRC`;
  - the version is not 2, 3 or 4;
  - the trailer matches neither SHA-1 over the content (20-byte ids) nor SHA-256 (32-byte ids);
  - an entry is truncated or the entry count runs past the end of the file;
  - a sparse-directory entry (a path ending in `/` with skip-worktree set) or a `link` (split index) or `sdir` extension is present;
  - **any** exception occurs inside the parser.

  The parser returns either the complete set or "unknown", nothing in between. *Check:* red fixtures for a bad signature, version 5, a flipped checksum byte, a truncated entry, a `link` extension and a sparse entry. Each one gives today's disk result and no exception. One v4 fixture checks path-prefix decompression.
- **G4-135-03 (NFR-19 kept).** Standard library only (`hashlib` and `struct` are fine). No `subprocess` and no network. `GIT_DIR`, `GIT_INDEX_FILE` and `GIT_WORK_TREE` are not read. Files under `.git` are opened read-only, and no `index.lock` is created. *Check:* `OfflineTests` run `check()` on a tree that has a fixture `.git/index`, so the index path runs under the patches, and a test asserts that no file under `.git` is opened for writing. `test_check_is_fast` still passes.
- **G4-135-04 (lint and hooks keep disk discovery).** `lint.py` `main()` and `_hooklib.frontmatter_names()` are not changed to use the tracked filter. An untracked local skill is still checked by lint for `RISKY_INSTRUCTIONS`, the frontmatter keys and the name grammar. *Check:* a lint test with an untracked skill containing a risky line still reports it.
- **G4-135-05 (ruleset commit, fix-now).** Add the missing comma after the `image-scan` object in `.github/rulesets/main.json`. `python -m unittest .claude/tests/test_ruleset.py` (or the full `.claude/tests` discover) is green before the `chore(ci)` commit. The file must match the live ruleset exactly (the same contexts, every one with `integration_id` 15368).

### SHOULD

- S-1. In `test_ruleset.py`, add `deploy-guards` to `FLOOR` and to `NEVER_CONDITIONAL`, so the file cannot quietly drop it and the job cannot gain `needs` or `if`. Each is one line in a review-required file. Do it in the `chore(ci)` commit, or log it on backlog #83.
- S-2. Resolve a worktree's `gitdir:` (relative to the `.git` file's directory), with one read and no symlink chasing beyond `Path.resolve`. If that is not built, document that worktrees fall back to disk.
- S-3. When a `.git` exists but the fallback is taken, print one line to stderr (`roster: git index not usable; using all skills on disk`) so a broken parser is visible. Print nothing when there is no `.git` (tests, exports).
- S-4. Update the `roster.py` docstring (Sources line) to say that skills are limited to tracked ones when the index is readable.

## Residual risk after #135

- The local check is advisory. During `git commit -a` or `git commit <paths>`, Git uses a temporary index (`GIT_INDEX_FILE`), which G4-135-03 deliberately ignores. In that case the local view can differ from the commit. The required `claude-config` check in CI, on a clean checkout, stays the authority.
- An untracked local skill is still loaded by Claude Code on that machine and is no longer listed in the roster. This existed before. Lint still scans it (G4-135-04), and the hooks and lanes do not depend on skills.
