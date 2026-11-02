using Decisya.ArchitectureTests.Fixtures.TenancyViolations.PersonalDataLogs;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #230, R5 (G2 D2; G3 S-01): the personal-data catalogue loads, has its seven categories, and every entry names
/// something that exists, so a typo cannot silently turn a source off. The catalogue is policy data owned by the
/// architect and the security-reviewer; these tests read it and never edit it.
/// </summary>
[Trait("Category", "Architecture")]
public class PersonalDataCatalogueTests(ITestOutputHelper output)
{
    private static readonly string[] SevenCategories =
    [
        "user-identifier", "contact", "name", "account-number", "free-text", "ai-text", "secret-material",
    ];

    private static readonly PersonalDataCatalogue Catalogue = PersonalDataCatalogue.Load();

    [Fact]
    public void R5_the_catalogue_loads_with_its_seven_unique_categories()
    {
        Catalogue.Version.Should().Be(PersonalDataCatalogue.SupportedVersion);
        Catalogue.Categories.Select(c => c.Id).Should().BeEquivalentTo(SevenCategories);
        Catalogue.Categories.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        Catalogue.Categories.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Name));
    }

    [Fact]
    public void R5_a_catalogue_of_another_schema_version_is_rejected()
    {
        var act = () => PersonalDataCatalogue.Parse("""{ "version": 1, "categories": [] }""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*version 1*");
    }

    [Fact]
    public void R5_secret_material_is_pinned_as_never_suppressible_and_it_is_the_only_one()
    {
        Catalogue.Categories.Where(c => !c.Suppressible).Select(c => c.Id).Should().Equal("secret-material");
    }

    [Fact]
    public void R5_every_entry_exists_in_LogScope_or_in_a_referenced_assembly_and_unreferenced_ones_are_listed_not_failed()
    {
        var problems = PersonalDataLogRule.CatalogueProblems(LogScope.AssemblyPaths, Catalogue, out var notReferenced);

        foreach (var entry in notReferenced)
        {
            output.WriteLine("not referenced: " + entry);
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
        notReferenced.Should().OnlyContain(entry => entry.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal));
    }

    [Fact]
    public void R5_an_unknown_name_a_namespace_or_a_wildcard_fails_and_names_the_entry()
    {
        var secret = Catalogue.Categories.Single(c => c.Id == "secret-material");
        var broken = Catalogue with
        {
            Categories =
            [
                .. Catalogue.Categories.Where(c => c.Id != "secret-material"),
                secret with
                {
                    Members =
                    [
                        "Decisya.Nope.Gone::Member",
                        "Decisya.Bff.Session",
                        "Decisya.Bff.*::Item",
                        "Microsoft.AspNetCore.Http.IRequestCookieCollection::NoSuchMember",
                        "Microsoft.AspNetCore.Http.NoSuchType::Item",
                        "Microsoft.Extensions.AI.NoSuchType::Text",
                    ],
                },
            ],
        };

        var problems = PersonalDataLogRule.CatalogueProblems(LogScope.AssemblyPaths, broken, out var notReferenced);

        problems.Should().Contain(p => p.Contains("Decisya.Nope.Gone::Member", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("'Decisya.Bff.Session'", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("Decisya.Bff.*::Item", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("IRequestCookieCollection::NoSuchMember", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("Http.NoSuchType::Item", StringComparison.Ordinal));
        problems.Should().NotContain(p => p.Contains("AI.NoSuchType", StringComparison.Ordinal));
        notReferenced.Should().Contain(entry => entry.Contains("Microsoft.Extensions.AI.NoSuchType", StringComparison.Ordinal));
    }

    [Fact]
    public void R5_S_01_a_wrong_nameParameter_an_unknown_fallback_category_and_a_bad_suppression_category_fail()
    {
        var broken = Catalogue with
        {
            ClaimReaders =
            [
                .. Catalogue.ClaimReaders,
                new ClaimReaderEntry("Microsoft.AspNetCore.Authentication.AuthenticationTokenExtensions::GetTokenValue", "noSuchParameter", "secret-material"),
                new ClaimReaderEntry("Microsoft.IdentityModel.JsonWebTokens.JsonWebToken::TryGetPayloadValue", "key", "no-such-category"),
            ],
            Suppressions = [new SuppressionEntry("Decisya.Nope.Gone::Run", "no-such-category", "Fixture: a long enough justification text")],
        };

        var problems = PersonalDataLogRule.CatalogueProblems(LogScope.AssemblyPaths, broken, out _);

        problems.Should().Contain(p => p.Contains("noSuchParameter", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("unknown fallbackCategory", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("unknown category id", StringComparison.Ordinal));
    }

    [Fact]
    public void R5_extension_a_new_category_with_a_name_absent_from_the_real_file_reports_a_fixture_that_uses_it_with_no_code_change()
    {
        var site = typeof(PayeeRead).FullName + "::Run";
        var realRun = PersonalDataLogRule.Evaluate([typeof(PayeeRead).Assembly.Location], Catalogue);
        realRun.Findings.Should().NotContain(f => f.Site == site, "Payee is in no category of the real catalogue");

        var extended = Catalogue with
        {
            Categories =
            [
                .. Catalogue.Categories,
                new CatalogueCategory { Id = "custom-text", Name = "Custom text", MemberNames = ["Payee"] },
            ],
        };
        var result = PersonalDataLogRule.Evaluate([typeof(PayeeRead).Assembly.Location], extended);

        result.Findings.Should().Contain(f => f.Site == site && f.CategoryId == "custom-text" && f.CategoryName == "Custom text");
    }
}
