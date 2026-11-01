using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.KeyBuilding;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.KeyBuilding;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R10 and R11 (<see cref="TenantKeyRule"/>; G1 Story 5; G3 G4-231-05): every tenant-keyed
/// Redis or object-storage key goes through <c>TenantKey</c>. Red on a fixture per clause, green on the
/// compliant fixtures, exact in its exemptions, and green on today's <c>src</c> (<see cref="KeyScope"/>),
/// where the Redis session ticket store is exempt by exact name and is not flagged. R12 is
/// <c>TenantKeyTests</c> in <c>Decisya.SharedKernel.Tests</c>; R13 is <c>TenantKeyRedisIsolationTests</c> in
/// <c>Decisya.Bff.Tests</c>.
/// </summary>
[Trait("Category", "Architecture")]
public class TenantKeyRuleTests
{
    private const string NoKey = "without TenantKey.ToCacheKey or TenantKey.ToObjectKey";
    private const string TenantText = "references tenant text";

    // ---- R10: each clause is red ----

    [Theory]
    [InlineData(typeof(HandBuiltKeyCache), NoKey)]
    [InlineData(typeof(HandBuiltKeyCache), "TenantId.get_Value")]
    [InlineData(typeof(InterpolatedKeyCache), NoKey)]
    [InlineData(typeof(InterpolatedKeyCache), TenantText)]
    [InlineData(typeof(ResolutionTextKeyCache), NoKey)]
    [InlineData(typeof(ResolutionTextKeyCache), "TenantResolution.ToString")]
    [InlineData(typeof(PlainStringKeyCache), NoKey)]
    [InlineData(typeof(ClaimFedKeyCache), "System.Security.Claims")]
    [InlineData(typeof(TenantTextBesideKeyCache), TenantText)]
    public void R10_fails_on_a_key_built_by_hand_and_names_the_type_and_the_clause(Type fixture, string clause)
    {
        var failures = TenantKeyRule.Evaluate([fixture.Assembly]);

        failures.Should().Contain(f =>
            f.StartsWith(fixture.FullName!, StringComparison.Ordinal) && f.Contains(clause, StringComparison.Ordinal));
    }

    [Fact]
    public void R10_checks_each_method_so_a_type_with_one_compliant_method_and_one_hand_built_key_is_red_and_names_the_second_method()
    {
        var fixture = typeof(MixedKeyCache);

        var failures = TenantKeyRule.Evaluate([fixture.Assembly])
            .Where(f => f.StartsWith(fixture.FullName!, StringComparison.Ordinal))
            .ToList();

        failures.Should().Contain(f => f.Contains("::" + nameof(MixedKeyCache.GetRawAsync), StringComparison.Ordinal) && f.Contains(NoKey, StringComparison.Ordinal));
        failures.Should().NotContain(f => f.Contains(nameof(MixedKeyCache.GetForAsync), StringComparison.Ordinal), "the compliant method is not named");
    }

