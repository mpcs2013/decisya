using System.Reflection;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>
/// G4-32-05: <see cref="TenantIdFormatException"/> carries no input at all — not a
/// sanitised preview, not a raw-value property, nothing in <see cref="Exception.InnerException"/>
/// or <see cref="Exception.Data"/>.
/// </summary>
public class TenantIdFormatExceptionTests
{
    private const string FixedMessage = "The supplied value is not a valid tenant identifier.";

    public static TheoryData<string, string> HostileInputs()
    {
        var data = new TheoryData<string, string>();

        var crlfCanary = Canaries.Unique("crlf");
        data.Add(crlfCanary, crlfCanary + "\r\n\u001b[31mnot-a-guid\u001b[0m");

        var hugeCanary = Canaries.Unique("huge");
        data.Add(hugeCanary, hugeCanary + new string('a', 10 * 1024));

        var jwtCanary = Canaries.Unique("jwt");
        data.Add(jwtCanary, jwtCanary + "." + Canaries.JwtShaped());

        return data;
    }

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void Parse_never_echoes_hostile_input_and_the_message_stays_fixed(string canary, string hostileInput)
    {
        var act = () => TenantId.Parse(hostileInput);

        var exception = act.Should().Throw<TenantIdFormatException>().Which;
        exception.Message.Should().Be(FixedMessage);
        exception.ToString().Should().NotContain(canary);
    }

    [Fact]
    public void The_exception_declares_no_public_property_beyond_FormatExceptions_own()
    {
        var declaredProperties = typeof(TenantIdFormatException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        declaredProperties.Should().BeEmpty();
    }

    [Fact]
    public void The_exception_has_a_null_inner_exception_and_empty_data()
    {
        var exception = new TenantIdFormatException();

        exception.InnerException.Should().BeNull();
        exception.Data.Count.Should().Be(0);
    }

    [Fact]
    public void The_exception_derives_from_FormatException()
    {
        typeof(TenantIdFormatException).BaseType.Should().Be<FormatException>();
    }

    /// <summary>
    /// N32-05(e) (G6 review): pins the constructor surface, not only the declared
    /// properties, so a later <c>TenantIdFormatException(string message)</c> (which could
    /// echo caller input unnoticed) fails this test the moment it's added.
    /// </summary>
    [Fact]
    public void The_exception_declares_exactly_one_public_parameterless_constructor()
    {
        var constructors = typeof(TenantIdFormatException)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        constructors.Should().ContainSingle();
        constructors[0].GetParameters().Should().BeEmpty();
    }
}
