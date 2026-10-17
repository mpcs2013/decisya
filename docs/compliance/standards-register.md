# Standards register

See `docs/PHASE-0.md` for the full table with owners and evidence. Re-checked by `compliance-auditor` at every phase exit.

| Standard | Evidence artifact |
| --- | --- |
| ISO/IEC/IEEE 12207 | Phase docs + GitHub milestones |
| NIST SSDF SP 800-218 | docs/security/ssdf-checklist.md |
| OWASP ASVS 5.0 L2 | docs/security/asvs-l2.md |
| OWASP Top 10 (2025) | CI: analyzers/CodeQL, ZAP baseline |
| OWASP LLM Top 10 | Phases 5–7 prompt-injection suite |
| OWASP SAMM | docs/security/samm.md |
| SLSA L2 | SBOM + cosign in release workflow |
| Supplier: Ostium identity packages (ADR-0019 and its 2026-10-17 amendment, from #164) | Per pinned `Ostium.*` package: CycloneDX SBOM inside the package (`sbom/bom.cdx.json`), covered by the signed SHA-512. Provenance chain, checked by Marco with Ostium's check script for every version change, with the `RESULT: PASS` block in the PR. The chain covers: the tag signed with the release key (namespace `git`); the checksums file `docs/releases/<version>/ostium-<version>.checksums` on Ostium's `main`, signed with the same key (namespace `ostium-checksums`); the key pinned by fingerprint `SHA256:rRaNoF/hJgpjYhrZ61pZ7jnRdOnQYYvDo/eEZpWCctI` in `.github/allowed-signers/ostium`; the SourceLink commit equal to the tag commit; the lock-file SHA-512 equal to the signed value. ASVS 5.0 V6, V7, V9 and V10 test report for the pinned version, committed next to the checksums file and covered by its signature; outside readers cannot inspect it. **Gap: 0.1.1 has no report.** Before the Phase 0 exit audit, Decisya moves to the first release with one; otherwise the audit records the gap as open (ADR-0019 amendment A4) |
| GDPR | docs/compliance/dpia.md |
| ISO/IEC 25010 | docs/requirements/nfr.md |
| OTel semantic conventions | docs/observability/signal-catalogue.md |
| WCAG 2.2 AA | axe-core in Playwright |
| Conventional Commits + SemVer | commitlint, release-please |
| arc42 / C4 / ADR | docs/architecture, docs/adr |
