"""No home-network address in the repository (issue #120, G2 D1 guard a, G3 T120-05).

Scans every tracked and every not-ignored text file for IPv4 and IPv6 literals in the private, CGNAT,
link-local, ULA and global ranges, and fails on any literal that the allow-list does not cover. This
test needs no Docker and runs in every `deploy-guards` run, docs-only pull requests included.

Reported hits name the file and line, never the literal: a real address must not be repeated in a CI
log. Ranges that can never be a home network (documentation ranges, loopback, multicast) are not
candidates, and neither is a block definition such as 10.0.0.0/8 (prefix /16 or shorter).
"""
from __future__ import annotations

import ipaddress
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ALLOW_LIST = ROOT / "deploy" / "compose" / "address-allowlist.txt"

V4 = re.compile(r"(?<![\w.])((?:\d{1,3}\.){3}\d{1,3})(?!\w|\.\d)(?:/(\d{1,2}))?")
V6 = re.compile(r"(?<![\w:.])([0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7})(?![\w:.])(?:/(\d{1,3}))?")

HOME_V4 = [ipaddress.ip_network(n) for n in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16")]
NON_CANDIDATE_V4 = [ipaddress.ip_network(n) for n in (
    "0.0.0.0/8", "127.0.0.0/8", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15",
    "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3")]
GLOBAL_V6 = ipaddress.ip_network("2000::/3")
DOC_V6 = ipaddress.ip_network("2001:db8::/32")
ULA_V6 = ipaddress.ip_network("fc00::/7")
LINK_LOCAL_V6 = ipaddress.ip_network("fe80::/10")

SKIP_DIRS = {".git", "node_modules", "bin", "obj", "artifacts", "__pycache__", ".vs", "dist", ".venv"}
SKIP_NAMES = {"package-lock.json", "pnpm-lock.yaml", "yarn.lock"}
SKIP_SUFFIXES = {".png", ".jpg", ".jpeg", ".gif", ".ico", ".woff", ".woff2", ".ttf", ".eot", ".zip", ".gz", ".pdf",
                 ".snk", ".pfx", ".dll", ".exe", ".pyc", ".lock"}
MAX_BYTES = 5 * 1024 * 1024

# Entries that are in a home range on paper but are fixed, well-known or non-network values. Adding a
# line here, or a line to the allow-list that is not documented below, fails the test below: the
# allow-list must not become the way a real address gets in.
REVIEWED_CANDIDATE_ENTRIES = {
    "10.120.0.0/24", "10.120.1.0/24",   # the committed Docker networks (G2 D2)
    "192.168.65.0/24",                  # Docker Desktop's internal VM subnet, named in the sandbox docs
    "169.254.169.254/32",               # the cloud metadata address, named in the sandbox threat models
    "1.1.1.1/32", "6.6.6.6/32",         # a public resolver and an attacker placeholder in tests and docs
    "2606:4700:4700::1111/128",         # the same public resolver over IPv6, in the sandbox docs
    "6.6.87.2/32", "2.6.1.0/32",        # WSL kernel and package versions that parse as addresses
}


def load_allow_list(path: Path = ALLOW_LIST) -> list:
    entries = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        entries.append(ipaddress.ip_network(line.split()[0], strict=False))
    return entries


def is_candidate(ip) -> bool:
    """Could this address belong to a home or a public network that identifies the owner?"""
    if ip.version == 4:
        if any(ip in net for net in NON_CANDIDATE_V4):
            return False
        return True  # RFC 1918, CGNAT, link-local and every global unicast address
    if ip in DOC_V6:
        return False
    return ip in GLOBAL_V6 or ip in ULA_V6 or ip in LINK_LOCAL_V6


def covered(network, allow: list) -> bool:
    return any(a.version == network.version and network.subnet_of(a) for a in allow)


def scan_text(text: str, allow: list) -> list:
    """(line number, kind) of every candidate literal the allow-list does not cover."""
    hits = []
    for number, line in enumerate(text.splitlines(), 1):
        for pattern, version in ((V4, 4), (V6, 6)):
            for match in pattern.finditer(line):
                try:
                    ip = ipaddress.ip_address(match.group(1))
                except ValueError:
                    continue
                if ip.version != version or not is_candidate(ip):
                    continue
                prefix = match.group(2)
                if prefix is not None:
                    try:
                        network = ipaddress.ip_network("%s/%s" % (ip, prefix), strict=False)
                    except ValueError:
                        continue
                    limit = 16 if version == 4 else 32
                    if int(prefix) <= limit and network.network_address == ip:
                        continue  # a block definition, never one home's network
                else:
                    network = ipaddress.ip_network(ip)
                if not covered(network, allow):
                    hits.append((number, "private or public IPv%d literal outside the allow-list" % version))
    return hits


def repository_files() -> list:
    names = []
    try:
        proc = subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
            cwd=ROOT, capture_output=True, timeout=120)
        if proc.returncode == 0 and proc.stdout:
            names = [n.decode("utf-8", "replace") for n in proc.stdout.split(b"\0") if n]
    except (OSError, subprocess.SubprocessError):
        names = []
    if not names:  # no git, or an exported tree: walk it
        names = [str(p.relative_to(ROOT)) for p in ROOT.rglob("*")
                 if p.is_file() and not (set(p.relative_to(ROOT).parts) & SKIP_DIRS)]
    files = []
    for name in sorted(set(names)):
        path = ROOT / name
        parts = set(Path(name).parts)
        if (parts & SKIP_DIRS) or path.name in SKIP_NAMES or path.suffix.lower() in SKIP_SUFFIXES:
            continue
        if path.is_file() and path.stat().st_size <= MAX_BYTES:
            files.append(path)
    return files


