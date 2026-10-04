"""#119 release supply chain: the release workflow's shape, the pin files, the image checks in
.github/scripts/release_images.py, and the release-please PR exemption in ci.yml's Pipeline gates.

Design: docs/architecture/release-supply-chain.md, ADR-0017; threat delta and MUSTs G4-119-01 to 05:
docs/security/threat-models/release-supply-chain.md. Stdlib only; nothing is built, pushed or signed.
The exemption test runs the real `run:` block of ci.yml with bash in a temporary git repository.
"""
import importlib.util
import inspect
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
RELEASE = ROOT / ".github" / "workflows" / "release.yml"
CI = ROOT / ".github" / "workflows" / "ci.yml"
spec = importlib.util.spec_from_file_location("release_images", ROOT / ".github" / "scripts" / "release_images.py")
ri = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ri)
SHA = "a" * 40


def jobs(text: str) -> dict[str, str]:
    """Top-level job name -> its text (two-space indent under `jobs:`)."""
    body = text.split("\njobs:\n", 1)[1]
    parts = re.split(r"(?m)^  ([A-Za-z0-9_-]+):\s*$", body)
    return {parts[i]: parts[i + 1] for i in range(1, len(parts), 2)}


class ReleaseWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = RELEASE.read_text(encoding="utf-8")
        self.jobs = jobs(self.text)

    def test_triggers_are_an_exact_allow_list(self):
        on = self.text.split("\non:\n", 1)[1].split("\n\n", 1)[0]
        triggers = set(re.findall(r"(?m)^  ([a-z_]+):", on))
        self.assertEqual(triggers, {"push", "workflow_dispatch", "pull_request"})
        for banned in ("workflow_call", "pull_request_target", "workflow_run", "\n  release:"):
            self.assertNotIn(banned, self.text)  # G4-119-03: no reusable-workflow trigger

    def test_permissions_start_empty_and_signing_lives_only_in_publish(self):
        self.assertRegex(self.text, r"(?m)^permissions:\s*\{\}\s*$")
        for job, body in self.jobs.items():
            with self.subTest(job=job):
                writes = set(re.findall(r"(?m)^\s+([a-z-]+):\s*write\s*$", body))
                expected = {"publish": {"packages", "id-token"}, "release-please": {"contents", "pull-requests"},
                            "release-assets": {"contents"}}.get(job, set())
                self.assertEqual(writes, expected)

    def test_build_checks_out_github_sha_never_an_action_output(self):
        build = self.jobs["build"]
        refs = re.findall(r"(?m)^\s+ref:\s*(.+)$", build)
        self.assertTrue(refs)
        self.assertTrue(all(r.strip() == "${{ github.sha }}" for r in refs), refs)  # G4-119-01
        # No ref anywhere in the workflow comes from an action output (G6 F-01e).
        self.assertFalse(re.findall(r"(?m)^\s+ref:\s*\$\{\{\s*(needs|steps)\.", self.text))

    def test_no_caches_and_exact_artifact_download(self):
        self.assertNotIn("actions/cache", self.text)
        self.assertNotRegex(self.text, r"(?m)^\s+cache:")  # G4-119-04
        for m in re.finditer(r"actions/download-artifact@\S+.*\n((?:\s{8,}.*\n)+)", self.text):
            keys = set(re.findall(r"(?m)^\s+([a-z-]+):", m.group(1)))
            self.assertLessEqual(keys, {"with", "name", "path"}, keys)

    def test_every_checkout_drops_credentials(self):
        for m in re.finditer(r"actions/checkout@\S+.*\n((?:\s{8,}.*\n)+)", self.text):
            self.assertIn("persist-credentials: false", m.group(1))

    def test_publish_runs_no_package_code(self):
        publish = "\n".join(l for l in self.jobs["publish"].splitlines() if not l.strip().startswith("#"))
        for banned in ("setup-dotnet", "setup-node", "npm ", "dotnet "):
            self.assertNotIn(banned, publish)

    def test_no_expression_reaches_run_directly(self):
        for block in re.findall(r"(?m)^\s+run: \|\n((?:\s{10,}.*\n)+)", self.text):
            self.assertNotIn("${{", block)
        for line in re.findall(r"(?m)^\s+run: (?!\|)(.+)$", self.text):  # single-line run: too (G6 F-01e)
            self.assertNotIn("${{", line)

    def test_build_exports_and_publish_consumes_the_scanned_digests(self):
        # G4-119-04 (G6 F-01c): the handoff is checked against what build scanned.
        build, publish = self.jobs["build"], self.jobs["publish"]
        for name in ("api", "bff", "migrator"):
            for kind in ("config", "sha256"):
                with self.subTest(name=name, kind=kind):
                    self.assertRegex(build, rf"(?m)^\s+{name}_{kind}:\s*\$\{{\{{\s*steps\.inspect\.outputs\.{name}_{kind}\s*\}}\}}")
                    self.assertRegex(publish, rf"(?m)^\s+{name.upper()}_{kind.upper()}:\s*\$\{{\{{\s*needs\.build\.outputs\.{name}_{kind}\s*\}}\}}")


