using System.Reflection;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>Issue #21, G2 D5: the ICurrentCaller contract's own shape.</summary>
public class ICurrentCallerShapeTests
{
    [Fact]
    public void ICurrentCaller_declares_exactly_one_member_a_read_only_string_UserId_property()
    {
        var type = typeof(ICurrentCaller);

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        properties.Should().ContainSingle();

        var userIdProperty = properties[0];
        userIdProperty.Name.Should().Be(nameof(ICurrentCaller.UserId));
        userIdProperty.PropertyType.Should().Be<string>();
        userIdProperty.SetMethod.Should().BeNull("ICurrentCaller.UserId must have no public setter");

        var accessors = userIdProperty.GetAccessors(nonPublic: true);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        methods.Should().OnlyContain(m => accessors.Contains(m));
    }
}
