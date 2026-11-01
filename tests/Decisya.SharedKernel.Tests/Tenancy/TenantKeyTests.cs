using System.Reflection;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>
/// Issue #231, G1 Story 5 / architecture rule R12 / G3 S-01: <see cref="TenantKey"/> is the one
/// builder of a tenant-keyed Redis or object-storage key.
/// </summary>
[Trait("Category", "Unit")]
public class TenantKeyTests
{
    private const string TenantAText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70";
    private const string TenantBText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f71";

    private static readonly TenantId TenantA = TenantId.Parse(TenantAText);
    private static readonly TenantId TenantB = TenantId.Parse(TenantBText);

    [Fact]
    public void A_key_starts_with_the_canonical_lowercase_tenant_text_and_joins_segments()
    {
        var key = TenantKey.For(TenantA, "budgets", "2026-11.json");

        key.Tenant.Should().Be(TenantA);
        key.ToCacheKey().Should().Be(TenantAText + ":budgets:2026-11.json");
        key.ToObjectKey().Should().Be(TenantAText + "/budgets/2026-11.json");
    }

    [Fact]
    public void Two_tenants_never_produce_the_same_key_for_the_same_resource_name()
    {
        var a = TenantKey.For(TenantA, "report");
        var b = TenantKey.For(TenantB, "report");

        a.ToCacheKey().Should().NotBe(b.ToCacheKey());
        a.ToObjectKey().Should().NotBe(b.ToObjectKey());
        a.Should().NotBe(b);
    }

    [Fact]
    public void default_TenantId_is_refused_and_no_key_is_produced()
    {
        var act = () => TenantKey.For(default, "report");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_segment_is_refused()
    {
        var act = () => TenantKey.For(TenantA);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_null_segment_is_refused_with_ArgumentNullException()
    {
        var act = () => TenantKey.For(TenantA, "report", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("../tenant-b/file")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("A")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a*")]
    [InlineData("a%2fb")]
    public void A_segment_that_could_escape_the_tenant_prefix_is_refused(string segment)
    {
        var act = () => TenantKey.For(TenantA, segment);

        act.Should().Throw<ArgumentException>();
    }

    // G3 S-01: a char.IsLetterOrDigit implementation would accept every one of these.
    [Theory]
    [InlineData("é")]
    [InlineData("café")]
    [InlineData("٣")] // ARABIC-INDIC DIGIT THREE
    [InlineData("１")] // FULLWIDTH DIGIT ONE
    [InlineData("ａ")] // FULLWIDTH LATIN SMALL LETTER A
    [InlineData("ß")]
    [InlineData("日本")]
    public void A_non_ASCII_letter_or_digit_is_refused(string segment)
    {
        var act = () => TenantKey.For(TenantA, segment);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a_b")]
    [InlineData("a-b")]
    [InlineData("...")]
    [InlineData("0")]
    public void The_allowed_characters_are_accepted(string segment)
    {
        var key = TenantKey.For(TenantA, segment);

        key.ToCacheKey().Should().EndWith(":" + segment);
    }

    [Fact]
    public void A_segment_is_at_most_128_characters()
    {
        TenantKey.For(TenantA, new string('a', TenantKey.MaxSegmentLength)).Should().NotBeNull();

        var act = () => TenantKey.For(TenantA, new string('a', TenantKey.MaxSegmentLength + 1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_whole_key_is_at_most_512_characters()
    {
        // 36 (tenant) + 4 * (1 + 100) = 440; a fifth segment of 71 characters gives 36 + 404 + 72 = 512.
        var hundred = new string('a', 100);
        var fits = TenantKey.For(TenantA, hundred, hundred, hundred, hundred, new string('b', 71));
        fits.ToCacheKey().Length.Should().Be(TenantKey.MaxKeyLength);
        fits.ToObjectKey().Length.Should().Be(TenantKey.MaxKeyLength);

        var act = () => TenantKey.For(TenantA, hundred, hundred, hundred, hundred, new string('b', 72));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_refusal_message_never_carries_the_segment_value()
    {
        const string Secret = "tenant-b/secret";

        var act = () => TenantKey.For(TenantA, Secret);

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(Secret);
    }

    [Fact]
    public void Keys_have_value_equality_over_the_tenant_and_the_segments()
    {
        var first = TenantKey.For(TenantA, "report", "q1");
        var same = TenantKey.For(TenantA, "report", "q1");

        first.Should().Be(same);
        first.GetHashCode().Should().Be(same.GetHashCode());
        first.Should().NotBe(TenantKey.For(TenantA, "report", "q2"));
        first.Should().NotBe(TenantKey.For(TenantA, "report-q1"));
    }

    // G3 S-01 / T-15: a log statement or exception message that formats a key by accident must not
    // print a tenant id.
    [Fact]
    public void ToString_is_the_fixed_type_name_and_never_the_key()
    {
        var key = TenantKey.For(TenantA, "report");

        key.ToString().Should().Be("TenantKey");
        key.ToString().Should().NotContain(TenantAText);
    }

    [Fact]
    public void The_public_surface_takes_a_TenantId_and_never_a_string_Guid_or_object_for_the_tenant()
    {
        typeof(TenantKey).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();

        var builders = typeof(TenantKey)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToList();

        builders.Should().ContainSingle("For is the only way to obtain a key");
        builders[0].Name.Should().Be(nameof(TenantKey.For));
        builders[0].GetParameters()[0].ParameterType.Should().Be<TenantId>();

        var tenantTyped = typeof(TenantKey)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OfType<MethodInfo>()
            .Where(method => !method.IsSpecialName)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(Guid) || parameter.ParameterType == typeof(object))
            .Select(parameter => parameter.Name)
            .ToList();

        // Equals(object) is the one place object appears; it is not a tenant parameter.
        tenantTyped.Should().BeEquivalentTo(["obj"]);
    }
}
