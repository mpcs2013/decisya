using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 G4-21-02: pins the exact anonymous and Tenancy-membership-opt-out endpoint sets, and the
/// <c>Tenancy.Owner</c> policy on <c>GET /api/tenancy/members</c>, against the real host's own
/// endpoint metadata — never a hand-maintained list a future endpoint could silently miss.
/// <c>SkipTenantMembershipMetadata</c> is internal to <c>Decisya.Modules.Tenancy</c>, so this
/// matches it by runtime type name rather than a compile-time reference (no
/// <c>InternalsVisibleTo</c> exists, or should exist, between that module and this project).
/// </summary>
public class EndpointAuthorizationTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string AdminPolicy = "Admin.PlatformAdmin";
    private const string Trial = "/api/admin/tenants/{tenantId}/trial";
    private const string Override = "/api/admin/tenants/{tenantId}/overrides/{featureKey}";

    private static bool IsOptOut(object metadata) => metadata.GetType().Name == "SkipTenantMembershipMetadata";

    /// <summary>#25 G2 D3 and G4-25-02: /api/whoami plus the three /api/admin routes, no more.</summary>
    [Fact]
    public async Task The_tenant_membership_opt_out_set_is_exactly_api_whoami_and_the_three_admin_routes()
    {
        var routes = await RoutesWithMetadataAsync(IsOptOut);

        routes.Should().BeEquivalentTo(["/api/whoami", Trial, Override, Override]);
    }

    /// <summary>G4-25-02 (T-05): the skip never spreads to a route that is not admin-only.</summary>
    [Fact]
    public async Task Every_membership_opt_out_other_than_api_whoami_requires_the_admin_policy()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var skipped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.Any(IsOptOut))
            .Where(endpoint => endpoint.RoutePattern.RawText != "/api/whoami")
            .ToList();

        skipped.Should().NotBeEmpty();
        foreach (var endpoint in skipped)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy)
                .Should().Contain(AdminPolicy, endpoint.RoutePattern.RawText);
        }
    }

    /// <summary>G4-25-02 (T-04): exactly the three admin routes, each with the policy, none anonymous.</summary>
    [Fact]
    public async Task The_admin_routes_are_exactly_the_three_and_each_requires_the_policy_and_none_is_anonymous()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var adminRoutes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/admin", StringComparison.Ordinal) == true)
            .ToList();

        adminRoutes
            .Select(endpoint => $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {endpoint.RoutePattern.RawText}")
            .Should().BeEquivalentTo([$"POST {Trial}", $"PUT {Override}", $"DELETE {Override}"]);

        foreach (var endpoint in adminRoutes)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy)
                .Should().Contain(AdminPolicy, endpoint.RoutePattern.RawText);
            endpoint.Metadata.OfType<IAllowAnonymous>().Should().BeEmpty(endpoint.RoutePattern.RawText);
            endpoint.Metadata.Any(IsOptOut).Should().BeTrue(endpoint.RoutePattern.RawText);
        }
    }

    [Fact]
    public async Task The_anonymous_endpoint_set_is_exactly_health_and_alive()
    {
        var routes = await RoutesWithMetadataAsync(metadata => metadata is IAllowAnonymous);

        routes.Should().BeEquivalentTo(["/health", "/alive"]);
    }

    [Fact]
    public async Task Api_tenancy_members_carries_the_Tenancy_Owner_policy()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var membersEndpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/tenancy/members");

        var policies = membersEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .ToList();

        policies.Should().Contain("Tenancy.Owner");
    }

    private async Task<List<string?>> RoutesWithMetadataAsync(Func<object, bool> predicate)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        return dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.Any(predicate))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
    }
}
