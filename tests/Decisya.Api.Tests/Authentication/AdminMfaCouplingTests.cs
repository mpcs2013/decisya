using Decisya.Api.Authentication;
using Decisya.Modules.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #121, G3 G4-121-01 (b): the coupling test. Every route whose pattern, compared without
/// regard to case, is <c>/api/admin</c> or starts with <c>/api/admin/</c> must carry both
/// <c>Admin.PlatformAdmin</c> and <c>Api.AdminMfa</c>. Read from the real host's endpoint metadata,
/// never from a hand-kept list; a deliberately uncoupled endpoint makes the checker fail (the red
/// rows below). No Docker.
/// </summary>
public class AdminMfaCouplingTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The checker: one line per endpoint that is an admin route and lacks a policy. Public routes are never reported.</summary>
    internal static IReadOnlyList<string> FindUncoupled(IEnumerable<Endpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!IsAdminRoute(pattern))
            {
                continue;
            }

            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();
            if (!policies.Contains(AdminModule.PlatformAdminPolicy, StringComparer.Ordinal))
            {
                violations.Add($"{pattern}: lacks {AdminModule.PlatformAdminPolicy}");
            }

            if (!policies.Contains(AdminMfaPolicy.Name, StringComparer.Ordinal))
            {
                violations.Add($"{pattern}: lacks {AdminMfaPolicy.Name}");
            }
        }

        return violations;
    }

    internal static bool IsAdminRoute(string pattern)
    {
        const string prefix = "/api/admin";
        return pattern.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (pattern.Length == prefix.Length || pattern[prefix.Length] == '/');
    }

    [Fact]
    public async Task Every_api_admin_endpoint_of_the_real_host_carries_both_policies()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var adminRoutes = endpoints.OfType<RouteEndpoint>().Where(e => IsAdminRoute(e.RoutePattern.RawText ?? string.Empty)).ToList();
        adminRoutes.Should().HaveCount(3, "a vacuous pass would hide a refactor that moved the group");
        FindUncoupled(endpoints).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/admin", true)]
    [InlineData("/api/admin/tenants/{tenantId}/trial", true)]
    [InlineData("/API/ADMIN/tenants", true)]
    [InlineData("/Api/Admin/x", true)]
    [InlineData("/api/administrators", false)]
    [InlineData("/api/adminx/y", false)]
    [InlineData("/api/whoami", false)]
    [InlineData("/health", false)]
    [InlineData("/x/api/admin", false)]
    public void The_route_match_is_case_insensitive_and_segment_anchored(string pattern, bool expected)
    {
        IsAdminRoute(pattern).Should().Be(expected);
    }

    /// <summary>The red rows: a route missing either policy, a case variant of the prefix, and the bare prefix all fail the checker.</summary>
    [Theory]
    [InlineData("/api/admin/uncoupled", new[] { "Admin.PlatformAdmin" }, 1)]
    [InlineData("/api/admin/uncoupled", new[] { "Api.AdminMfa" }, 1)]
    [InlineData("/api/admin/uncoupled", new string[0], 2)]
    [InlineData("/API/ADMIN/uncoupled", new[] { "Admin.PlatformAdmin" }, 1)]
    [InlineData("/Api/Admin", new[] { "Api.AdminMfa" }, 1)]
    [InlineData("/api/admin/coupled", new[] { "Admin.PlatformAdmin", "Api.AdminMfa" }, 0)]
    [InlineData("/api/tenancy/members", new string[0], 0)]
    public void A_deliberately_uncoupled_admin_endpoint_makes_the_checker_fail(string pattern, string[] policies, int expectedViolations)
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), order: 0);
        foreach (var policy in policies)
        {
            builder.Metadata.Add(new AuthorizeAttribute(policy));
        }

        FindUncoupled([builder.Build()]).Should().HaveCount(expectedViolations);
    }

    /// <summary>The same red row against the real endpoints: strip the MFA policy from each and the checker names them all.</summary>
    [Fact]
    public async Task Stripping_the_mfa_policy_from_the_real_admin_endpoints_is_reported()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var real = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(e => IsAdminRoute(e.RoutePattern.RawText ?? string.Empty)).ToList();

        var stripped = real.Select(endpoint =>
        {
            var builder = new RouteEndpointBuilder(endpoint.RequestDelegate!, endpoint.RoutePattern, endpoint.Order);
            foreach (var metadata in endpoint.Metadata.Where(m => m is not AuthorizeAttribute { Policy: AdminMfaPolicy.Name }))
            {
                builder.Metadata.Add(metadata);
            }

            return (Endpoint)builder.Build();
        }).ToList();

        FindUncoupled(stripped).Should().HaveCount(real.Count).And.OnlyContain(line => line.EndsWith("lacks Api.AdminMfa", StringComparison.Ordinal));
    }
}
