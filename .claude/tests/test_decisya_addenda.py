"""Decisya's rules from the files the Norma lock replaces stay present (#246, G3 M1, ADR-0023).

Norma's core agents and skills read `.claude/addenda/<name>.md`; rules every session follows sit in
CLAUDE.md. Each entry below is one rule that lived in a Decisya agent or skill before the lock. A
missing phrase means the rule was dropped or reworded away: restore it, or record why it moved.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ADDENDA = ROOT / ".claude" / "addenda"

REQUIRED = {
    "security-reviewer.md": [
        r"Level 2\*\* everywhere",
        r"V6 \(authentication\) and V7 \(session management\) chapters apply Level 3",
        r"file upload, AI feature or auth change",
        r"antiforgery on every non-GET request",
        r"tenant filter is never bypassed",
        r"`\[Sensitive\]` present on every PII field",
        r"dotnet list package --vulnerable --include-transitive` or `npm audit`",
        r"prompt injection, output validation, spend caps, no advice-like output",
        r"browser \| BFF \| API \| data stores \| external providers",
        r"Tampering \(amounts, tenant id\)",
        r"V6 Authentication .*apply L3 requirements",
        r"V7 Session management .*apply L3 requirements",
        r"V5 File handling \| Upload size/type allow-list",
        r"V8 Authorization \| .*tenant filter",
        r"docs/security/asvs-l2\.md",
    ],
    "test-engineer.md": [
        r"AwesomeAssertions",
        r"never FluentAssertions",
        r"Unit\|Architecture\|Integration\|Contract\|E2E\|AppHost",
        r"no mocked DbContext",
        r"two-tenant isolation tests",
        r"expired token, wrong audience, `alg=none`, missing antiforgery header",
        r"Firefox and Chromium; axe reports zero violations",
        r"--minimum-expected-tests",
        r"never a report or output option such as `--report-xunit-trx`",
        r"dotnet test --no-build --list-tests",
    ],
    "architect.md": [
        r"\*\*tenancy\*\*",
        r"Contracts projects and Wolverine messages",
        r"ADR-0005",
        r"`ITenantScoped`",
        r"you write the `\*\.Contracts` projects",
        r"C4 diagrams \(Mermaid\)",
        r"Deciders: Marco",
    ],
    "product-owner.md": [
        r"role in tenant",
        r"entitlement feature key \(`module\.feature`\)",
        r"ISO 4217",
        r"\*informational\* story with the disclaimer criterion",
    ],
    "tech-writer.md": [
        r"admin guide",
        r"Visual Studio 2026 \| CLI",
        r"— \(terminal only\)",
        r"`dotnet --version` and `aspire --version`",
        r"Firefox is the default browser",
    ],
    "issue.md": [
        r"approved: Marco <YYYY-MM-DD>",
        r"Phase 1: #243",
        r"`\.devcontainer/\*\*`",
        r"`Directory\.Build\.props`, `global\.json`, `dotnet-tools\.json`",
        r"`Directory\.Packages\.props` and `package\.json` only",
        r"`dotnet build -warnaserror` then `dotnet test --no-build`",
        r"volume reset",
        r"never write \"close #n\"",
        r"Build logic and dependency manifests belong to the main session, never to a lane agent",
        r"`Directory\.Packages\.props`",
        r"`\.github/allowed-signers/\*\*`",
    ],
    "ef-migration.md": [
        r"`bigint` minor units plus `char\(3\)` currency",
        r"`tenant_id uuid not null` and an index starting with `tenant_id`",
        r"--startup-project src/Decisya\.Infrastructure\.Migrator",
        r"deploy/sql/<schema>/<timestamp>\.sql",
        r"Database\.Migrate\(\)",
    ],
    "otel-instrumentation.md": [
        r"ActivitySource\(\"Decisya\.<Module>\"\)",
        r"messaging\.system=wolverine",
        r"decisya\.tenant_id",
        r"decisya\.<module>\.<noun>",
        r"Never a user id or an amount in a metric attribute",
        r"full prompts to AI models",
        r"SensitiveDataMaskingProcessor",
    ],
    "testcontainers.md": [
        r"test_ci_integration\.py",
        r"read:packages",
        r"DECISYA_LOCKED_RESTORE=true",
        r"BannedApiFixture",
        r"KeycloakRealmFixture\.EnsureStartedAsync",
        r"`Canaries`",
        r"`ContainerImages` \(tag plus sha256\)",
        r"CaddyRootTrustTests",
    ],
}

CLAUDE_MD = [
    r"## Decisya conventions",
    r"`\.claude/addenda/<name>\.md`",
    r"Wolverine messages \(ADR-0005\)",
    r"`tenant_id uuid not null`",
    r"Level 3 for V6 \(authentication\) and V7 \(session management\)",
    r"Every non-GET request carries antiforgery",
    r"AwesomeAssertions, never FluentAssertions",
    r"two-tenant isolation tests",
    r"DECISYA_LOCKED_RESTORE=true",
    r"Never put a user id or an amount in a metric attribute",
    r"full AI prompts",
]


class DecisyaAgentFileTests(unittest.TestCase):
    """Rules on Decisya-owned agent files, from the pre-lock test_agent_containment and test_npm_guard."""

    def test_devops_keeps_the_stop_and_report_rule(self):  # c:184-189
        text = (ROOT / ".claude" / "agents" / "devops.md").read_text(encoding="utf-8")
        rule = next((line for line in text.splitlines() if "(stop-and-report)" in line), "")
        for fragment in ("(stop-and-report)", "stop at once", "Report to your caller", "Do not retry"):
            self.assertIn(fragment, rule)

    def test_frontend_dev_cannot_install_or_fetch_packages(self):  # n:167-175
        tools = next(line for line in (ROOT / ".claude" / "agents" / "frontend-dev.md").read_text(encoding="utf-8")
                     .splitlines() if line.startswith("tools:"))
        for banned in ("npm ci", "npm install", "npm i ", "npx", "pnpm", "yarn"):
            self.assertNotIn(banned, tools)


class AddendaPresenceTests(unittest.TestCase):
    def test_every_addendum_keeps_its_rules(self):
        for name, patterns in REQUIRED.items():
            text = (ADDENDA / name).read_text(encoding="utf-8")
            for pattern in patterns:
                with self.subTest(addendum=name, rule=pattern):
                    self.assertRegex(text, pattern)

    def test_claude_md_keeps_the_conventions(self):
        text = (ROOT / "CLAUDE.md").read_text(encoding="utf-8")
        for pattern in CLAUDE_MD:
            with self.subTest(rule=pattern):
                self.assertRegex(text, pattern)

    def test_no_addendum_drops_a_core_check(self):
        """An addendum may add checks, never drop one (Norma's agents; ADR-0023)."""
        weakening = re.compile(r"(?i)\b(skip|ignore|disable|do not run|need not)\b[^.\n]*\b(gate|check|verdict|deny|review)\b")
        for path in sorted(ADDENDA.glob("*.md")):
            with self.subTest(addendum=path.name):
                self.assertIsNone(weakening.search(path.read_text(encoding="utf-8")))


if __name__ == "__main__":
    unittest.main()
