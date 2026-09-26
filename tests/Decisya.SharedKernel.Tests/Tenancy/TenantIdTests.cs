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
        // N32-01: TenantId has no public string TryParse overload (it would be an ASP.NET
        // Core model-binding convention); callers with a string use the span overload via
        // AsSpan(), or Parse inside a try.
        var succeeded = TenantId.TryParse("not-a-guid".AsSpan(), out var tenantId);

        succeeded.Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
        tenantId.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public void TryParse_of_a_null_string_span_reports_failure_without_throwing()
    {
        // ((string?)null).AsSpan() is the empty span, per MemoryExtensions.AsSpan(string?).
        var succeeded = TenantId.TryParse(((string?)null).AsSpan(), out var tenantId);

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

    /// <summary>
    /// N32-01 (G6 review): a public static <c>TryParse(string, out T)</c> or
    /// <c>TryParse(string, IFormatProvider, out T)</c> is the model-binding convention
    /// ASP.NET Core minimal APIs and MVC use — with no <c>IParsable&lt;T&gt;</c> required.
    /// A public static <c>BindAsync</c> is the other minimal-API binding convention. Neither
    /// exists on <see cref="TenantId"/>.
    /// </summary>
    [Fact]
    public void TenantId_declares_no_string_based_TryParse_and_no_BindAsync()
    {
        var staticMethods = typeof(TenantId).GetMethods(BindingFlags.Public | BindingFlags.Static);

        staticMethods.Should().NotContain(m =>
            m.Name == "TryParse"
            && m.GetParameters().Length > 0
            && m.GetParameters()[0].ParameterType == typeof(string));

        staticMethods.Should().NotContain(m => m.Name == "BindAsync");
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

    /// <summary>
    /// N32-05(f) (G6 review): unwraps <c>Nullable&lt;T&gt;</c> so <c>DateTime?</c> and
    /// <c>DateTimeOffset?</c> can't slip through, and also checks every method parameter
    /// (unwrapping <see langword="out"/> parameters' by-ref element type), not just
    /// properties and return types.
    /// </summary>
    [Fact]
    public void TenantId_exposes_no_member_typed_DateTime_DateTimeOffset_or_a_NodaTime_type()
    {
        var methods = typeof(TenantId).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        var propertyTypes = typeof(TenantId).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType);
        var returnTypes = methods.Select(m => m.ReturnType);
        var parameterTypes = methods
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType);

        var allTypes = propertyTypes.Concat(returnTypes).Concat(parameterTypes);

        allTypes.Should().NotContain(t => IsDateOrTimeType(t));
    }

    private static bool IsDateOrTimeType(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        return unwrapped == typeof(DateTime)
            || unwrapped == typeof(DateTimeOffset)
            || (unwrapped.Namespace != null && unwrapped.Namespace.StartsWith("NodaTime", StringComparison.Ordinal));
    }
}
