using System.Net;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Admin.Tests.TestSupport;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Decisya.Modules.Admin.Tests;

/// <summary>
/// Issue #25, Stories 1-4 and 7 at module level: the real Admin endpoints on an in-process TestServer, with a
/// recording <c>IEntitlementAdminCommands</c> stub and no database. The end-to-end scenarios (real Postgres,
/// real tokens) run in <c>Decisya.Api.Tests</c> and <c>Decisya.Identity.Tests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminEndpointTests
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Feature = "forecasting.scenarios";
    private const string ValidBody = """{"reason":"Design-partner pilot","expiresAt":"2030-03-31T23:59:59Z"}""";

    public static TheoryData<string, string> Verbs => new()
    {
        { "POST", $"/api/admin/tenants/{Tenant}/trial" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}" },
        { "DELETE", $"/api/admin/tenants/{Tenant}/overrides/{Feature}" },
    };

    public static TheoryData<string> Callers => new()
    {
        nameof(TestCaller.NonAdminTenantLess),
        nameof(TestCaller.TenantUser),
        nameof(TestCaller.TenantUserWithAdminFlag),
    };

    internal static Task<HttpResponseMessage> SendAsync(
        AdminTestHost host, string method, string path, string? body = null, string mediaType = "application/json", TestCaller caller = TestCaller.PlatformAdmin)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(AdminTestHost.CallerHeader, AdminTestHost.HeaderValue(caller));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }
        else if (method == "PUT")
        {
            request.Content = new StringContent(ValidBody, Encoding.UTF8, "application/json");
        }

        return host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // ---- Story 1: only a platform admin ----

    [Fact]
    public async Task A_platform_admin_passes_the_role_check()
    {
        await using var host = await AdminTestHost.StartAsync();

        foreach (var (method, path) in new[] { ("POST", $"/api/admin/tenants/{Tenant}/trial"), ("PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}"), ("DELETE", $"/api/admin/tenants/{Tenant}/overrides/{Feature}") })
        {
            var response = await SendAsync(host, method, path);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"{method} {path}");
        }

        host.Commands.Calls.Select(c => c.Action).Should().Equal("start_trial", "grant_override", "revoke_override");
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task A_caller_who_is_not_a_platform_admin_gets_403_and_no_command_runs_whatever_the_input(string callerName)
    {
        var caller = Enum.Parse<TestCaller>(callerName);
        await using var host = await AdminTestHost.StartAsync();

        // Existing tenant, malformed tenant, the all-zero GUID, an unknown and a malformed feature key, an invalid body:
        // the policy refuses first, so every input gets the same 403 and learns nothing.
        string[] tenants = [Tenant, "not-a-guid", "00000000-0000-0000-0000-000000000000"];
        string[] features = [Feature, "nosuch.feature", "Forecasting.Scenarios"];
        foreach (var tenant in tenants)
        {
            (await SendAsync(host, "POST", $"/api/admin/tenants/{tenant}/trial", caller: caller)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            foreach (var feature in features)
            {
                (await SendAsync(host, "PUT", $"/api/admin/tenants/{tenant}/overrides/{feature}", ValidBody, caller: caller)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await SendAsync(host, "PUT", $"/api/admin/tenants/{tenant}/overrides/{feature}", "{not json", caller: caller)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await SendAsync(host, "DELETE", $"/api/admin/tenants/{tenant}/overrides/{feature}", caller: caller)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
        }

        host.Commands.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task An_anonymous_call_gets_401_on_every_verb_and_no_command_runs(string method, string path)
    {
        await using var host = await AdminTestHost.StartAsync();

        var response = await SendAsync(host, method, path, caller: TestCaller.Anonymous);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Commands.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_admin_policy_applies_to_routes_added_later_under_the_same_prefix()
    {
        // The group carries the policy, so the endpoint set is exactly the three routes, each requiring it,
        // and none allows anonymous access.
        await using var host = await AdminTestHost.StartAsync();

        var routes = host.Endpoints.Endpoints.OfType<RouteEndpoint>().ToList();

        routes.Select(r => (Method: r.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single(), Pattern: r.RoutePattern.RawText))
            .Should().BeEquivalentTo(
            [
                (Method: "POST", Pattern: "/api/admin/tenants/{tenantId}/trial"),
                (Method: "PUT", Pattern: "/api/admin/tenants/{tenantId}/overrides/{featureKey}"),
                (Method: "DELETE", Pattern: "/api/admin/tenants/{tenantId}/overrides/{featureKey}"),
            ]);

        foreach (var route in routes)
        {
            route.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().Contain(a => a.Policy == AdminModule.PlatformAdminPolicy, route.RoutePattern.RawText);
            route.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull(route.RoutePattern.RawText);
        }
    }

    // ---- Stories 2-4: path parsing, bodies ignored, status mapping ----

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0x7c9e6679")]
    public async Task A_malformed_tenant_id_in_the_path_gets_400_with_tenant_invalid_on_every_verb(string tenant)
    {
        await using var host = await AdminTestHost.StartAsync();

        foreach (var (method, suffix) in new[] { ("POST", "trial"), ("PUT", $"overrides/{Feature}"), ("DELETE", $"overrides/{Feature}") })
        {
            var response = await SendAsync(host, method, $"/api/admin/tenants/{tenant}/{suffix}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{method} {tenant}");
            (await CodeOf(response)).Should().Be("entitlements.tenant_invalid");
        }

        host.Commands.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Forecasting.Scenarios")]
    [InlineData("forecasting.scenarios%20")]
    [InlineData("a.b.c")]
    [InlineData("nosuch.")]
    [InlineData("Nosuch")]
    public async Task A_feature_key_in_the_path_is_matched_exactly_and_a_malformed_one_gets_400_with_feature_unknown(string feature)
    {
        await using var host = await AdminTestHost.StartAsync();

        foreach (var method in new[] { "PUT", "DELETE" })
        {
            var response = await SendAsync(host, method, $"/api/admin/tenants/{Tenant}/overrides/{feature}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{method} {feature}");
            (await CodeOf(response)).Should().Be("entitlements.feature_unknown");
        }

        host.Commands.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_well_formed_key_that_the_catalog_does_not_list_is_passed_to_the_command_on_DELETE()
    {
        await using var host = await AdminTestHost.StartAsync();

        var response = await SendAsync(host, "DELETE", $"/api/admin/tenants/{Tenant}/overrides/nosuch.feature");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Commands.Calls.Should().ContainSingle().Which.Feature.Should().NotBeNull();
    }

    [Fact]
    public async Task A_request_body_on_the_trial_and_revoke_endpoints_is_ignored_never_read()
    {
        await using var host = await AdminTestHost.StartAsync();

        var trial = await SendAsync(host, "POST", $"/api/admin/tenants/{Tenant}/trial", "{ garbage \u0000", "text/plain");
        var revoke = await SendAsync(host, "DELETE", $"/api/admin/tenants/{Tenant}/overrides/{Feature}", """{"reason":"MARKER"}""");

        trial.StatusCode.Should().Be(HttpStatusCode.NoContent);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Commands.Calls.Should().OnlyContain(c => c.Reason == null);
    }

    [Theory]
    [InlineData(EntitlementAdminStatus.Forbidden, "entitlements.forbidden", 403, false)]
    [InlineData(EntitlementAdminStatus.Forbidden, "entitlements.actor_unknown", 403, false)]
    [InlineData(EntitlementAdminStatus.Invalid, "entitlements.reason_invalid", 400, true)]
    [InlineData(EntitlementAdminStatus.Invalid, "entitlements.expiry_not_in_future", 400, true)]
    [InlineData(EntitlementAdminStatus.NotFound, "entitlements.tenant_not_found", 404, true)]
    [InlineData(EntitlementAdminStatus.Conflict, "entitlements.trial_already_used", 409, true)]
    public async Task Each_failure_status_of_the_facade_maps_to_its_http_status_with_a_code_only_where_the_contract_says(
        EntitlementAdminStatus status, string code, int expected, bool hasCode)
    {
        await using var host = await AdminTestHost.StartAsync();
        host.Commands.Result = EntitlementAdminResult.Failed(status, code);

        foreach (var (method, path) in new[] { ("POST", $"/api/admin/tenants/{Tenant}/trial"), ("PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}"), ("DELETE", $"/api/admin/tenants/{Tenant}/overrides/{Feature}") })
        {
            var response = await SendAsync(host, method, path);

            ((int)response.StatusCode).Should().Be(expected, $"{method} {path}");
            var members = await MembersOf(response);
            members.Should().NotContain(["detail", "instance"], "a ProblemDetails never carries detail");
            (members.Contains("code")).Should().Be(hasCode);
            if (hasCode)
            {
                (await CodeOf(response)).Should().Be(code);
            }
        }
    }

    [Fact]
    public async Task A_facade_that_throws_gives_a_generic_500_with_no_code_and_no_detail()
    {
        await using var host = await AdminTestHost.StartAsync();
        host.Commands.Fault = new InvalidOperationException("The entitlement admin facade returned an undefined status.");

        foreach (var (method, path) in new[] { ("POST", $"/api/admin/tenants/{Tenant}/trial"), ("PUT", $"/api/admin/tenants/{Tenant}/overrides/{Feature}"), ("DELETE", $"/api/admin/tenants/{Tenant}/overrides/{Feature}") })
        {
            var response = await SendAsync(host, method, path);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            text.Should().NotContain("InvalidOperationException").And.NotContain("undefined status");
            (await MembersOf(response)).Should().NotContain(["code", "detail"]);
        }
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task Every_admin_response_from_the_endpoints_is_no_store(string method, string path)
    {
        await using var host = await AdminTestHost.StartAsync();

        var success = await SendAsync(host, method, path);
        var rejected = await SendAsync(host, method, path.Replace(Tenant, "not-a-guid", StringComparison.Ordinal));

        success.Headers.CacheControl?.NoStore.Should().BeTrue("a 204");
        rejected.Headers.CacheControl?.NoStore.Should().BeTrue("a 400");
    }

    // ---- helpers ----

    internal static async Task<HashSet<string>> MembersOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        using var document = JsonDocument.Parse(text);
        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    internal static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
