"""The applets of the self-built Caddy image work in the pinned image (issue #225, ADR-0021 "Enforced by",
G2 note section 4; deferred from PR-1 because it needs the published image).

Every test runs the **pinned** image (`load_scan_refs(ROOT)["caddy"]`, the digest from ContainerImages.cs)
with `--network none --read-only --cap-drop ALL --security-opt no-new-privileges` and a tmpfs where Caddy
needs a writable folder. Nothing mounts a bind path, a secret or the Docker socket. The key-ring tests use a
scratch volume of their own: the stackctl argv shapes are used, with the volume source replaced, so the
stack's real key-ring volume is never touched.

  1. `nc -z` succeeds against a `caddy respond` listener on the loopback address and fails on a closed port
     (the Compose healthcheck of `caddy`).
  2. The three key-ring scripts (`keyring_prepare_argv`, `keyring_reset_argv`, `keyring_verify_argv`) run on
     a scratch volume and print their counts.
  3. BusyBox `wget --no-check-certificate` completes a TLS handshake with a `tls internal` site served by the
     same image (the smoke client; BusyBox's own TLS, no OpenSSL in the image).

The image has no `sleep` applet, so every wait is a bounded `nc -z` retry loop.

Unlike the other Docker tests this file fails, not skips, when Docker is missing on Linux (the `deploy-guards`
job), so the job cannot go green without proving the published image. On another host without Docker it skips.
"""
from __future__ import annotations

import subprocess
import sys
import unittest
import uuid

from deploy_common import REQUIRE_DOCKER, ROOT, DockerCase, docker_available, guards, stackctl

HARDENING = ["--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges"]
RESOURCES = ["--pids-limit", "256", "--memory", "256m"]
CADDY_TMPFS = ["--tmpfs", "/tmp", "-e", "XDG_DATA_HOME=/tmp/data", "-e", "XDG_CONFIG_HOME=/tmp/config", "-e", "HOME=/tmp"]

# A bounded retry with no sleep applet: nc -z returns at once on a closed port, so the loop is a short wait.
WAIT_LISTEN = """
i=0
until nc -z 127.0.0.1 {port}; do
  i=$((i + 1))
  if [ "$i" -ge 30000 ]; then echo "listen=timeout"; exit 1; fi
done
"""

NC_SCRIPT = """set -u
/usr/bin/caddy respond --listen 127.0.0.1:8080 --body ok > /dev/null 2>&1 &
""" + WAIT_LISTEN.format(port=8080) + """echo "open=ok"
if nc -z 127.0.0.1 8081; then echo "closed=unexpected-success"; else echo "closed=refused"; fi
"""

WGET_SCRIPT = """set -u
cat > /tmp/Caddyfile <<'EOF'
{
	admin off
	skip_install_trust
}
https://localhost:8443 {
	tls internal
	respond "tls-ok"
}
EOF
/usr/bin/caddy run --config /tmp/Caddyfile --adapter caddyfile > /tmp/caddy.log 2>&1 &
""" + WAIT_LISTEN.format(port=8443) + """n=0
until body=$(wget --no-check-certificate -q -T 10 -O - https://localhost:8443/); do
  n=$((n + 1))
  if [ "$n" -ge 200 ]; then echo "wget=failed"; cat /tmp/caddy.log; exit 1; fi
done
echo "wget=$body"
"""

# Key files the strict rule of stackguards.keyring_file_problem decides on.
WRAPPED_KEY = ('<key id="1"><encryptedSecret decryptorType="Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor, '
               'Microsoft.AspNetCore.DataProtection"><EncryptedData Type="x"/></encryptedSecret></key>')
REVOCATION = '<revocation version="1"><revocationDate>2026-10-31T00:00:00Z</revocationDate></revocation>'
PLAINTEXT_KEY = '<key id="2"><masterKey>not-a-real-key</masterKey></key>'
SEED_SCRIPT = (
    "set -eu\n"
    "cd /keyring\n"
    "printf '%s' '" + WRAPPED_KEY + "' > good.xml\n"
    "printf '%s' '" + REVOCATION + "' > revocation.xml\n"
    "printf '%s' '" + PLAINTEXT_KEY + "' > bad.xml\n"
    "printf 'note' > note.txt\n"
)


def run_docker(argv: list, timeout: int = 300) -> subprocess.CompletedProcess:
    return subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def pinned_caddy() -> str:
    return guards.load_scan_refs(ROOT)["caddy"]


class ToolsDockerCase(DockerCase):
    """DockerCase that also fails, not skips, when Docker is missing on Linux."""

    @classmethod
    def setUpClass(cls):
        if not docker_available():
            if REQUIRE_DOCKER or sys.platform.startswith("linux"):
                raise RuntimeError("Docker is required here (the pinned Caddy image is under test) but is not available")
            raise unittest.SkipTest("Docker is not available")
        cls.image = pinned_caddy()
        pulled = run_docker(["docker", "pull", cls.image], timeout=900)
        if pulled.returncode != 0:
            raise RuntimeError("could not pull the pinned Caddy image (exit %d): %s" % (pulled.returncode, pulled.stderr[-400:]))


