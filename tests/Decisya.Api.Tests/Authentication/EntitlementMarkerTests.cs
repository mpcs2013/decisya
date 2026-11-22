using Decisya.Api.Authorization;
using Decisya.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
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

    // ---- G6-341-03: M341-01 (c) and R-L13 ----

    /// <summary>
    /// M341-01 (c): a group policy replaces the fallback's requirements, so the combined policy of every ledger
    /// endpoint must hold every requirement type, every claim requirement and every scheme of the live fallback,
    /// read at run time from <see cref="IAuthorizationPolicyProvider.GetFallbackPolicyAsync"/>, plus the
    /// entitlement requirement. Removing the <c>Combine</c> in <c>AddEntitlementPolicies</c> drops the schemes.
    /// </summary>
    [Fact]
    public async Task Every_ledger_endpoints_combined_policy_holds_the_live_fallback_policy_and_the_entitlement_requirement()
    {
        var (fallback, resolved) = await ServicesAsync("/api/ledger");

        fallback.AuthenticationSchemes.Should().NotBeEmpty("the live fallback names its scheme, so a dropped Combine is visible here");
        resolved.Should().HaveCount(5, "the five ledger operations");
        foreach (var (endpoint, combined) in resolved)
        {
            combined.Should().NotBeNull(Describe(endpoint));
            AssertHoldsFallback(combined!, fallback, requireSchemes: true, Describe(endpoint));
            combined!.Requirements.OfType<EntitlementRequirement>().Should().ContainSingle(Describe(endpoint));
        }
    }

    /// <summary>
    /// The same check for <c>Api.AdminMfa</c> (its policy copies the fallback's requirements by hand): the combined
    /// policy of every admin endpoint holds every requirement type and claim of the live fallback plus the MFA requirement.
    /// The hand-built policies name no scheme, so the Bearer scheme reaches them as the one default scheme
    /// (<c>AuthenticationSchemeSetTests</c>); the schemes are therefore not required here, but none may differ from the fallback's.
    /// </summary>
    [Fact]
    public async Task Every_admin_endpoints_combined_policy_holds_the_live_fallback_policy_and_the_MFA_requirement()
    {
        var (fallback, resolved) = await ServicesAsync("/api/admin");

        resolved.Should().NotBeEmpty();
        foreach (var (endpoint, combined) in resolved)
        {
            combined.Should().NotBeNull(Describe(endpoint));
            AssertHoldsFallback(combined!, fallback, requireSchemes: false, Describe(endpoint));
            combined!.Requirements.OfType<Decisya.Api.Authentication.AdminMfaRequirement>().Should().ContainSingle(Describe(endpoint));
        }
    }

    [Fact]
    public async Task Every_ledger_write_carries_the_owner_policy_and_no_ledger_GET_does()
    {
        var (_, resolved) = await ServicesAsync("/api/ledger");
        var endpoints = resolved.Select(r => r.Endpoint).ToList();

        endpoints.Should().HaveCount(5);
        foreach (var endpoint in endpoints)
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            endpoint.Metadata.Any(m => m.GetType().Name == "SkipTenantMembershipMetadata")
                .Should().BeFalse($"{Describe(endpoint)}: the membership gate runs for every ledger endpoint (R-L13)");
            endpoint.Metadata.OfType<NoEntitlementRequiredAttribute>()
                .Should().BeEmpty($"{Describe(endpoint)}: a ledger endpoint is gated by its entitlement policy (R-L13)");
            var carriesOwner = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => string.Equals(data.Policy, Decisya.Modules.Ledger.LedgerModule.OwnerPolicy, StringComparison.Ordinal));

            if (HttpMethods.IsGet(method))
            {
                carriesOwner.Should().BeFalse($"{Describe(endpoint)}: members read (R-L13)");
            }
            else
            {
                carriesOwner.Should().BeTrue($"{Describe(endpoint)}: every ledger write needs the owner policy (R-L13)");
            }
        }

        endpoints.Count(e => HttpMethods.IsGet(e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single())).Should().Be(2);
    }

    private static void AssertHoldsFallback(AuthorizationPolicy combined, AuthorizationPolicy fallback, bool requireSchemes, string because)
    {
        foreach (var requirement in fallback.Requirements)
        {
            if (requirement is ClaimsAuthorizationRequirement claim)
            {
                combined.Requirements.OfType<ClaimsAuthorizationRequirement>()
                    .Should().Contain(c => c.ClaimType == claim.ClaimType, $"{because}: claim requirement {claim.ClaimType}");
            }
            else
            {
                combined.Requirements.Should().Contain(r => r.GetType() == requirement.GetType(), $"{because}: {requirement.GetType().Name}");
            }
        }

        if (requireSchemes)
        {
            combined.AuthenticationSchemes.Should().Contain(fallback.AuthenticationSchemes, because);
        }
        else
        {
            combined.AuthenticationSchemes.Should().BeSubsetOf(fallback.AuthenticationSchemes, because);
        }
    }

    private async Task<(AuthorizationPolicy Fallback, List<(RouteEndpoint Endpoint, AuthorizationPolicy? Combined)> Resolved)> ServicesAsync(string prefix)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var policies = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallback = await policies.GetFallbackPolicyAsync();
        fallback.Should().NotBeNull();

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } text
                && (text.Equals(prefix, StringComparison.OrdinalIgnoreCase) || text.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var resolved = new List<(RouteEndpoint, AuthorizationPolicy?)>();
        foreach (var endpoint in endpoints)
        {
            resolved.Add((endpoint, await AuthorizationPolicy.CombineAsync(policies, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())));
        }

        return (fallback!, resolved);
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
