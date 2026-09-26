// G4-15-32: every test in this project carries [Trait("Category", "AppHost")], excluding
// them from the CI unit step (ci.yml `--filter-not-trait "Category=AppHost"`), because
// they need DCP and the Aspire dashboard from the Aspire CLI bundle, which is not
// installed on CI runners (ADR-0010; F-5 tracks installing it). They run on Marco's host
// instead.
//
// Deviation from the G2/G3 design text ("applied once at assembly level... [assembly:
// AssemblyTrait("Category", "AppHost")]"): xunit.v3 (4.0.1) does not ship an
// AssemblyTraitAttribute type (xunit v2's Xunit.Sdk.AssemblyTraitAttribute was not
// carried forward; xunit.v3.core only exposes the lower-level ITraitAttribute
// extensibility point for a custom attribute to implement its own assembly-level trait
// discovery). Reported instead of guessed around silently: the equivalent applied here is
// a per-class [Trait("Category", "AppHost")], matching this repo's existing convention
// (tests/Decisya.SharedKernel.Tests/RepoPathsTests.cs). G6/G7 should confirm this reading.
