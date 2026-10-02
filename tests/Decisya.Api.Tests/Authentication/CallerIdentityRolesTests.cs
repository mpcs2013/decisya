using System.Security.Claims;
using Decisya.Api.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 G4-25-01 (T-01, T-02), the parser half: <see cref="CallerIdentity.HasPlatformAdminRole"/> is
/// true only for the exact, ordinal value <c>platform-admin</c> in claims of type exactly
/// <c>roles</c>, all of them strings. Anything ambiguous is false, and odd roles never make
/// <see cref="CallerIdentity.From"/> return <see langword="null"/>.
/// </summary>
public class CallerIdentityRolesTests
{
    private const string Admin = "platform-admin";

    [Fact]
    public void The_exact_role_value_is_an_admin()
    {
        HasRole(new Claim("roles", Admin)).Should().BeTrue();
    }

    [Fact]
    public void A_multi_value_array_with_the_role_is_an_admin()
    {
        HasRole(new Claim("roles", "tenant-user"), new Claim("roles", Admin)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Platform-Admin")]
    [InlineData("PLATFORM-ADMIN")]
    [InlineData("platform-admin-x")]
    [InlineData("xplatform-admin")]
    [InlineData("platform-admin ")]
    [InlineData(" platform-admin")]
    [InlineData("platform_admin")]
    [InlineData("admin")]
    [InlineData("tenant-user")]
    [InlineData("")]
    public void Any_other_string_value_is_not_an_admin(string value)
    {
        HasRole(new Claim("roles", value)).Should().BeFalse();
    }

    [Fact]
    public void A_missing_roles_claim_is_not_an_admin_and_the_identity_is_still_valid()
    {
        var identity = CallerIdentity.From(Principal(new Claim("sub", "dev-alice")));

        identity.Should().NotBeNull();
        identity!.HasPlatformAdminRole.Should().BeFalse();
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("role")]
    [InlineData("Roles")]
    [InlineData("custom")]
    [InlineData("resource_access")]
    [InlineData("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")]
    public void The_value_under_any_other_claim_type_is_ignored(string claimType)
    {
        HasRole(new Claim(claimType, Admin)).Should().BeFalse();
    }

    [Fact]
    public void The_role_only_inside_realm_access_is_ignored()
    {
        var realmAccess = new Claim("realm_access", $$"""{"roles":["{{Admin}}"]}""", JsonClaimValueTypes.Json);

        HasRole(realmAccess).Should().BeFalse();
    }

    [Fact]
    public void A_non_string_roles_claim_next_to_a_string_admin_value_makes_the_set_ambiguous()
    {
        var nested = new Claim("roles", """{"x":1}""", JsonClaimValueTypes.Json);

        HasRole(new Claim("roles", Admin), nested).Should().BeFalse();
        HasRole(nested, new Claim("roles", Admin)).Should().BeFalse();
    }

    [Theory]
    [InlineData(JsonClaimValueTypes.Json)]
    [InlineData(JsonClaimValueTypes.JsonArray)]
    [InlineData(ClaimValueTypes.Integer32)]
    [InlineData(ClaimValueTypes.Boolean)]
    public void A_roles_claim_with_a_non_string_value_type_is_never_an_admin(string valueType)
    {
        HasRole(new Claim("roles", Admin, valueType)).Should().BeFalse();
    }

    [Fact]
    public void Odd_roles_never_make_the_identity_null()
    {
        var identity = CallerIdentity.From(Principal(
            new Claim("sub", "dev-alice"), new Claim("roles", """[{"a":1}]""", JsonClaimValueTypes.JsonArray)));

        identity.Should().NotBeNull();
        identity!.HasPlatformAdminRole.Should().BeFalse();
    }

    [Fact]
    public void The_role_is_read_independently_of_the_tenant_claim()
    {
        var identity = CallerIdentity.From(Principal(
            new Claim("sub", "dev-admin"), new Claim("tenant_id", "7c9e6679-7425-40de-944b-e07fc1f90ae7"), new Claim("roles", Admin)));

        identity!.HasPlatformAdminRole.Should().BeTrue("the 'no tenant' half is decided by the resolution, not here");
        identity.TenantId.Should().NotBeNull();
    }

    private static bool HasRole(params Claim[] roleClaims)
    {
        var identity = CallerIdentity.From(Principal([new Claim("sub", "dev-user"), .. roleClaims]));

        identity.Should().NotBeNull();
        return identity!.HasPlatformAdminRole;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));
}
