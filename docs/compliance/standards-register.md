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
| Supplier: Ostium identity packages (ADR-0019, from #164) | Per pinned `Ostium.*` package: CycloneDX SBOM attached to the Ostium release at its tag; provenance chain, checked and recorded in the PR for every version change (tag signed with Marco's key, pinned by fingerprint in the allowed-signers file; SourceLink source commit equal to the tag commit; lock-file SHA-512 equal to the value in the signed tag message or signed checksums file); Ostium's V6/V7 test report for the pinned version, attached to its signed release tag and referenced by tag and commit; outside readers cannot inspect it (ADR-0019) |
| GDPR | docs/compliance/dpia.md |
| ISO/IEC 25010 | docs/requirements/nfr.md |
| OTel semantic conventions | docs/observability/signal-catalogue.md |
| WCAG 2.2 AA | axe-core in Playwright |
| Conventional Commits + SemVer | commitlint, release-please |
| arc42 / C4 / ADR | docs/architecture, docs/adr |
