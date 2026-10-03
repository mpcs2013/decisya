# 0014. Pin every CI and hook dependency by commit SHA or digest

- Status: Accepted (Marco, 2026-10-03)
- Date: 2026-10-03
- Deciders: Marco
- Tags: security, tooling

## Context and problem statement

`ci.yml` references every GitHub Action by a mutable tag (`actions/checkout@v4`, `gitleaks/gitleaks-action@v3`, `github/codeql-action/init@v4`, and so on), and `.pre-commit-config.yaml` pins the gitleaks hook by tag (`rev: v8.30.1`). Anyone who controls the upstream repository can move a tag to new code. That code then runs in CI with the job's `GITHUB_TOKEN`, or on Marco's host at the next commit. This is not hypothetical. Hijacked action tags have hit widely used actions more than once (the `tj-actions/changed-files` incident in 2025 is the best-known case), and a hijacked tag is invisible in this repository's diff.

The finding is open as #35 T-16 and system baseline C-08 (Medium). The `codeql` job's own comment says "pin every action to a commit SHA before Phase 0 exit".

Forces:
- **Security:** a pin must be immutable. A tag is not, and a short SHA is ambiguous.
- **Solo-developer time:** updates must keep arriving as reviewable PRs without hand work. Dependabot already runs the `github-actions`, `pre-commit` and `docker` ecosystems here.
- **Detectability:** a later hand edit, or an agent edit, must not quietly reintroduce a tag.

## Decision drivers

- Immutability of whatever CI and the hooks execute.
- Updates stay automatic and reviewable (Dependabot plus Marco's review).
- An offline guard in the existing `.claude` test suite, which runs in CI (`claude-config`, a required check) and in the pre-push hook.

## Considered options

1. **Keep tags; rely on Dependabot and review.** Rejected: a moved tag never produces a diff, so there is nothing to review.
2. **Pin full 40-character commit SHAs with a version comment; Dependabot keeps them current; an offline test fails on any unpinned reference** (chosen).
3. **Vendor or fork every action into the account.** Rejected: it brings a maintenance burden for one developer, and updates become manual.
4. **Add a runtime egress-hardening action (for example `step-security/harden-runner`).** Rejected for now: it is one more third-party action with broad runner access, and it does not remove the tag problem. It can be revisited after Phase 0 (#83).

## Decision outcome

Chosen option: **2**, because it makes the executed code immutable at zero ongoing manual cost, and the guard keeps it that way.

1. **Actions.** Every `uses:` in `.github/workflows/*.y{a,}ml` has the form `<owner>/<repo>[/<path>]@<40 lowercase hex>  # v<major>.<minor>.<patch>`. Local actions (`./…`) are exempt. A `docker://` reference must carry `@sha256:<64 hex>`. The SHA is the **peeled commit** of the tag (`refs/tags/<tag>^{}` for annotated tags), not the tag object. All uses of the same `<owner>/<repo>` carry one SHA and one comment.
2. **pre-commit.** Every remote `repo:` has `rev: <40 lowercase hex>  # frozen: v<x.y.z>`, which is the form `pre-commit autoupdate --freeze` writes. `local` and `meta` repositories are exempt. The gitleaks parity test reads the version from the `frozen:` comment.
3. **Images used by CI tooling** (the image scanner, ADR-0015) are pinned as `<registry>/<image>:<tag>@sha256:<64 hex>` in a Dockerfile-shaped file that is never built, so that Dependabot's `docker` ecosystem bumps it. This follows the `.devcontainer/engine/images.Dockerfile` precedent.
4. **Updates.** Dependabot's existing `github-actions` and `pre-commit` entries bump the SHA and rewrite the version comment. Every non-npm entry gets `cooldown: { default-days: 7 }`, as npm already has, so a freshly hijacked release has a week to be noticed before a PR proposes it. Security updates are not delayed by the cooldown. If Dependabot's `pre-commit` ecosystem does not update `frozen` revisions (G4 checks this on the first run), the fallback is `pre-commit autoupdate --freeze` by Marco at each phase exit, and the parity test still keeps CI and pre-commit on the same gitleaks version.

### Consequences

- Good:
  - What CI and the hooks run can change only through a diff in this repository.
  - Dependabot PRs show the old and new SHA together with the version, so review stays possible.
  - The guard catches regressions in seconds: locally, on pre-push and in CI.
- Bad:
  - **A SHA-pinned Docker container action whose `action.yml` names its image by tag is still mutable at the image layer.** G4 lists every such action. This is a residual for G3, not fixed here.
  - Release binaries downloaded by an action at runtime (the gitleaks binary, fetched by `gitleaks-action` from `GITLEAKS_VERSION`) are pinned by version only, with no checksum. This is deferred to #83.
  - The SHA-to-tag correspondence cannot be checked offline. It is checked once by G4 (`git ls-remote`) and then by Dependabot, which writes both.
  - More Dependabot PRs, each needing a `@dependabot rebase` under the strict ruleset.
- Enforced by:
  - `.claude/tests/test_ci_pins.py` (new; workflows, pre-commit and the scanner pin file), run by `claude-config` and the pre-push check;
  - `.claude/tests/test_gitleaks_parity.py` (reads the `frozen:` comment);
  - `.claude/tests/test_commitlint.py` (already reads the `# vX.Y.Z` comment);
  - Dependabot configuration in `.github/dependabot.yml`.
