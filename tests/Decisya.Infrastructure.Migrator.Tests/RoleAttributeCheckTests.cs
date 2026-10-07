namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>Issue #120, C-06, G4-120-04: the read-back that fails closed on an elevated module role.</summary>
[Trait("Category", "Unit")]
public sealed class RoleAttributeCheckTests
{
    private const string Role = "decisya_tenancy_app";

    private static MigrationRunner.RoleAttributes Clean => new(
        Super: false, CreateDb: false, CreateRole: false, Replication: false, BypassRls: false, Inherit: false, CanLogin: true);

    [Fact]
    public void A_clean_role_passes_the_attribute_check()
    {
        var act = () => MigrationRunner.EnsureNoElevatedAttributes(Role, Clean);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("SUPERUSER")]
    [InlineData("CREATEDB")]
    [InlineData("CREATEROLE")]
    [InlineData("REPLICATION")]
    [InlineData("BYPASSRLS")]
    [InlineData("INHERIT")]
    [InlineData("NOLOGIN")]
    public void An_elevated_attribute_fails_the_check_naming_the_role_and_the_attribute(string attribute)
    {
        var attributes = attribute switch
        {
            "SUPERUSER" => Clean with { Super = true },
            "CREATEDB" => Clean with { CreateDb = true },
            "CREATEROLE" => Clean with { CreateRole = true },
            "REPLICATION" => Clean with { Replication = true },
            "BYPASSRLS" => Clean with { BypassRls = true },
            "INHERIT" => Clean with { Inherit = true },
            "NOLOGIN" => Clean with { CanLogin = false },
            _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, null),
        };

        var act = () => MigrationRunner.EnsureNoElevatedAttributes(Role, attributes);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(Role).And.Contain(attribute);
    }
}