class PinFileTests(unittest.TestCase):
    def test_base_and_tool_pins_parse_and_are_real(self):
        bases = ri.parse_pins(ri.BASE_PINS, ri.BASE_ALIASES)
        tools = ri.parse_pins(ri.TOOL_PINS, ri.TOOL_ALIASES)
        for ref in bases.values():
            self.assertRegex(ref, r"^mcr\.microsoft\.com/dotnet/(aspnet|runtime):10\.0-noble-chiseled-extra@sha256:[0-9a-f]{64}$")
        self.assertTrue(all("v0.0.0" not in r for r in tools.values()))

    def test_gitleaks_tool_matches_ci_and_precommit(self):
        sys.path.insert(0, str(ROOT / ".claude" / "tests"))
        import test_gitleaks_parity as parity
        tag = ri.parse_pins(ri.TOOL_PINS, ri.TOOL_ALIASES)["gitleaks"].split(":")[1].split("@")[0]
        self.assertEqual(tag.lstrip("v"), parity.ci_version())

    def test_placeholders_and_bad_lines_fail_closed(self):
        cases = {
            "zero digest": "FROM ghcr.io/a/b:v1@sha256:" + "0" * 64 + " AS syft\n",
            "no digest": "FROM ghcr.io/a/b:v1 AS syft\n",
            "unknown alias": "FROM ghcr.io/a/b:v1@sha256:" + "1" * 64 + " AS other\n",
            "extra line": "RUN echo x\n",
        }
        with tempfile.TemporaryDirectory() as d:
            for label, text in cases.items():
                with self.subTest(case=label):
                    p = Path(d) / "pins.Dockerfile"
                    p.write_text(text, encoding="utf-8")
                    with self.assertRaises(ri.ReleaseError):
                        ri.parse_pins(p, ("syft",))

    def test_buildkit_secrets_rule_for_every_dockerfile(self):
        bad = re.compile(r"(?im)^\s*(ARG|ENV)\s+\S*(secret|token|passw|pwd|key|credential)")
        env_copy = re.compile(r"(?im)^\s*(COPY|ADD)\b.*(\.e" + r"nv|secrets\.json)")
        for path in ROOT.rglob("*Dockerfile*"):
            if any(part in path.parts for part in ("node_modules", ".git")):
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            with self.subTest(file=str(path.relative_to(ROOT))):
                self.assertIsNone(bad.search(text))
                self.assertIsNone(env_copy.search(text))


