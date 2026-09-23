$m = "Phase 0 – Platform Foundations"
$issues = @(
  "0.01 Repo bootstrap|devops|dotnet build clean with warnings as errors; gitleaks passes",
  "0.02 ADR-001..009 accepted|architect|ADR index links all nine with status Accepted",
  "0.03 Aspire AppHost + ServiceDefaults (OTel, health, JSON logs)|backend-dev|Aspire dashboard shows a trace for a health call",
  "0.04 SharedKernel: TenantId, Money, CurrencyCode, IClock, result types|backend-dev|Money tests pass; banned-symbol test fails on DateTime.Now",
  "0.05 Keycloak realm export + bff client + seeded users|devops|Realm imports on container start; login page reachable",
  "0.06 BFF: cookie auth, OIDC, Redis ticket store, antiforgery, logout|backend-dev|Login sets cookie with correct flags; browser never receives a token",
  "0.07 YARP /api forwarding with server-side token injection + refresh|backend-dev|Expired access token refreshed transparently (integration test)",
  "0.08 API host JWT bearer validation|backend-dev|Wrong aud, expired, alg=none and HS256 tokens rejected",
  "0.09 Modules.Tenancy (tenant, membership, roles, invitations)|backend-dev|Two-tenant isolation test passes",
  "0.10 Global tenant query filter + NetArchTest ITenantScoped rule|backend-dev|Architecture test fails on a deliberate violation, then passes",
  "0.11 Modules.Entitlements (plans, features, overrides, trials)|backend-dev|Free plan denies Phase 3 feature; admin override grants it",
  "0.12 Modules.Audit append-only log|backend-dev|Audit rows cannot be updated or deleted at DB role level",
  "0.13 Modules.Admin API|backend-dev|Non-admin gets 403; every call audited",
  "0.14 React SPA shell with capability manifest|frontend-dev|Playwright login flow in Firefox and Chromium; axe zero violations",
  "0.15 STRIDE threat models + ASVS L2 baseline|security-reviewer|Every High mitigation has an issue or is closed",
  "0.16 CI hardening: pinned SHAs, ZAP, CodeQL decision, dependency gates|devops|Green pipeline; seeded vulnerable package fails the build",
  "0.17 Release: SBOM, cosign, release-please, Compose from aspire publish, Caddy|devops|Tagged build deploys to the VPS from a clean clone",
  "0.18 Backup/restore runbook + drill; admin guide|devops, tech-writer|Restore drill reproduces a seeded tenant on a fresh host"
)
foreach ($i in $issues) {
  $t, $a, $d = $i -split '\|'
  gh issue create --title $t --milestone $m --label phase-0 --body "**Agent:** $a`n`n**Done when:** $d`n`nSee the Phase 0 work package for context."
}