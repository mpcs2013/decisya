using System.Reflection;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>Issue #22, Story 1, Scenario 1: the ITenantScoped contract's own shape.</summary>
public class ITenantScopedShapeTests
{
    [Fact]
    public void ITenantScoped_declares_exactly_one_member_a_read_only_TenantId_property()
    {
        var type = typeof(ITenantScoped);

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        properties.Should().ContainSingle();

        var tenantIdProperty = properties[0];
        tenantIdProperty.Name.Should().Be(nameof(ITenantScoped.TenantId));
        tenantIdProperty.PropertyType.Should().Be<TenantId>();
        tenantIdProperty.SetMethod.Should().BeNull("ITenantScoped.TenantId must have no public setter");

        // Every method the interface declares must be that property's own accessor(s) —
        // i.e. the interface exposes nothing beyond the single TenantId property.
        var accessors = tenantIdProperty.GetAccessors(nonPublic: true);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        methods.Should().OnlyContain(m => accessors.Contains(m));
    }
}