class ImageCheckTests(unittest.TestCase):
    def layer(self, members):
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w") as tar:
            for info, data in members:
                tar.addfile(info, io.BytesIO(data) if data is not None else None)
        return buf.getvalue()

    def test_absolute_symlink_is_skipped_but_a_bad_regular_file_fails(self):
        link = tarfile.TarInfo("usr/lib/ssl/certs")
        link.type, link.linkname = tarfile.SYMTYPE, "/etc/ssl/certs"
        ok = tarfile.TarInfo("app/a.txt")
        ok.size = 1
        with tempfile.TemporaryDirectory() as d:
            names, skipped = ri.unpack_layer(self.layer([(link, None), (ok, b"x")]), Path(d) / "l0")
            self.assertEqual(len(skipped), 1)
            self.assertTrue((Path(d) / "l0" / "app" / "a.txt").exists())
            evil = tarfile.TarInfo("../outside.txt")
            evil.size = 1
            with self.assertRaises(ri.ReleaseError):
                ri.unpack_layer(self.layer([(evil, b"x")]), Path(d) / "l1")

    def test_config_policy(self):
        good = {"os": "linux", "architecture": "amd64",
                "config": {"User": "1654", "Env": [f"{k}=x" for k in ri.ENV_ALLOW], "Entrypoint": ["dotnet", "/app/Decisya.Api.dll"]}}
        self.assertEqual(ri.check_config("api", good), [])
        for label, change in (("root", {"User": "0"}), ("env", {"Env": ["ASPNETCORE_ENVIRONMENT=Development"]}),
                              ("entry", {"Entrypoint": ["sh"]})):
            with self.subTest(case=label):
                bad = json.loads(json.dumps(good))
                bad["config"].update(change)
                self.assertTrue(ri.check_config("api", bad))
        self.assertTrue(ri.check_config("api", {**good, "architecture": "arm64"}))

    def test_forbidden_files_are_reported(self):
        self.assertTrue(ri.check_names("api", 3, ["app/appsettings.Development.json"]))
        self.assertTrue(ri.check_names("api", 3, ["app/" + "." + "env"]))
        self.assertEqual(ri.check_names("api", 3, ["app/appsettings.json", "etc/ssl/certs/ca.pem"]), [])

    def test_gitleaks_report_goes_to_stdout(self):
        # The first local inspect run (2026-10-04): "/dev/stdout" wrote nothing in the container, so the
        # canary failed closed. "-" is gitleaks' stdout.
        argv = ri.gitleaks_argv("tool", Path("/w/scan"), Path("/w/cfg"))
        self.assertEqual(argv[argv.index("--report-path") + 1], "-")

    def test_only_the_exact_openssl_ca_link_is_allowed(self):
        self.assertEqual(ri.check_names("api", 0, ["usr/lib/ssl/cert.pem"]), [])
        self.assertTrue(ri.check_names("api", 0, ["usr/lib/ssl/private/server.pem"]))
        self.assertTrue(ri.check_names("api", 0, ["app/client.crt"]))

    def test_exceptions_may_name_the_release_images(self):
        spec2 = importlib.util.spec_from_file_location("image_scan", ROOT / ".github" / "scripts" / "image_scan.py")
        ims = importlib.util.module_from_spec(spec2)
        spec2.loader.exec_module(ims)
        self.assertEqual(set(ims.EXCEPTION_IMAGES) - set(ims.ALIASES), {"api", "bff", "migrator"})
        self.assertEqual(ims.ALIASES, ("postgres", "keycloak", "redis"))  # the scanned set is unchanged

    def test_gitleaks_cannot_be_steered_by_the_scanned_tree(self):
        argv = ri.gitleaks_argv("tool", Path("/w/scan"), Path("/w/cfg"))
        self.assertIn("--ignore-gitleaks-allow", argv)
        cfg = argv[argv.index("--config") + 1]
        ignore = argv[argv.index("--gitleaks-ignore-path") + 1]
        self.assertTrue(cfg.startswith("/cfg/") and ignore.startswith("/cfg/"))  # G4-119-02: outside /scan
        self.assertTrue(any(a.endswith(":/scan:ro") for a in argv))

    def test_verify_identity_is_exact_and_needs_the_sha(self):
        flags = ri.verify_flags(SHA, "push")
        self.assertEqual(flags[flags.index("--certificate-identity") + 1],
                         "https://github.com/mpcs2013/decisya/.github/workflows/release.yml@refs/heads/main")
        self.assertIn("--certificate-github-workflow-ref", flags)
        self.assertFalse(any("regexp" in f for f in flags))
        for sha, trigger in (("", "push"), ("abc", "push"), (SHA, "pull_request")):
            with self.subTest(sha=sha, trigger=trigger), self.assertRaises(ri.ReleaseError):
                ri.verify_flags(sha, trigger)
        script = (ROOT / ".github" / "scripts" / "release_images.py").read_text(encoding="utf-8")
        self.assertNotIn("identity-regexp", script)
        self.assertNotIn("issuer-regexp", script)

    def test_release_context_crosschecks_every_release_please_output(self):
        # G4-119-01 (G6 F-01a): each release-please value must match the pushed commit and the manifest.
        good = {"RELEASE_EVENT": "push", "RELEASE_SHA": SHA, "RP_RELEASE_CREATED": "true", "RP_SHA": SHA,
                "RP_VERSION": "0.1.0", "RP_TAG": "v0.1.0", "GITHUB_REPOSITORY": "mpcs2013/decisya"}
        with tempfile.TemporaryDirectory() as d:
            manifest = Path(d) / "m.json"
            manifest.write_text('{".": "0.1.0"}', encoding="utf-8")
            with mock.patch.object(ri, "RELEASE_MANIFEST", manifest), mock.patch.dict(os.environ, good):
                self.assertEqual(ri.context()["tag"], "0.1.0")
            for label, change in (("sha", {"RP_SHA": "b" * 40}), ("created", {"RP_RELEASE_CREATED": "false"}),
                                  ("version", {"RP_VERSION": "0.2.0", "RP_TAG": "v0.2.0"}),
                                  ("tag", {"RP_TAG": "v9.9.9"}), ("repo", {"GITHUB_REPOSITORY": "fork/decisya"})):
                with self.subTest(case=label), mock.patch.object(ri, "RELEASE_MANIFEST", manifest), \
                        mock.patch.dict(os.environ, {**good, **change}), self.assertRaises(ri.ReleaseError):
                    ri.context()

    def test_canary_runs_before_any_real_scan(self):
        # G4-119-02 (G6 F-01b)
        source = inspect.getsource(ri.cmd_inspect)
        self.assertIn("canary(", source)
        self.assertLess(source.index("canary("), source.index("for name in IMAGES"))

    def test_push_refuses_an_archive_that_differs_from_the_scanned_one(self):
        # G4-119-04 (G6 F-01c): checked before any docker command runs.
        with tempfile.TemporaryDirectory() as d:
            src = Path(d) / "release-in"
            src.mkdir()
            for name in ("api", "bff", "migrator"):
                (src / f"{name}.tar").write_bytes(b"swapped")
            env = {"RUNNER_TEMP": d, "RELEASE_EVENT": "push", "RELEASE_SHA": SHA, "RELEASE_TAG": "0.1.0",
                   "GITHUB_REPOSITORY": "mpcs2013/decisya", "API_SHA256": "c" * 64, "API_CONFIG": "sha256:" + "d" * 64}
            with mock.patch.dict(os.environ, env), mock.patch.object(ri, "run_checked") as docker:
                with self.assertRaisesRegex(ri.ReleaseError, "SHA-256 differs"):
                    ri.cmd_push(None)
                docker.assert_not_called()

    def test_a_verified_negative_case_is_never_a_pass(self):
        self.assertEqual(ri.classify_negative(0, ""), "verified")
        self.assertEqual(ri.classify_negative(1, "connection refused"), "other")


