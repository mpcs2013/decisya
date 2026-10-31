---
name: bootstrap
description: "Sets up a new project from a Norma release bundle (project layer, install, checks), or adds a pack to a project that already has Norma. Use when the owner wants Norma in a repository that has no Norma lock yet (a new project, or one with older copies of the engines), or wants to add or remove a stack pack."
disable-model-invocation: "true"
---
# bootstrap

The owner starts this skill (`/bootstrap`). It never pushes and never edits a locked file.

## Ask the owner first
1. The release bundle file, its announced SHA-256 and tag. Check the file's SHA-256 against the announcement before
   anything else; stop on a mismatch.
2. The release signer line from the announcement (one line: principal `norma-release`, `namespaces="git"`, the key).
3. The stack packs to install (`dotnet`, `npm`, `python`, or none) and, for each, the values its required keys need (for the
   dotnet pack: `solution`, `unit_test_filters`). Put them in a small JSON file, `{"dotnet": {...}}`, outside the project.
4. Whether the repository has, or will have, rulesets under `.github/rulesets/` or `.github/dependabot.yml`.
5. The interpreter token the hooks should run: `python` or `python3` (the one that is Python 3.12 or newer here).
6. Whether the repository is public or private (GitHub Pro is the baseline; a private repository is set up to avoid
   costs a public one does not have), and whether a dependency bot such as Dependabot should get the narrow gate path
   (its branch prefix and its login, for example `dependabot/` and `dependabot[bot]`; none by default).
7. Who may approve a skipped gate: the name of each approver, as they will write it in a skip's approval (at least
   one; the owner first). They become `gates.approvers` in `project.json`.
8. The id delivery tracking gave this project, `GL-` and four digits; `GL-0000` when the project is not tracked. It
   becomes `usage.project_id` in `project.json`, and every usage capture carries it. Never make one up.

## First install (no Norma lock yet)
The same steps serve a new project and a project that has older copies of the engines but no
`.claude/core.lock.json`.

1. The project must be a git repository (`git init` first if it is new); open the issue branch (`issue/<n>-<slug>`)
   first. A project with older copies first removes
   every older file from `.claude/hooks/` and `.claude/scripts/` (those folders hold only release files; the install
   names any file left there and installs nothing).
2. Check the bundle with system tools only, before any file from it runs: show the owner the first-install check
   from the release notice (the same blocks are in Norma's consumer guide, for bash and for PowerShell) and let the
   owner run it in an empty folder outside the project. It checks the bundle's SHA-256, the tag signature with
   `ssh-keygen -Y verify` against the notice's signer line, the manifest against the signed tag, and `bootstrap.py`,
   `lock.py` and `base-settings.json` against the manifest, and extracts only those checked files into
   `norma-check/`. The block stops at the first failing step; go on only when its last line is
   `first-install check: OK`. Never run a file from an unchecked bundle.
3. From the project root, show the owner this command (with `<tmp>` the folder that holds `norma-check/`) and let the
   owner run it:
   `python -I <tmp>/norma-check/core/.claude/scripts/bootstrap.py --bundle <tmp>/norma-vX.Y.Z.tar --tag vX.Y.Z --packs <packs> --signer-line '<line>' --visibility <public|private> --approver '<name>' [--approver '<name>' ...] --project-id GL-NNNN [--pack-settings <file>] [--rulesets] [--dependabot] [--bot-branch dependabot/ --bot-login 'dependabot[bot]'] [--python python3] [--adopt [--plan]]`
   `--root` names the project folder (default: the current folder). `--python` names the interpreter the hooks run
   (default `python`); pass `python3` where `python` is not 3.12 or newer.
   `<packs>` is comma-separated (`dotnet,npm`); give `--approver` once per approver. Put the signer line in single
   quotes, in bash and in PowerShell: it contains `namespaces="git"`, which double quotes would break. The
   pack-settings file is an input only; keep it outside the project.
4. Read its output. It writes only missing files (`project.json`, `boundaries.json`, `settings.json`, the roster page
   `docs/ai/README.md`, `CLAUDE.md`, the signers file) and keeps every existing one, installs the release with
   `lock.py write` (which checks the signature and every file first), then runs the roster, `lint.py`, `floors.py`
   and `prereqs.py interpreter`. A project that already had a `project.json` keeps it, and `--project-id` is not used:
   before the command, add the `usage` section to it (`docs/project-json.md`), or every check and hook stops on the
   missing key. A project that already had a `settings.json` or `boundaries.json` keeps it as it is,
   so `floors.py` names the base deny entries and hook registrations it lacks, and `lint.py` warns about each missing
   build-logic glob. To add them, run the same command with `--adopt --plan` first and show the owner the additions it
   prints (nothing is written or installed), then, once the owner agrees, without `--plan`. `--adopt` only adds, at
   the end of each list; when a file cannot be merged cleanly (for example an older registration of a core hook) it
   writes nothing and names the place: merge that file by hand as `docs/consumer-guide.md` ("Settings for a project
   that has its own") shows. A project that already has a signers file keeps it too: add the release signer line to it
   (exactly one `norma-release` line); until then the install stops on `signers: exactly one signer line must match
   the release principal`.
   If a check fails, report the exact error, fix the project file it names with the owner, and run the command again;
   existing files are kept.
5. Add `__pycache__/`, `.agent-logs/` (the hooks' audit log and freeze markers) and `/.local.usage/` (the usage
   capture folder, `usage.capture_folder`) to `.gitignore` if they are not there, before the first commit: the hooks
   and scripts write bytecode into the core folders when they run, and committed bytecode fails the lock check; the
   capture writes nothing into a folder git does not ignore.
6. Review the written files with the owner. Starting values to decide:
   - `gates.dependency_paths` and `gates.ci_tooling_paths`;
   - `gates.docs_line_from`: `0` makes every PR need a `Docs:` line; a project with earlier issues sets the next issue
     number;
   - the core agents' lanes in `boundaries.json` are documentation lanes, and G4 is routed to the tests lane until the
     project adds its own implementer agents. With a pack, `deny` holds the pack's build-logic globs when bootstrap
     wrote `boundaries.json` or adopted it with `--adopt` (no listed agent then writes them); a `boundaries.json` kept
     without `--adopt` lacks them until they are added. If a project agent must edit one of those files, narrow the
     lanes instead (for example `tests/**/*.cs`) and remove the glob; `lint.py` then warns about it, without failing.
7. Commit in the project's own issue and PR (branch `issue/<n>-<slug>`). The roster lists what the git index holds, so
   stage first, render again, then commit: `git add -A`, `python .claude/scripts/roster.py`, `git add -A`,
   `python .claude/scripts/lint.py`, then `git commit`. Committed without that, `lint.py` reports a stale roster in a
   fresh clone.

## Add or remove a pack later
1. Change `packs` in `.claude/project.json` (with the pack's required keys), in the project's issue.
2. Run `python .claude/scripts/lock.py write --bundle <the installed release's bundle> --tag <its tag> --packs <the new list>`.
3. Run `python .claude/scripts/roster.py`, `python .claude/scripts/lint.py`, `python .claude/scripts/floors.py` and the
   project's tests, then commit. Bootstrap does not run here, so add a new pack's build-logic globs to `deny` yourself:
   `lint.py` warns about each one that is missing.

## Never
- Never extract a bundle into the project's own `.claude/` by hand, and never copy core files from another project.
- Never edit `.claude/core.lock.json` or any locked file to make a check pass. The one hand edit of
  `core.lock.json` there is, the downgrade marker of a rollback, follows Norma's `docs/runbooks/bump-a-consumer.md`
  and goes through review.