class NcApplet(ToolsDockerCase):
    def test_nc_z_succeeds_against_a_caddy_respond_listener_and_fails_on_a_closed_port(self):
        proc = run_docker(["docker", "run", "--rm", *HARDENING, *RESOURCES, *CADDY_TMPFS, "--entrypoint", "sh", self.image, "-c", NC_SCRIPT])
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        lines = proc.stdout.split()
        self.assertIn("open=ok", lines, proc.stdout + proc.stderr)
        self.assertIn("closed=refused", lines, proc.stdout + proc.stderr)
        self.assertNotIn("closed=unexpected-success", lines)


class WgetTls(ToolsDockerCase):
    def test_busybox_wget_completes_tls_with_a_tls_internal_site_of_the_same_image(self):
        proc = run_docker(["docker", "run", "--rm", *HARDENING, *RESOURCES, *CADDY_TMPFS, "--entrypoint", "sh", self.image, "-c", WGET_SCRIPT])
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertIn("wget=tls-ok", proc.stdout.split("\n"), proc.stdout + proc.stderr)


class KeyringScripts(ToolsDockerCase):
    """The stackctl argv shapes on a scratch volume (never the stack's own key-ring volume)."""

    def setUp(self):
        self.volume = "decisya-tools-test-" + uuid.uuid4().hex[:12]
        self.assertNotEqual(self.volume, guards.KEYRING_VOLUME_NAME)
        created = run_docker(["docker", "volume", "create", self.volume])
        self.assertEqual(created.returncode, 0, created.stderr)
        self.addCleanup(lambda: run_docker(["docker", "volume", "rm", "-f", self.volume]))
        seed = run_docker(["docker", "run", "--rm", *HARDENING, "--pids-limit", "64", "--memory", "64m",
                           "--mount", "type=volume,source=%s,target=%s" % (self.volume, guards.KEYRING_MOUNT_TARGET),
                           "--entrypoint", "sh", self.image, "-c", SEED_SCRIPT])
        self.assertEqual(seed.returncode, 0, seed.stdout + seed.stderr)

    def scratch(self, argv: list) -> list:
        """The stackctl argv with its volume source replaced by this test's scratch volume."""
        replaced = []
        out = []
        for part in argv:
            if part.startswith("type=volume,source=%s," % guards.KEYRING_VOLUME_NAME):
                part = part.replace("source=%s," % guards.KEYRING_VOLUME_NAME, "source=%s," % self.volume, 1)
                replaced.append(part)
            out.append(part)
        self.assertEqual(len(replaced), 1, "exactly one volume mount is replaced")
        self.assertFalse(any(guards.KEYRING_VOLUME_NAME in part for part in out), "the real key-ring volume is not named")
        return out

    def run_tool(self, argv: list) -> subprocess.CompletedProcess:
        return run_docker(self.scratch(argv), timeout=300)

    def report(self) -> dict:
        proc = self.run_tool(stackctl.keyring_verify_argv(self.image))
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        facts, reason = guards.parse_keyring_report(proc.stdout)
        self.assertIsNotNone(facts, "%s: %s" % (reason, proc.stdout + proc.stderr))
        return facts

    def test_prepare_verify_reset_and_reset_all_print_their_counts(self):
        prepared = self.run_tool(stackctl.keyring_prepare_argv(self.image))
        self.assertEqual(prepared.returncode, 0, prepared.stdout + prepared.stderr)
        # good.xml, revocation.xml, bad.xml and note.txt; the volume root only (the image has no mkdir applet).
        self.assertEqual(prepared.stdout.strip(), "prepared files=4 dirs=1")

        facts = self.report()
        self.assertEqual(facts, {"refused_entries": 0, "xml_files": 3, "xml_ok": 2, "xml_failing": 1,
                                 "other_files": 1, "wrong_owner": 0, "wrong_mode": 0})

        reset = self.run_tool(stackctl.keyring_reset_argv(self.image))
        self.assertEqual(reset.returncode, 0, reset.stdout + reset.stderr)
        self.assertEqual(reset.stdout.strip(), "reset kept=2 deleted=1")
        facts = self.report()
        self.assertEqual((facts["xml_files"], facts["xml_ok"], facts["xml_failing"]), (2, 2, 0))

        reset_all = self.run_tool(stackctl.keyring_reset_argv(self.image, all_keys=True))
        self.assertEqual(reset_all.returncode, 0, reset_all.stdout + reset_all.stderr)
        self.assertEqual(reset_all.stdout.strip(), "reset kept=0 deleted=2")
        self.assertEqual(self.report()["xml_files"], 0)


if __name__ == "__main__":
    unittest.main()
