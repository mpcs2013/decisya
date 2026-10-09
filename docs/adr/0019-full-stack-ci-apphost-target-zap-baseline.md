# 0019. Full-stack CI: the AppHost on the runner as the test target, a per-run dev password, and an unauthenticated ZAP baseline under our own policy

- Status: Accepted (Marco, 2026-10-09)
- Date: 2026-10-09
- Deciders: Marco
- Tags: security, tooling, hosting

## Context and problem statement

Issue #123 brings three suites into CI that have only run on Marco's host:
- the `Category=AppHost` tests (#15 F-5);
- the Playwright E2E suite with axe (#26, #121, #122);
- a dynamic application security test (DAST) of the BFF and, through it, the Api.

All three need the whole application running. Two runnable shapes exist:
- the AppHost in run mode (Aspire, DCP, dev realm with seeded dev users, dev certificate);
- the deployable Compose stack (#120, ADR-0018: production realm without users, Caddy edge with LAN allow-lists, release images, file secrets, admin MFA).

The scanner and the way E2E gets its password are new structural choices. ADR-0014 (pinning) and ADR-0015 (image CVE scan) say how CI tool images are pinned and which images are scanned, but not whether a DAST tool image is a scan target.

Forces:
- **Security:** no long-lived credential that guards nothing; no token in a public log or artifact; no scanner with a token or the Docker socket.
- **Reliability:** a required check must always report, and must not depend on a secret that fork and Dependabot PRs do not receive.
- **Fidelity:** the CI target should be close enough to production that the scan means something.
- **Cost and time:** the repository is public, so GitHub-hosted runner minutes are free; wall-clock time to a green PR is the real cost for a solo developer.

## Decision drivers

- The same running stack serves E2E and DAST, so there is only one way to start it.
- Every value CI needs is generated on the runner, or is not needed at all.
- The pass/fail policy for DAST is our own code: offline-testable, fail-closed, with expiring exceptions. This is the ADR-0015 pattern.

## Considered options

Target environment:
1. **The AppHost in run mode on the runner** (chosen). The dev realm seeds `dev-alice` and `dev-admin` from one generated password, which is exactly what the E2E suite was written against. It needs the Aspire CLI bundle (DCP and the dashboard, `AspireUseCliBundle=true`) and a trusted dev certificate on Linux.
2. **The Compose stack.** Rejected for this issue:
   - it needs the three release images, which exist only after a release (`stack_smoke.py` has to build or `docker load` them by hand);
   - it needs a Caddy edge whose host names and LAN allow-lists assume the NAS;
   - its production realm has no users, so E2E would have to create synthetic users through the Keycloak admin API at test time, and admin MFA blocks `dev-admin`'s flows.

   It is the better target for a production-shaped ZAP scan later, run by hand next to `stack_smoke.py` (#83).

E2E password:
1. **Generated per run in the job, masked, never written to `$GITHUB_ENV` or `$GITHUB_OUTPUT`** (chosen). This is the precedent of the integration step's `DECISYA_DEV_USER_PASSWORD`. The value guards a realm that lives only as long as the job.
2. **A GitHub Actions secret, optionally in a protected environment.** Rejected:
   - Fork PRs and Dependabot PRs do not receive Actions secrets, so a required `e2e` check would fail on every Dependabot PR.
   - A protected environment with reviewers would hold every PR for an approval.
   - The stored value would be a long-lived credential that protects nothing beyond one job, and it would need rotation.

DAST:
1. **OWASP ZAP baseline (passive scan, traditional and AJAX spider), unauthenticated, run from its digest-pinned image with `docker run`, with the verdict from our own stdlib policy script** (chosen).
2. **ZAP full scan (active).** Rejected for every PR: it runs for tens of minutes, it is noisy against a dev-mode host, and active attacks on a throwaway stack that has no data add little over passive checks at this stage.
3. **ZAP API scan.** Not possible yet: no OpenAPI document exists. It becomes the next step when the first `Modules.<Name>.Contracts` OpenAPI file lands (#83).
4. **`zaproxy/action-baseline`.** Rejected: a third-party action that runs with the job token and pulls its image by tag, which is the threat ADR-0014 and ADR-0015 already rejected for Trivy.
5. **Nuclei or another template scanner.** Rejected: template-driven CVE probing of known products, not a web-application header, cookie and content check; no clear gain over ZAP for a BFF.

## Decision outcome

Chosen: **the AppHost on the runner, a per-run password, and the unauthenticated ZAP baseline under our own policy**, because together they bring all three suites into CI with no stored secret and no new action.

1. **Target.** E2E and ZAP start the built AppHost DLL in run mode on `ubuntu-latest`, in ephemeral-container mode with a generated `decisya-apphosttests-<32 hex>` volume name. The DLL runs directly, never through `dotnet run` (#120). The Aspire CLI is installed at the exact `Aspire.AppHost.Sdk` version of `Decisya.AppHost.csproj`, from nuget.org, never through an install script piped to a shell (S-120-06). The BFF is reached only on `https://localhost:7200`.
2. **Password.** One harness process (`.github/scripts/fullstack.py`) does all of these: generates the dev password, masks it, passes it to the AppHost (`Parameters__dev-user-password`) and to Playwright (`E2E_DEV_PASSWORD`), stops the stack, and scans the results. The value never leaves that process tree. The `apphost-tests` job generates its own value in its single test step.
3. **Scanner pin.** `ghcr.io/zaproxy/zaproxy:<version>@sha256:<64 hex>` lives in a never-built `.github/zap/Dockerfile`, with a Dependabot `docker` entry and a 7-day cooldown (ADR-0014 item 3).
   - The container gets `--network host`, which is needed because Kestrel and DCP bind loopback only.
   - Its only mount is a fresh work directory under `RUNNER_TEMP`. It never sees the repository, the Docker socket, a token or a secret.
4. **Not an ADR-0015 target.** Like the Grype image, the ZAP image is a CI tool:
   - it is never deployed and never reaches the product;
   - it runs only on an ephemeral runner against a throwaway stack;
   - it is not in `ContainerImages.cs`.

   ADR-0015's targets stay the product images. This clarifies ADR-0015 and does not change it.
5. **Policy.** `.github/scripts/zap_policy.py` reads ZAP's JSON report:
   - **Fail:** any alert of risk Medium or High, at any confidence except False Positive, unless an unexpired entry in `.github/zap/exceptions.json` covers it.
   - **Exception schema:** `{pluginId, path, justification, issue, added, expires}`. `expires` is at most 90 days after `added`. The schema and the expiry follow ADR-0015 item 4.
   - **Low and Informational alerts** are listed in the job summary and never fail the job.
   - **Fail-closed:** the run fails when the report is missing or unparsable, when it names a site other than `https://localhost:7200`, when it shows fewer than the minimum set of scanned URLs (`/`, `/bff/me`), or when ZAP exits 3.
   - **No rule suppression in ZAP itself:** ZAP's `rules.tsv` is not used to IGNORE rules, because it has no expiry and no justification.
6. **Unauthenticated, for now.** The scan covers the surface an anonymous client reaches:
   - the shell and its assets, with their headers;
   - `/bff/me`, `/bff/login` and its redirect;
   - `/bff/logout`;
   - `/api/*`, which answers 401 through the BFF.

   An authenticated scan is deferred (#83). The intended design proxies the E2E browsers through a ZAP daemon. The open risk is the Keycloak form POST that ZAP would then record. ZAP is never given a session cookie on its command line.

### Marco's decisions (2026-10-09)

The choices that the architecture note (`docs/architecture/full-stack-ci.md`) left open for Marco are decided:
- **M1, password:** `E2E_DEV_PASSWORD` is generated per run (decision 2), not stored as a GitHub Actions secret.
- **M2, required checks:** all three jobs, `apphost-tests`, `e2e` and `zap`, are required. There is no advisory burn-in for `zap`.
- **M3, new tool and image:** the Aspire CLI (tool-path install, decision 1) and the OWASP ZAP stable image (decision 3) are approved. The exact pins are resolved at G4, past the 7-day cooldown, and shown to Marco before they are committed.
- **M4:** this ADR is accepted.

### Consequences

- Good:
  - #15 F-5, #26's CI run and the DAST item close with no stored secret, so fork and Dependabot PRs run the same jobs.
  - One harness starts the stack for both E2E and ZAP, and the password exists only in one process tree.
  - The DAST verdict is our code: unit-tested, fail-closed, with exceptions that expire.
- Bad:
  - **The CI target is dev-mode.** HSTS, Caddy's edge headers, the production realm and admin MFA are not scanned. Low alerts such as a missing HSTS header are expected and only reported. A production-shaped scan against the Compose stack stays a host-run step (#83).
  - The Aspire CLI is a new CI tool, and on Linux the dev certificate must be trusted (`dotnet dev-certs https --trust` plus `SSL_CERT_DIR`). Both are unproven on `ubuntu-latest` until the G4 spike.
  - Playwright's browser builds and their OS dependencies (`--with-deps`, apt) are downloaded at run time, pinned by the Playwright version in `package-lock.json` only, with no checksum. This is the same class as the gitleaks binary residual in ADR-0014.
  - The ZAP image is large (about 1.5 GB) and is not CVE-scanned, by decision 4.
  - About 30 more runner-minutes per code PR. That is free while the repository is public; if it ever goes private, revisit the job split.
- Enforced by (G4):
  - `.claude/tests/test_ci_fullstack.py` checks the three jobs:
    - permissions are `contents: read` only;
    - no job-level `if`, `environment:` or `secrets.` use;
    - `needs` is only `changes`;
    - `timeout-minutes` is set;
    - the ZAP image is referenced by digest, has no socket mount and no `-e`;
    - the password is never written to `$GITHUB_ENV` or `$GITHUB_OUTPUT`;
    - no `dotnet run`.
  - `.claude/tests/test_zap_policy.py` checks the policy, the exception schema and expiry, and the fail-closed cases.
  - `.claude/tests/test_ci_pins.py` is extended to `.github/zap/Dockerfile`.
  - `.claude/tests/test_ruleset.py`: `apphost-tests`, `e2e` and `zap` join the floor set, since all three are required (M2).
  - `AppHostCategoryTraitGuardTests` is unchanged.