class AddressScanTests(unittest.TestCase):
    def setUp(self):
        self.allow = load_allow_list()

    def test_the_repository_holds_no_address_outside_the_allow_list(self):
        found = []
        for path in repository_files():
            raw = path.read_bytes()
            if b"\0" in raw[:8000]:
                continue
            text = raw.decode("utf-8", "replace")
            for number, kind in scan_text(text, self.allow):
                found.append("%s:%d: %s" % (path.relative_to(ROOT).as_posix(), number, kind))
        self.assertEqual(found, [], "move the value to a documentation range (RFC 5737, RFC 3849) or to ops.local/ (git-ignored)")

    def test_the_allow_list_only_holds_reviewed_candidate_ranges(self):
        for network in self.allow:
            if is_candidate(network.network_address) or is_candidate(network.broadcast_address):
                self.assertIn(str(network), REVIEWED_CANDIDATE_ENTRIES,
                              "an allow-list entry would admit a home or public range; review it, then list it here")

    def test_the_committed_docker_subnets_are_covered(self):
        for cidr in ("10.120.0.0/24", "10.120.1.0/24"):
            self.assertTrue(covered(ipaddress.ip_network(cidr), self.allow))


class ScannerDetectsTests(unittest.TestCase):
    """The scan itself can fail (red cases) and does not cry wolf (green cases)."""

    def setUp(self):
        self.allow = load_allow_list()

    def flagged(self, text):
        return bool(scan_text(text, self.allow))

    # The red-case literals are built from parts: this file is scanned too, and must not hold one.
    @staticmethod
    def v4(*octets):
        return ".".join(str(o) for o in octets)

    @staticmethod
    def v6(*groups):
        return ":".join(groups)

    def test_home_and_public_addresses_are_flagged(self):
        v4, v6 = self.v4, self.v6
        for text in ("host " + v4(192, 168, 1, 20), "bind " + v4(10, 7, 7, 7) + ":8443", "a " + v4(172, 20, 3, 4),
                     "cgnat " + v4(100, 64, 1, 1), "ll " + v4(169, 254, 7, 7), "dns " + v4(8, 8, 8, 8),
                     "net " + v4(192, 168, 1, 0) + "/24", "ula " + v6("fd12", "3456", "789a", "", "1"),
                     "ll6 " + v6("fe80", "", "1234"), "pub " + v6("2606", "4700", "", "1111"),
                     "mixed " + v4(192, 0, 2, 1) + " and " + v4(192, 168, 7, 7)):
            with self.subTest(text=text.split(" ")[0]):
                self.assertTrue(self.flagged(text))

    def test_documentation_loopback_and_committed_ranges_pass(self):
        for text in ("192.0.2.10", "198.51.100.0/24", "203.0.113.7", "2001:db8::1", "127.0.0.1", "0.0.0.0", "::1", "::",
                     "10.120.0.2", "10.120.1.0/24", "224.0.0.1", "255.255.255.255", "198.18.0.1"):
            with self.subTest(text=text):
                self.assertFalse(self.flagged(text))

    def test_block_definitions_oids_and_versions_are_not_addresses(self):
        for text in ("10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 100.64.0.0/10 169.254.0.0/16", "fc00::/7 fe80::/10",
                     'new Oid("1.3.6.1.5.5.7.3.1")', "1.3.6.1.4.1.57264.1.15", "v8.0.0.0", "time 12:30:45", "mac 00:1a:2b:3c:4d:5e",
                     "sha256:abcdef0123456789", "std::vector and Add::add"):
            with self.subTest(text=text):
                self.assertFalse(self.flagged(text))

    def test_a_narrow_home_network_is_flagged_even_with_a_prefix(self):
        self.assertTrue(self.flagged(self.v4(192, 168, 0, 0) + "/24"))
        self.assertTrue(self.flagged(self.v4(10, 0, 0, 0) + "/24"))

    def test_reported_hits_do_not_repeat_the_literal(self):
        hits = scan_text(self.v4(192, 168, 55, 66), self.allow)
        self.assertEqual(len(hits), 1)
        for _, kind in hits:
            self.assertNotIn("192", kind)


if __name__ == "__main__":
    unittest.main()
