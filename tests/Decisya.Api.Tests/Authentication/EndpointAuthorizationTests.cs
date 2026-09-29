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

    [Fact]
    public async Task The_tenant_membership_opt_out_set_is_exactly_api_whoami()
    {
        var routes = await RoutesWithMetadataAsync(metadata => metadata.GetType().Name == "SkipTenantMembershipMetadata");

        routes.Should().BeEquivalentTo(["/api/whoami"]);
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
