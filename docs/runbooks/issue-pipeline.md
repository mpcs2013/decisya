# Issue pipeline (agents, gates, lint)

- Owner: devops · Last verified: 2026-09-23 (SDK 10.0.401, Aspire 13.5.4, Python 3.14.4, pre-commit 4.6.2)
- When to use: working any GitHub issue with the Claude Code agents, checking where an issue stands, or changing `.claude/`.

Every issue runs gates G0 to G7 (see `CLAUDE.md`, "Agents and skills"). The state of each issue lives in `docs/ai/pipeline/<n>.md`, where `<n>` is the GitHub issue number. Claude Code runs the same way from the VS 2026 extension panel and from the terminal; "terminal only" means the step has no UI in Visual Studio, so use *View → Terminal* (Developer PowerShell).

## Prerequisites

| Visual Studio 2026 | CLI |
| --- | --- |
| Python 3 on `PATH`: *View → Terminal*, `python --version` | `python --version` |
| GitHub CLI signed in: *View → Terminal*, `gh auth status` | `gh auth status` |
| Git hooks, once per clone: *View → Terminal*, `pre-commit install --hook-type pre-commit --hook-type commit-msg` | `pre-commit install --hook-type pre-commit --hook-type commit-msg` |
| Local .NET tools (dotnet-ef), once per clone: *View → Terminal*, `dotnet tool restore` | `dotnet tool restore` |

## Steps

| # | Visual Studio 2026 | CLI |
| --- | --- | --- |
| 1 | Start or resume an issue: open the Claude panel and send `/issue <n>` **(unverified: first real run pending)** | `claude`, then `/issue <n>` **(unverified: first real run pending)** |
| 2 | Approve the skips Claude proposes for a docs-only, CI or dependency issue by answering in the panel; Claude writes `approved: Marco <date>` into the manifest | Same, in the terminal session |
| 3 | See where an issue stands — terminal only: `python .claude/scripts/gates.py <n>` | `python .claude/scripts/gates.py <n>` |
| 4 | See the next gate — terminal only: `python .claude/scripts/gates.py <n> --next` | `python .claude/scripts/gates.py <n> --next` |
| 5 | Check a skill's prerequisites before using it — terminal only: `python .claude/scripts/prereqs.py module-scaffold --phase tenancy` | `python .claude/scripts/prereqs.py module-scaffold --phase tenancy` |
| 6 | After editing `.claude/` or `CLAUDE.md` — terminal only: `python .claude/scripts/lint.py` (also runs on commit through pre-commit) | `python .claude/scripts/lint.py` or `pre-commit run claude-lint --all-files` |
| 7 | Commit — *Git Changes* window; the hooks run gitleaks, commit-message and claude-lint checks | `git commit`; same hooks |
| 8 | Open the PR with the body from the manifest's "G7 PR body draft" (contains `Closes #<n>`) — *Git Changes → Create a Pull Request* | `git push -u origin issue/<n>-<slug>` then `gh pr create --title "<title>" --body-file <file>` |

## What the hooks do

- An agent that writes outside its paths in `.claude/boundaries.json` is refused, with the reason shown to the agent. The main session is not restricted.
- Any shell command that would read a `.env` file (except `.env.example`), a user-secrets file or `dotnet user-secrets list` is refused for every session.

## Verify

- `python .claude/scripts/lint.py` prints `claude-lint: OK`.
- `python .claude/scripts/gates.py <n>` prints `all gates passed` before you open the PR; CI's `claude-config` job runs the same check on `issue/<n>-*` branches.

## Rollback

- A hook blocks legitimate work: remove its entry under `hooks` in `.claude/settings.json` (both hooks fail open on their own errors, so this is only needed for a wrong rule) and open an issue.
- A wrong gate verdict: the owner agent edits its artifact's verdict line; never edit it by hand to make a gate pass.