    [Fact]
    public void R10_passes_a_type_whose_every_sink_method_builds_its_key_with_TenantKey()
    {
        var failures = TenantKeyRule.Evaluate([typeof(TenantKeyedCache).Assembly], Exempt(typeof(SessionKeyedCache)));

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    // ---- R10: the exemption is exact, and narrower than "anything goes" ----

    [Fact]
    public void R10_a_session_keyed_store_is_red_unless_it_is_on_the_exemption_list_by_exact_name()
    {
        var session = typeof(SessionKeyedCache);

        TenantKeyRule.Evaluate([session.Assembly], new HashSet<string>(StringComparer.Ordinal))
            .Should().Contain(f => f.StartsWith(session.FullName!, StringComparison.Ordinal));

        foreach (var notExact in new[] { session.Namespace!, session.Namespace + ".*", session.FullName + "X", session.Name })
        {
            TenantKeyRule.Evaluate([session.Assembly], Exempt(notExact))
                .Should().Contain(f => f.StartsWith(session.FullName!, StringComparison.Ordinal), $"'{notExact}' is not the exact type name");
        }
    }

    [Fact]
    public void R10_a_nested_type_counts_as_its_outermost_type_for_the_exemption_and_a_nested_type_of_a_non_exempt_type_is_still_checked()
    {
        var exempt = typeof(NestedExemptOuter);
        var plain = typeof(NestedPlainOuter);

        var failures = TenantKeyRule.Evaluate([exempt.Assembly], Exempt(exempt));

        failures.Should().NotContain(f => f.StartsWith(exempt.FullName!, StringComparison.Ordinal), "the nested type is covered by its outer type's exact name");
        failures.Should().Contain(f => f.StartsWith(plain.FullName + "+Inner", StringComparison.Ordinal) && f.Contains(NoKey, StringComparison.Ordinal));

        TenantKeyRule.Evaluate([exempt.Assembly], new HashSet<string>(StringComparer.Ordinal))
            .Should().Contain(f => f.StartsWith(exempt.FullName + "+Inner", StringComparison.Ordinal), "without the exemption the nested type is red");
    }

    [Fact]
    public void R10_an_exempt_type_still_may_not_read_the_tenant_id_claim_or_reference_tenant_text()
    {
        var claim = typeof(ExemptReadsTenantClaim);
        var text = typeof(ExemptReadsTenantText);

        var failures = TenantKeyRule.Evaluate([claim.Assembly], Exempt(claim, text));

        failures.Should().Contain(f => f.StartsWith(claim.FullName!, StringComparison.Ordinal) && f.Contains("tenant_id claim", StringComparison.Ordinal));
        failures.Should().Contain(f => f.StartsWith(text.FullName!, StringComparison.Ordinal) && f.Contains(TenantText, StringComparison.Ordinal));
        failures.Should().NotContain(
            f => f.StartsWith(claim.FullName!, StringComparison.Ordinal) && f.Contains(NoKey, StringComparison.Ordinal),
            "an exempt type skips only the per-method TenantKey clause and the System.Security.Claims ban");
    }

    [Fact]
    public void R10_an_exemption_that_is_a_namespace_a_wildcard_or_an_unknown_name_fails()
    {
        var unknown = TenantKeyRule.UnknownExemptions(
            KeyScope.Assemblies,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Decisya.Bff.Session",
                "Decisya.Bff.Session.*",
                "Decisya.Bff.Session.RedisTicketStoreX",
                "Decisya.Bff.Session.RedisTicketStore",
            });

        unknown.Should().BeEquivalentTo(["Decisya.Bff.Session", "Decisya.Bff.Session.*", "Decisya.Bff.Session.RedisTicketStoreX"]);
    }

    // ---- R10 on today's src: the session ticket store is not flagged (G1 Story 5, last-but-one scenario) ----

    [Fact]
    public void R10_is_green_on_today_src_and_the_exemptions_are_exactly_the_three_session_types_and_exist()
    {
        var failures = TenantKeyRule.Evaluate(KeyScope.Assemblies);

        failures.Should().BeEmpty(string.Join("; ", failures));
        TenantKeyRule.Exemptions.Should().BeEquivalentTo(
            ["Decisya.Bff.Session.RedisTicketStore", "Decisya.Bff.Session.RedisRefreshLock", "Decisya.Bff.Session.RedisRegistration"]);
        TenantKeyRule.UnknownExemptions(KeyScope.Assemblies).Should().BeEmpty();
    }

    [Fact]
    public void R10_without_its_exemptions_the_rule_does_flag_the_Redis_session_types_so_the_green_run_is_not_vacuous()
    {
        var failures = TenantKeyRule.Evaluate(KeyScope.Assemblies, new HashSet<string>(StringComparer.Ordinal));

        failures.Should().Contain(f => f.StartsWith("Decisya.Bff.Session.RedisTicketStore", StringComparison.Ordinal));
    }

    // ---- R11: the scope is every src project but the AppHost and the Migrator ----

    [Fact]
    public void R11_every_src_project_is_in_KeyScope_except_exactly_the_AppHost_and_the_Migrator()
    {
        var allProjects = Directory
            .EnumerateFiles(RepoPaths.Find("src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

        KeyScope.Excluded.Should().Equal("Decisya.AppHost", "Decisya.Infrastructure.Migrator");
        KeyScope.Excluded.Should().OnlyContain(name => allProjects.Contains(name), "an exclusion must name an existing project");
        KeyScope.ProjectNames.Concat(KeyScope.Excluded).Order(StringComparer.Ordinal).Should().Equal(allProjects);
        KeyScope.Assemblies.Select(a => a.GetName().Name).Should().BeEquivalentTo(KeyScope.ProjectNames);
    }

    private static HashSet<string> Exempt(params Type[] types) =>
        new(types.Select(t => t.FullName!), StringComparer.Ordinal);

    private static HashSet<string> Exempt(string name) => new(StringComparer.Ordinal) { name };
}
