using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>
/// Issue #22, Story 3 and G4-22-01 (the non-EF half: resolution itself, proved without a
/// database). The EF-facing half of G4-22-01 — that an "Invalid" resolution throws before
/// any SQL is sent, that "None" yields zero rows against real Postgres data, and that the
/// filter is read per query rather than cached — is proved in
/// <c>tests/Decisya.Infrastructure.Persistence.Tests</c> (see the G4 report for #22).
/// </summary>
public class TenantResolutionTests
{
    [Fact]
    public void Default_resolution_is_Invalid()
    {
        default(TenantResolution).Kind.Should().Be(TenantResolutionKind.Invalid);
        default(TenantResolution).Should().Be(TenantResolution.Invalid);
    }

    [Fact]
    public void Invalid_TenantId_throws()
    {
        var act = () => TenantResolution.Invalid.TenantId;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NoTenant_TenantId_throws()
    {
        var act = () => TenantResolution.NoTenant.TenantId;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NoTenant_is_not_Invalid_and_is_not_a_default_or_zero_TenantId()
    {
        TenantResolution.NoTenant.Kind.Should().Be(TenantResolutionKind.None);
        TenantResolution.NoTenant.Should().NotBe(TenantResolution.Invalid);
    }

    [Fact]
    public void For_default_TenantId_throws()
    {
        var act = () => TenantResolution.For(default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void For_an_initialized_TenantId_resolves_to_that_tenant()
    {
        var tenantId = TenantId.New();

        var resolution = TenantResolution.For(tenantId);

        resolution.Kind.Should().Be(TenantResolutionKind.Tenant);
        resolution.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public void FromClaim_null_resolves_to_NoTenant()
    {
        var resolution = TenantResolution.FromClaim(null);

        resolution.Should().Be(TenantResolution.NoTenant);
    }

    [Fact]
    public void FromClaim_a_well_formed_guid_resolves_to_that_tenant()
    {
        const string claim = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

        var resolution = TenantResolution.FromClaim(claim);

        resolution.Kind.Should().Be(TenantResolutionKind.Tenant);
        resolution.TenantId.Should().Be(TenantId.Parse(claim));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0x7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    [InlineData("+7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    public void FromClaim_a_malformed_value_resolves_to_Invalid_never_NoTenant_and_names_no_tenant(string claim)
    {
        var resolution = TenantResolution.FromClaim(claim);

        resolution.Kind.Should().Be(TenantResolutionKind.Invalid);
        resolution.Should().NotBe(TenantResolution.NoTenant);
        var act = () => resolution.TenantId;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Two_resolutions_for_the_same_tenant_are_equal()
    {
        var tenantId = TenantId.New();

        TenantResolution.For(tenantId).Should().Be(TenantResolution.For(tenantId));
    }

    [Fact]
    public void ToString_reports_each_kind_distinctly()
    {
        var tenantId = TenantId.New();

        TenantResolution.Invalid.ToString().Should().Be("Invalid");
        TenantResolution.NoTenant.ToString().Should().Be("None");
        TenantResolution.For(tenantId).ToString().Should().Be($"Tenant({tenantId})");
    }
}
