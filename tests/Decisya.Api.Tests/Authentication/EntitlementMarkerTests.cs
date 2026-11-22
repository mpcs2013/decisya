using Decisya.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// ADR-0008 amendment 1 / #26 G2 D3: every <c>/api</c> endpoint of the real host is either gated by
/// an entitlement policy or carries <see cref="NoEntitlementRequiredAttribute"/>. Since #341 the ledger
/// group is gated by the <c>Entitlement:ledger.transactions</c> policy; every other endpoint carries the
/// marker, and the marked set is exactly the seven endpoints of D3.
/// </summary>
[Trait("Category", "Unit")]
public class EntitlementMarkerTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Trial = "/api/admin/tenants/{tenantId}/trial";
    private const string Override = "/api/admin/tenants/{tenantId}/overrides/{featureKey}";

    [Fact]
    public async Task The_marked_set_is_exactly_the_seven_api_endpoints()
    {
        var endpoints = await ApiEndpointsAsync();

        endpoints.Where(endpoint => endpoint.Metadata.OfType<NoEntitlementRequiredAttribute>().Any())
            .Select(Describe)
            .Should().BeEquivalentTo(
            [
                "GET /api/whoami",
                "GET /api/tenancy/me",
                "GET /api/tenancy/members",
                $"POST {Trial}",
                $"PUT {Override}",
                $"DELETE {Override}",
                "GET /api/capabilities",
            ]);
    }

    [Fact]
    public async Task Every_api_endpoint_carries_the_marker_or_an_entitlement_policy_and_none_is_anonymous()
    {
        var endpoints = await ApiEndpointsAsync();

        endpoints.Should().NotBeEmpty();
        foreach (var endpoint in endpoints)
        {
            var description = Describe(endpoint);
            endpoint.Metadata.OfType<IAllowAnonymous>().Should().BeEmpty(description);

            // #341 G2 D4: the ledger group is gated by an Entitlement:<key> policy; every other endpoint carries the marker.
            var hasMarker = endpoint.Metadata.OfType<NoEntitlementRequiredAttribute>().Any();
            var hasEntitlementPolicy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy?.StartsWith("Entitlement:", StringComparison.Ordinal) == true);
            (hasMarker || hasEntitlementPolicy).Should().BeTrue(
                $"{description} must carry NoEntitlementRequiredAttribute or an Entitlement:<key> policy");
        }
    }

    [Fact]
    public async Task Api_capabilities_uses_the_fallback_policy_and_the_membership_gate()
    {
        var endpoints = await ApiEndpointsAsync();

        var capabilities = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/capabilities");

        // No RequireAuthorization(): that would replace the fallback policy (which requires sub).
        capabilities.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().BeEmpty();
        capabilities.Metadata.Any(metadata => metadata.GetType().Name == "SkipTenantMembershipMetadata")
            .Should().BeFalse("the membership gate must run for tenant callers");
    }

    private async Task<List<RouteEndpoint>> ApiEndpointsAsync()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .ToList();
    }

    private static string Describe(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {endpoint.RoutePattern.RawText}";
}