def bash() -> str | None:
    """Git Bash on Windows (System32's bash.exe is the WSL launcher), plain bash elsewhere."""
    if os.name == "nt" and shutil.which("git"):
        for parent in Path(shutil.which("git")).resolve().parents:
            if (parent / "bin" / "bash.exe").exists():
                return str(parent / "bin" / "bash.exe")
        return None
    return shutil.which("bash")


def pipeline_gates_script() -> str:
    lines = CI.read_text(encoding="utf-8").splitlines()
    start = next(i for i, l in enumerate(lines) if l.strip() == "- name: Pipeline gates")
    run = next(i for i in range(start, len(lines)) if lines[i].strip() == "run: |")
    indent = len(lines[run + 1]) - len(lines[run + 1].lstrip())
    body = []
    for line in lines[run + 1:]:
        if line.strip() and len(line) - len(line.lstrip()) < indent:
            break
        body.append(line[indent:])
    return "\n".join(body) + "\n"


@unittest.skipUnless(bash() and shutil.which("git"), "needs bash and git")
class ReleasePrExemptionTests(unittest.TestCase):
    """G4-119-05: the real ci.yml block, run in a temporary repository."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self.tmp.name) / "repo"
        self.repo.mkdir()
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@example.invalid")
        self.git("config", "user.name", "t")
        for name in ("CHANGELOG.md", "version.txt", ".release-please-manifest.json", "README.md"):
            (self.repo / name).write_text("0\n", encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-qm", "base")
        self.base = self.git("rev-parse", "HEAD").strip()
        self.script = Path(self.tmp.name) / "gates.sh"  # outside the repository, so never committed
        self.script.write_text(pipeline_gates_script(), encoding="utf-8", newline="\n")  # LF, also on Windows

    def tearDown(self):
        self.tmp.cleanup()

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.repo, capture_output=True, text=True, check=True).stdout

    def commit(self, change):
        change(self.repo)
        self.git("add", "-A")
        self.git("commit", "-qm", "x", "--allow-empty")
        return self.git("rev-parse", "HEAD").strip()

    def gates(self, head, author="github-actions[bot]", ref="release-please--branches--main"):
        env = {**os.environ, "HEAD_REF": ref, "PR_AUTHOR": author, "ACTOR": "mpcs2013",
               "BASE_SHA": self.base, "HEAD_SHA": head}
        proc = subprocess.run([bash(), str(self.script)], cwd=self.repo, env=env, capture_output=True, text=True)
        return proc.returncode, proc.stdout + proc.stderr

    def test_only_the_three_files_are_exempt(self):
        def release(repo):
            for name in ("CHANGELOG.md", "version.txt", ".release-please-manifest.json"):
                (repo / name).write_text("1\n", encoding="utf-8")
        code, out = self.gates(self.commit(release))
        self.assertEqual(code, 0, out)

    def test_red_cases_fall_through_and_fail(self):
        def extra(repo):
            (repo / "CHANGELOG.md").write_text("1\n", encoding="utf-8")
            (repo / "README.md").write_text("1\n", encoding="utf-8")

        def delete(repo):
            (repo / "CHANGELOG.md").unlink()

        def rename(repo):
            (repo / "version.txt").unlink()
            (repo / "README.md").rename(repo / "version.txt")

        def symlink(repo):
            (repo / "version.txt").unlink()
            subprocess.run(["git", "update-index", "--add", "--cacheinfo",
                            "120000," + subprocess.run(["git", "hash-object", "-w", "--stdin"], cwd=repo, input="x",
                                                      capture_output=True, text=True, check=True).stdout.strip()
                            + ",version.txt"], cwd=repo, check=True)

        cases = {"extra file": extra, "deletion": delete, "rename into an allowed name": rename,
                 "empty diff": lambda repo: None}
        for label, change in cases.items():
            with self.subTest(case=label):
                self.git("reset", "-q", "--hard", self.base)
                code, out = self.gates(self.commit(change))
                self.assertNotEqual(code, 0, out)
        self.git("reset", "-q", "--hard", self.base)
        subprocess.run(["git", "rm", "-q", "--cached", "version.txt"], cwd=self.repo, check=True)
        subprocess.run(["git", "update-index", "--add", "--cacheinfo", f"160000,{self.base},version.txt"],
                       cwd=self.repo, check=True)  # a gitlink (G6 F-01d)
        self.git("commit", "-qm", "gitlink")
        code, out = self.gates(self.git("rev-parse", "HEAD").strip())
        self.assertNotEqual(code, 0, out)
        self.git("reset", "-q", "--hard", self.base)
        symlink(self.repo)
        self.git("commit", "-qm", "link")
        code, out = self.gates(self.git("rev-parse", "HEAD").strip())
        self.assertNotEqual(code, 0, out)

    def test_other_author_or_branch_is_not_exempt(self):
        def release(repo):
            (repo / "CHANGELOG.md").write_text("1\n", encoding="utf-8")
        head = self.commit(release)
        for author, ref in (("mpcs2013", "release-please--branches--main"), ("github-actions[bot]", "release-please--x")):
            with self.subTest(author=author, ref=ref):
                code, out = self.gates(head, author, ref)
                self.assertNotEqual(code, 0, out)

    def test_dependabot_condition_is_unchanged(self):
        script = pipeline_gates_script()
        self.assertIn("if [ \"$PR_AUTHOR\" = 'dependabot[bot]' ] && [ \"$ACTOR\" = 'dependabot[bot]' ] && [ \"$dependabot_head\" = true ]; then", script)


if __name__ == "__main__":
    unittest.main()
