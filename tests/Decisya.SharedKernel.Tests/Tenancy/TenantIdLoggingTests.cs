using System.Reflection;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>Story 3 — TenantId's text form is safe to log unmasked.</summary>
public class TenantIdLoggingTests
{
    [Fact]
    public void ToString_produces_the_canonical_lowercase_guid_text()
    {
        var tenantId = TenantId.Parse("018F2C9E-6B7A-7C3E-8B1A-2F3C4D5E6F70");

        tenantId.ToString().Should().Be("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70");
    }

    [Fact]
    public void TenantId_carries_no_SensitiveAttribute_on_the_type_or_any_member()
    {
        Attribute.IsDefined(typeof(TenantId), typeof(SensitiveAttribute), inherit: true).Should().BeFalse();

        var members = typeof(TenantId).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        members.Should().OnlyContain(m => !Attribute.IsDefined(m, typeof(SensitiveAttribute), inherit: true));
    }

    [Fact]
    public void A_logged_TenantId_value_passes_through_SensitiveDataMaskingProcessor_unmasked()
    {
        var tenantId = TenantId.New();
        var processor = new SensitiveDataMaskingProcessor();

        var value = processor.ProcessValue("tenant_id", tenantId, out var masked);

        masked.Should().BeFalse();
        value.Should().NotBeNull();
        value!.ToString().Should().Be(tenantId.ToString());
    }

    [Fact]
    public void The_default_uninitialized_TenantId_formats_safely_as_an_empty_string()
    {
        var act = () => default(TenantId).ToString();

        act.Should().NotThrow();
        default(TenantId).ToString().Should().Be(string.Empty);
    }

    /// <summary>
    /// G4-32-12: pins the exact public instance-property shape the masking core's
    /// <c>SensitiveTypeAnalyzer.IsScalar</c>/graph-walk relies on staying scalar-shaped. A
    /// new member here must force a re-check of the masking outcome above.
    /// </summary>
    [Fact]
    public void TenantId_exposes_exactly_the_two_expected_public_instance_properties()
    {
        var properties = typeof(TenantId)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, p.PropertyType))
            .ToArray();

        properties.Should().BeEquivalentTo(
        [
            (Name: nameof(TenantId.Value), typeof(Guid)),
            (Name: nameof(TenantId.IsInitialized), typeof(bool)),
        ]);
    }
}
