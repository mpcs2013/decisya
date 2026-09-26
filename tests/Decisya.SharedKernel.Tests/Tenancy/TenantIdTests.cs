using System.ComponentModel;
using System.Reflection;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>Story 1 — TenantId is a validated, never-default identifier.</summary>
public class TenantIdTests
{
    private const string CanonicalText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70";

    [Fact]
    public void New_produces_an_initialized_non_empty_TenantId()
    {
        var tenantId = TenantId.New();

        tenantId.IsInitialized.Should().BeTrue();
        tenantId.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void A_well_formed_claim_string_parses_and_formats_back_to_the_same_lowercase_text()
    {
        var tenantId = TenantId.Parse(CanonicalText);

        tenantId.ToString().Should().Be(CanonicalText);
    }

    [Fact]
    public void A_malformed_claim_string_is_rejected_and_produces_no_instance()
    {
        var act = () => TenantId.Parse("not-a-guid");

        act.Should().Throw<TenantIdFormatException>();
    }

    [Fact]
    public void TryParse_reports_failure_without_throwing_and_the_out_parameter_is_the_default()
    {
        var succeeded = TenantId.TryParse("not-a-guid", out var tenantId);

        succeeded.Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
        tenantId.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public void TryParse_of_a_null_string_reports_failure_without_throwing()
    {
        var succeeded = TenantId.TryParse((string?)null, out var tenantId);

        succeeded.Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
    }

    [Fact]
    public void Parse_of_a_null_string_throws()
    {
        var act = () => TenantId.Parse((string?)null);

        act.Should().Throw<TenantIdFormatException>();
    }

    [Fact]
    public void Guid_Empty_is_rejected_by_From()
    {
        var act = () => TenantId.From(Guid.Empty);

        act.Should().Throw<TenantIdFormatException>();
    }

    [Fact]
    public void The_all_zero_guid_text_is_rejected_by_Parse()
    {
        var act = () => TenantId.Parse("00000000-0000-0000-0000-000000000000");

        act.Should().Throw<TenantIdFormatException>();
    }

    [Fact]
    public void The_all_zero_guid_text_is_rejected_by_TryParse_string_overload()
    {
        TenantId.TryParse("00000000-0000-0000-0000-000000000000", out var tenantId).Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
    }

    [Fact]
    public void The_all_zero_guid_text_is_rejected_by_TryParse_span_overload()
    {
        TenantId.TryParse("00000000-0000-0000-0000-000000000000".AsSpan(), out var tenantId).Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
    }

    [Fact]
    public void The_default_TenantId_obtained_without_any_factory_is_never_initialized()
    {
        default(TenantId).IsInitialized.Should().BeFalse();
    }

    [Theory]
    [InlineData("018F2C9E-6B7A-7C3E-8B1A-2F3C4D5E6F70")]
    [InlineData("  018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70")]
    [InlineData("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70  ")]
    public void Two_instances_parsed_from_the_same_guid_differing_only_by_case_or_whitespace_are_equal(string variant)
    {
        var canonical = TenantId.Parse(CanonicalText);
        var other = TenantId.Parse(variant);

        other.Should().Be(canonical);
        (other == canonical).Should().BeTrue();
        (other != canonical).Should().BeFalse();
        other.GetHashCode().Should().Be(canonical.GetHashCode());
    }

    [Fact]
    public void TenantId_exposes_no_public_settable_member()
    {
        var properties = typeof(TenantId).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        properties.Should().OnlyContain(p => p.SetMethod == null, "TenantId must be immutable after construction");
    }

    // --- Default fails closed (G4-32-04) ---

    [Fact]
    public void Default_TenantId_Value_throws_InvalidOperationException_with_a_fixed_message()
    {
        var act = () => default(TenantId).Value;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("This TenantId was never initialised via TenantId.New, TenantId.From or TenantId.Parse; use default(TenantId) only as a placeholder, never as a value.");
    }

    [Fact]
    public void Default_TenantId_formats_as_an_empty_string_without_throwing()
    {
        var act = () => default(TenantId).ToString();

        act.Should().NotThrow();
        default(TenantId).ToString().Should().BeEmpty();
    }

    // --- G4-32-07: no route or string binding ---

    [Fact]
    public void TenantId_implements_neither_IParsable_nor_ISpanParsable()
    {
        var interfaces = typeof(TenantId).GetInterfaces();

        interfaces.Should().NotContain(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>));
        interfaces.Should().NotContain(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISpanParsable<>));
    }

    [Fact]
    public void TenantId_carries_no_TypeConverterAttribute()
    {
        Attribute.GetCustomAttribute(typeof(TenantId), typeof(TypeConverterAttribute)).Should().BeNull();
    }

    [Fact]
    public void TenantId_declares_no_implicit_or_explicit_conversion_from_string_or_guid()
    {
        var conversions = typeof(TenantId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .ToArray();

        conversions.Should().NotContain(m => m.GetParameters()[0].ParameterType == typeof(string));
        conversions.Should().NotContain(m => m.GetParameters()[0].ParameterType == typeof(Guid));
    }

    // --- G4-32-11: no time accessor ---

    [Fact]
    public void New_produces_a_version_7_guid()
    {
        TenantId.New().Value.Version.Should().Be(7);
    }

    [Fact]
    public void One_thousand_calls_to_New_produce_distinct_values()
    {
        var values = Enumerable.Range(0, 1_000).Select(_ => TenantId.New().Value).ToArray();

        values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TenantId_exposes_no_member_typed_DateTime_DateTimeOffset_or_a_NodaTime_type()
    {
        var members = typeof(TenantId).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .Concat(typeof(TenantId).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.ReturnType));

        members.Should().NotContain(t =>
            t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || (t.Namespace != null && t.Namespace.StartsWith("NodaTime", StringComparison.Ordinal)));
    }
}
