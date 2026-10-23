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

    def test_restore_hardening_errors(self):
        """#164 (ADR-0019 A6, G3 M-1): lock files stay on, NuGet.config is the only source, locked mode holds."""
        text = (ROOT / "Directory.Build.targets").read_text(encoding="utf-8")
        for code, needle in (("DECISYA0007", "'$(RestorePackagesWithLockFile)' != 'true'"),
                             ("DECISYA0008", "'$(RestoreSources)' != '' or '$(RestoreAdditionalProjectSources)' != ''"
                                             " or '$(RestoreFallbackFolders)' != ''"),
                             ("DECISYA0009", "'$(DECISYA_LOCKED_RESTORE)' == 'true' and '$(RestoreLockedMode)' != 'true'")):
            with self.subTest(code=code):
                self.assertRegex(text, re.escape(needle) + r'"\s*\n\s*Code="' + code + '"')

    def test_lock_files_and_locked_mode_switch(self):
        self.assertEqual(prop("Directory.Build.props", "RestorePackagesWithLockFile"), "true")
        # The SDK's implicit library-packs and fallback folders are restore sources outside NuGet.config (DECISYA0008).
        self.assertEqual(prop("Directory.Build.props", "DisableImplicitLibraryPacksFolder"), "true")
        self.assertEqual(prop("Directory.Build.props", "DisableImplicitNuGetFallbackFolder"), "true")
        text = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
        self.assertIn("<RestoreLockedMode Condition=\"'$(DECISYA_LOCKED_RESTORE)' == 'true'\">true</RestoreLockedMode>", text)

    def test_nuget_config_maps_ostium_to_the_feed_only(self):
        text = (ROOT / "NuGet.config").read_text(encoding="utf-8")
        sources = re.search(r"<packageSources>(.*?)</packageSources>", text, re.S).group(1)
        self.assertTrue(sources.strip().startswith("<clear />"), "machine and user sources must be cleared first")
        self.assertEqual(re.findall(r'<add key="([^"]+)" value="([^"]+)"', sources),
                         [("nuget.org", "https://api.nuget.org/v3/index.json"),
                          ("ostium", "https://nuget.pkg.github.com/mpcs2013/index.json")])
        mapping = dict(re.findall(r'<packageSource key="([^"]+)">\s*<package pattern="([^"]+)" />\s*</packageSource>', text))
        self.assertEqual(mapping, {"nuget.org": "*", "ostium": "Ostium.*"})
        self.assertNotIn("packageSourceCredentials", text, "no credential ever lives in the repository")
        self.assertRegex(text, r"<auditSources>\s*<clear />\s*<add key=\"nuget.org\"")

    def test_ostium_pins_are_exactly_0_2_0(self):
        text = (ROOT / "Directory.Packages.props").read_text(encoding="utf-8")
        pins = dict(re.findall(r'<PackageVersion Include="(Ostium\.[^"]+)" Version="([^"]+)" />', text))
        self.assertEqual(pins, {"Ostium.Authentication.Jwt": "0.2.0", "Ostium.Authentication.Jwt.Testing": "0.2.0",
                                "Ostium.Backchannel": "0.2.0"})
        self.assertNotIn("0.1.0", text, "0.1.0 never existed on the feed (O-006)")

    def test_pinned_signer_line_is_byte_exact(self):
        """ADR-0019 A2: one line, LF, no comment; the supplier's check refuses anything else."""
        data = (ROOT / ".github" / "allowed-signers" / "ostium").read_bytes()
        self.assertEqual(data, b'release@ostium namespaces="git,ostium-checksums" ssh-ed25519 '
                               b"AAAAC3NzaC1lZDI1NTE5AAAAILkewFGdwh1FFykHc8gI0B6/hzjHppYG1CUOR20orqgX ostium-release-tags\n")

    def test_no_project_file_disables_the_build_targets_import(self):
        for path in ROOT.rglob("*.*proj"):
            if "node_modules" in path.parts:
                continue
            with self.subTest(project=path.relative_to(ROOT).as_posix()):
                self.assertNotIn("ImportDirectoryBuildTargets", path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
