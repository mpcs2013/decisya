using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.ChainlessRoutes;
using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.ConstructionTimeReads;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.ChainlessRoutes;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.ConstructionTimeReads;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R16 (<see cref="ConstructionTimeTenantReadRule"/>, G3 G4-231-09) and R17
/// (<see cref="ChainlessHandlerRouteRule"/>, G3 S-12): red on a fixture per shape, green on the
/// compliant fixtures, and green on today's <c>src</c> (<see cref="KeyScope"/>).
/// </summary>
[Trait("Category", "Architecture")]
public class ConstructionTimeAndChainlessRuleTests
{
    [Theory]
    [InlineData(typeof(CtorReadsResolution), ".ctor", "ICurrentTenant.get_Resolution")]
    [InlineData(typeof(CtorReadsUserId), ".ctor", "ICurrentCaller.get_UserId")]
    [InlineData(typeof(FieldInitializerReadsIsPlatformAdmin), ".ctor", "ICurrentCaller.get_IsPlatformAdmin")]
    [InlineData(typeof(DiFactoryReadsResolution), "Register", "ICurrentTenant.get_Resolution")]
    public void R16_fails_on_a_read_at_construction_time_and_names_the_type_the_site_and_the_member(Type fixture, string site, string member)
    {
        var failures = ConstructionTimeTenantReadRule.Evaluate([fixture.Assembly]);

        failures.Should().Contain(f =>
            f.Contains(fixture.FullName!, StringComparison.Ordinal)
            && f.Contains(site, StringComparison.Ordinal)
            && f.Contains(member, StringComparison.Ordinal));
    }

    [Fact]
    public void R16_passes_the_use_time_pattern_a_stored_reference_and_a_Lazy_over_a_lambda()
    {
        var compliant = typeof(StoresTheReference).Assembly;

        var failures = ConstructionTimeTenantReadRule.Evaluate([compliant]);

        failures.Should().BeEmpty(string.Join("; ", failures));
        // The green fixtures really are read through the same scan: they are in that assembly.
        compliant.GetType(typeof(LazyOverALambda).FullName!).Should().NotBeNull();
    }

    [Fact]
    public void R16_is_green_on_today_src()
    {
        var failures = ConstructionTimeTenantReadRule.Evaluate(KeyScope.Assemblies);

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    [Fact]
    public void R16_exemptions_are_an_exact_list_that_is_empty_in_231_and_every_entry_exists()
    {
        ConstructionTimeTenantReadRule.Exemptions.Should().BeEmpty();
        ConstructionTimeTenantReadRule.UnknownExemptions(KeyScope.Assemblies).Should().BeEmpty();
    }

    [Theory]
    [InlineData(typeof(ImplementsAgentCommand), "implements Wolverine.Runtime.Agents.IAgentCommand")]
    [InlineData(typeof(CreatesEnvelope), "creates Wolverine.Envelope")]
    [InlineData(typeof(CreatesAcknowledgement), "creates Wolverine.Runtime.RemoteInvocation.Acknowledgement")]
    [InlineData(typeof(CreatesFailureAcknowledgement), "creates Wolverine.Runtime.RemoteInvocation.FailureAcknowledgement")]
    public void R17_fails_on_a_route_into_a_chainless_handler_and_names_the_type_and_the_target(Type fixture, string expected)
    {
        var failures = ChainlessHandlerRouteRule.Evaluate([fixture.Assembly]);

        failures.Should().Contain(f =>
            f.StartsWith(fixture.FullName!, StringComparison.Ordinal) && f.EndsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void R17_passes_a_type_that_only_takes_and_calls_IMessageBus()
    {
        var failures = ChainlessHandlerRouteRule.Evaluate([typeof(BusOnlyUser).Assembly]);

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    [Fact]
    public void R17_is_green_on_today_src_and_its_exemption_list_is_empty()
    {
        var failures = ChainlessHandlerRouteRule.Evaluate(KeyScope.Assemblies);

        failures.Should().BeEmpty(string.Join("; ", failures));
        ChainlessHandlerRouteRule.Exemptions.Should().BeEmpty();
    }

    [Fact]
    public void KeyScope_is_every_src_project_but_the_AppHost_and_the_Migrator()
    {
        KeyScope.Excluded.Should().Equal("Decisya.AppHost", "Decisya.Infrastructure.Migrator");
        KeyScope.ProjectNames.Should().Contain(["Decisya.Api", "Decisya.Bff", "Decisya.SharedKernel", "Decisya.Modules.Tenancy"]);
        KeyScope.ProjectNames.Should().NotContain(KeyScope.Excluded);
        KeyScope.Assemblies.Should().HaveCount(KeyScope.ProjectNames.Count);
    }
}
