using System.Reflection;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>Issue #21, G2 D5 and issue #25, G2 D2: the ICurrentCaller contract's own shape.</summary>
public class ICurrentCallerShapeTests
{
    [Fact]
    public void ICurrentCaller_declares_exactly_two_members_read_only_UserId_and_IsPlatformAdmin()
    {
        var type = typeof(ICurrentCaller);

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        properties.Select(p => p.Name).Should().Equal(nameof(ICurrentCaller.IsPlatformAdmin), nameof(ICurrentCaller.UserId));

        var isPlatformAdmin = properties[0];
        isPlatformAdmin.PropertyType.Should().Be<bool>();
        isPlatformAdmin.SetMethod.Should().BeNull("ICurrentCaller.IsPlatformAdmin must have no public setter");

        var userIdProperty = properties[1];
        userIdProperty.PropertyType.Should().Be<string>();
        userIdProperty.SetMethod.Should().BeNull("ICurrentCaller.UserId must have no public setter");

        var accessors = properties.SelectMany(p => p.GetAccessors(nonPublic: true)).ToList();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        methods.Should().OnlyContain(m => accessors.Contains(m));
    }

    [Fact]
    public void An_implementation_that_only_supplies_UserId_is_not_a_platform_admin()
    {
        ICurrentCaller caller = new UserIdOnlyCaller();

        caller.IsPlatformAdmin.Should().BeFalse(
            "IsPlatformAdmin is a default interface member returning false, so a double with no role information is never an admin");
    }

    [Fact]
    public void IsPlatformAdmin_is_a_default_interface_member_not_an_abstract_one()
    {
        var getter = typeof(ICurrentCaller).GetProperty(nameof(ICurrentCaller.IsPlatformAdmin))!.GetMethod!;

        getter.IsAbstract.Should().BeFalse();
    }

    private sealed class UserIdOnlyCaller : ICurrentCaller
    {
        public string UserId => "user-1";
    }
}
