"""Central Package Management and NuGet audit policy stays in place (#59, G4-59-13 to 16).

The enforcement itself is Directory.Build.targets (it fails the build); this test fails CI and the
pre-push check if someone weakens or removes it. Behaviour was proven with probe projects (manifest,
G4 evidence).
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def prop(path: str, name: str) -> str | None:
    m = re.search(rf"<{name}>\s*([^<]*?)\s*</{name}>", (ROOT / path).read_text(encoding="utf-8"))
    return m.group(1) if m else None


class BuildPolicyTests(unittest.TestCase):
    def test_central_package_defaults(self):
        self.assertEqual(prop("Directory.Packages.props", "ManagePackageVersionsCentrally"), "true")
        self.assertEqual(prop("Directory.Packages.props", "CentralPackageTransitivePinningEnabled"), "true")
        self.assertEqual(prop("Directory.Packages.props", "CentralPackageVersionOverrideEnabled"), "false")

    def test_audit_is_explicit(self):
        self.assertEqual(prop("Directory.Build.props", "NuGetAudit"), "true")
        self.assertEqual(prop("Directory.Build.props", "NuGetAuditMode"), "all")
        self.assertIsNone(prop("Directory.Build.props", "NuGetAuditLevel"), "raising the audit level needs a note to Marco")

    def test_policy_target_checks_every_bypass(self):
        text = (ROOT / "Directory.Build.targets").read_text(encoding="utf-8")
        self.assertIn('BeforeTargets="CollectPackageReferences;CollectPackageDownloads"', text)
        for code, needle in (("DECISYA0001", "'$(ManagePackageVersionsCentrally)' != 'true'"),
                             ("DECISYA0002", "'$(CentralPackageVersionOverrideEnabled)' != 'false'"),
                             ("DECISYA0003", "'$(CentralPackageTransitivePinningEnabled)' != 'true'"),
                             ("DECISYA0004", "'@(PackageDownload)' != ''"),
                             ("DECISYA0005", "'$(NuGetAudit)' != 'true' or '$(NuGetAuditMode)' != 'all'")):
            with self.subTest(code=code):
                self.assertRegex(text, re.escape(needle) + r'"\s*\n\s*Code="' + code + '"')

    def test_no_project_file_disables_the_build_targets_import(self):
        for path in ROOT.rglob("*.*proj"):
            if "node_modules" in path.parts:
                continue
            with self.subTest(project=path.relative_to(ROOT).as_posix()):
                self.assertNotIn("ImportDirectoryBuildTargets", path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
