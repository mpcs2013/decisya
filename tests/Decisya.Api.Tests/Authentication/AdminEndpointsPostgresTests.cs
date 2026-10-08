using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Tenancy.Contracts;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #25, Stories 2-5 and NFR-39, end to end: the real Api host (real token validation, caller context, the
/// admin policy, the Admin endpoints, the Entitlements handlers and the Audit writer) against a real, fully
/// migrated Postgres 18 database. Every test gets its own database, so row counts are exact. The API connects
/// as the database owner here; the least-privilege roles are covered by the module and Migrator tests.
/// Test names are the G1 scenario titles verbatim.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminEndpointsPostgresTests : IDisposable
{
    private const string AdminSub = "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59";
    private const string Feature = "forecasting.scenarios";
    private static readonly Guid TenantAId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid TenantBId = Guid.Parse("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b");
    private static readonly Guid UnknownTenantId = Guid.Parse("9d4b7e21-6c3f-4a58-b0e2-5f1a8c7d3b94");

    private readonly TenancyDatabaseFixture _database;
    private readonly TestTokenIssuer _issuer = new();

    public AdminEndpointsPostgresTests(TenancyDatabaseFixture database)
    {
        _database = database;
    }

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string> RejectedGrants => new()
    {
        { "nosuch.feature", """{"reason":"Pilot"}""" },
        { Feature, """{"reason":""}""" },
        { Feature, """{"reason":"   "}""" },
        { Feature, "{\"reason\":\"" + new string('x', 501) + "\"}" },
        { Feature, """{"reason":"Pilot","expiresAt":"2020-01-01T00:00:00Z"}""" },
    };

    public static TheoryData<string> MalformedKeys => new()
    {
        "Forecasting.Scenarios",
        "forecasting",
        "forecasting.scenarios.extra",
        ".scenarios",
        "1forecasting.scenarios",
        "forecasting.scenarios%20",
        "a." + "bbbb" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
    };

    // ---- Story 2: start a trial ----

    [Fact]
    public async Task Starting_a_trial_succeeds_and_is_audited()
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("POST", $"/api/admin/tenants/{TenantAId}/trial");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @t AND plan = 'Pro' AND ends_at - starts_at = interval '14 days'", TenantAId)).Should().Be(1);

        var audit = await stack.AuditRowsAsync(TenantAId);
        var row = audit.Should().ContainSingle().Which;
        row.GetProperty("action").GetString().Should().Be("entitlements.trial.start");
        row.GetProperty("outcome").GetString().Should().Be("succeeded");
        row.GetProperty("actor_user_id").GetString().Should().Be(AdminSub);
        row.GetProperty("feature_key").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_second_start_is_refused_with_409_and_writes_nothing()
    {
        await using var stack = await StartAsync();
        (await stack.SendAsync("POST", $"/api/admin/tenants/{TenantAId}/trial")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var second = await stack.SendAsync("POST", $"/api/admin/tenants/{TenantAId}/trial");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(second)).Should().Be("entitlements.trial_already_used");
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @t", TenantAId)).Should().Be(1);
        (await stack.AuditRowsAsync(TenantAId)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_malformed_tenant_id_in_the_path_gets_400(string tenant)
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("POST", $"/api/admin/tenants/{tenant}/trial");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("entitlements.tenant_invalid");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants")).Should().Be(0);
    }

    [Fact]
    public async Task A_request_body_on_the_trial_endpoint_is_ignored_never_stored()
    {
        await using var stack = await StartAsync();
        var body = $"{{\"reason\":\"MARKER-9f3a\",\"tenantId\":\"{TenantBId}\"}}";

        using var response = await stack.SendAsync("POST", $"/api/admin/tenants/{TenantAId}/trial", body);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @t", TenantAId)).Should().Be(1);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @t", TenantBId)).Should().Be(0);
        (await stack.AuditRowsAsync(TenantBId)).Should().BeEmpty();
    }

    // ---- Story 3: grant or replace an override ----

    [Fact]
    public async Task Granting_an_override_succeeds_and_is_audited_the_Done_when()
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync(
            "PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":"Design-partner pilot","expiresAt":"2099-12-31T23:59:59Z"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t AND feature_key = @k AND expires_at IS NOT NULL", TenantAId, Feature)).Should().Be(1);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t", TenantBId)).Should().Be(0, "tenant B is denied it");

        var row = (await stack.AuditRowsAsync(TenantAId)).Should().ContainSingle().Which;
        row.GetProperty("action").GetString().Should().Be("entitlements.override.grant");
        row.GetProperty("feature_key").GetString().Should().Be(Feature);
        row.GetProperty("outcome").GetString().Should().Be("succeeded");
        row.GetProperty("actor_user_id").GetString().Should().Be(AdminSub);
    }

    [Fact]
    public async Task An_override_without_an_expiry_lasts_until_revoked()
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":"Design-partner pilot"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t AND expires_at IS NULL", TenantAId)).Should().Be(1);
    }

    [Fact]
    public async Task The_same_PUT_again_replaces_the_override_and_writes_a_further_record()
    {
        await using var stack = await StartAsync();
        var path = $"/api/admin/tenants/{TenantAId}/overrides/{Feature}";
        (await stack.SendAsync("PUT", path, """{"reason":"Design-partner pilot"}""")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var again = await stack.SendAsync("PUT", path, """{"reason":"Pilot extended","expiresAt":"2099-03-31T23:59:59Z"}""");

        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t AND feature_key = @k AND reason = 'Pilot extended'", TenantAId, Feature)).Should().Be(1);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t", TenantAId)).Should().Be(1);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE tenant_id = @t AND action = 'entitlements.override.grant'", TenantAId)).Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(RejectedGrants))]
    public async Task A_request_the_command_rejects_gets_400_with_a_stable_code_and_writes_nothing(string feature, string body)
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{feature}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().BeOneOf(
            "entitlements.feature_unknown", "entitlements.reason_invalid", "entitlements.expiry_not_in_future");
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    // ---- Story 4: revoke ----

    [Fact]
    public async Task Revoking_an_override_succeeds_and_is_audited()
    {
        await using var stack = await StartAsync();
        var path = $"/api/admin/tenants/{TenantAId}/overrides/{Feature}";
        (await stack.SendAsync("PUT", path, """{"reason":"pilot"}""")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var response = await stack.SendAsync("DELETE", path);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t", TenantAId)).Should().Be(0);
        var revokes = (await stack.AuditRowsAsync(TenantAId)).Where(r => r.GetProperty("action").GetString() == "entitlements.override.revoke").ToList();
        var revoke = revokes.Should().ContainSingle().Which;
        revoke.GetProperty("feature_key").GetString().Should().Be(Feature);
        revoke.GetProperty("outcome").GetString().Should().Be("succeeded");
    }

    [Fact]
    public async Task Revoking_a_feature_with_no_override_is_idempotent_and_still_audited()
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE tenant_id = @t AND action = 'entitlements.override.revoke'", TenantAId)).Should().Be(1);
    }

    [Fact]
    public async Task Revoking_for_tenant_A_never_touches_tenant_Bs_override()
    {
        await using var stack = await StartAsync();
        (await stack.SendAsync("PUT", $"/api/admin/tenants/{TenantBId}/overrides/{Feature}", """{"reason":"pilot B"}""")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var auditBefore = await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE tenant_id = @t", TenantBId);

        (await stack.SendAsync("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @t", TenantBId)).Should().Be(1);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE tenant_id = @t", TenantBId)).Should().Be(auditBefore);
    }

    [Fact]
    public async Task A_well_formed_key_that_the_catalog_does_not_list_is_accepted_and_audited()
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/nosuch.feature");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE tenant_id = @t AND action = 'entitlements.override.revoke' AND feature_key = 'nosuch.feature'", TenantAId)).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public async Task A_malformed_key_gets_400_and_writes_nothing(string key)
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{key}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("entitlements.feature_unknown");
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    // ---- Story 5: the target tenant exists ----

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task An_unknown_tenant_gets_404_and_nothing_is_written(string method)
    {
        await using var stack = await StartAsync();
        var path = method == "POST"
            ? $"/api/admin/tenants/{UnknownTenantId}/trial"
            : $"/api/admin/tenants/{UnknownTenantId}/overrides/{Feature}";

        using var response = await stack.SendAsync(method, path, method == "PUT" ? """{"reason":"pilot"}""" : null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(response)).Should().Be("entitlements.tenant_not_found");
        (await MembersOf(response)).Should().BeEquivalentTo(["type", "title", "status", "traceId", "code"]);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    [Fact]
    public async Task Validation_errors_come_before_the_existence_check()
    {
        await using var stack = await StartAsync();

        using var response = await stack.SendAsync("PUT", $"/api/admin/tenants/{UnknownTenantId}/overrides/{Feature}", """{"reason":""}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("entitlements.reason_invalid");
    }

    [Fact]
    public async Task An_existing_tenant_passes_the_check()
    {
        await using var stack = await StartAsync();

        (await stack.SendAsync("POST", $"/api/admin/tenants/{TenantAId}/trial")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_tenant_that_exists_but_another_tenants_id_is_guessed()
    {
        await using var stack = await StartAsync();

        (await stack.SendAsync("POST", $"/api/admin/tenants/{TenantBId}/trial")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @t", TenantBId)).Should().Be(1);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @t", TenantAId)).Should().Be(0);
    }

    [Fact]
    public async Task A_database_error_in_the_existence_check_is_a_500_never_not_found_and_never_found()
    {
        await using var stack = await StartAsync(configureServices: services =>
            services.AddScoped<ITenantExistence>(_ => new ThrowingTenantExistence()));

        foreach (var (method, path, body) in new (string, string, string?)[]
        {
            ("POST", $"/api/admin/tenants/{TenantAId}/trial", null),
            ("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":"pilot"}"""),
            ("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", null),
        })
        {
            using var response = await stack.SendAsync(method, path, body);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, $"{method} {path}");
            (await MembersOf(response)).Should().NotContain(["code", "detail"]);
        }

        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    // ---- NFR-39 ----

    [Fact]
    public async Task Every_2xx_admin_response_has_exactly_one_audit_record_and_every_non_2xx_none()
    {
        await using var stack = await StartAsync();
        (string Method, string Path, string? Body)[] calls =
        [
            ("POST", $"/api/admin/tenants/{TenantAId}/trial", null),                                  // 204
            ("POST", $"/api/admin/tenants/{TenantAId}/trial", null),                                  // 409
            ("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":"a"}"""),      // 204
            ("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":"b"}"""),      // 204 (replace)
            ("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":""}"""),       // 400
            ("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", "{not json"),               // 400
            ("PUT", $"/api/admin/tenants/{UnknownTenantId}/overrides/{Feature}", """{"reason":"a"}"""), // 404
            ("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", null),                   // 204
            ("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", null),                   // 204 (idempotent)
            ("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/Bad%20Key", null),                    // 400
            ("POST", "/api/admin/tenants/not-a-guid/trial", null),                                     // 400
            ("POST", $"/api/admin/tenants/{UnknownTenantId}/trial", null),                             // 404
        ];

        var successes = 0;
        foreach (var (method, path, body) in calls)
        {
            using var response = await stack.SendAsync(method, path, body);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                successes++;
            }
        }

        successes.Should().Be(5);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(successes);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records WHERE actor_user_id = @a", AdminSub)).Should().Be(successes);
    }

    /// <summary>
    /// Issue #121, Story 3 / NFR-44: a platform-admin token with no MFA proof (no <c>acr</c>, or level "1") is
    /// refused with 403 on every admin verb, and the database holds no trial, no override and no audit record.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public async Task An_admin_token_without_the_MFA_level_gets_403_and_writes_no_row_and_no_audit_record(string? acr)
    {
        await using var stack = await StartAsync(acr: acr);
        (string Method, string Path, string? Body)[] calls =
        [
            ("POST", $"/api/admin/tenants/{TenantAId}/trial", null),
            ("PUT", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", """{"reason":"a"}"""),
            ("DELETE", $"/api/admin/tenants/{TenantAId}/overrides/{Feature}", null),
        ];

        foreach (var (method, path, body) in calls)
        {
            using var response = await stack.SendAsync(method, path, body);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
            (await CodeOf(response)).Should().BeNull("the refusal carries no code and no mention of MFA");
        }

        (await stack.CountAsync("SELECT count(*) FROM entitlements.trial_grants")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM entitlements.feature_overrides")).Should().Be(0);
        (await stack.CountAsync("SELECT count(*) FROM audit.audit_records")).Should().Be(0);
    }

    [Fact]
    public async Task The_reason_is_never_echoed_or_logged()
    {
        const string email = "anna.meier@example.com";
        const string iban = "DE89370400440532013000";
        var reason = $"MARKER-9f3a-{Guid.NewGuid():N} Contact: {email}, IBAN {iban}";
        string[] canaries = ["MARKER-9f3a", email, iban];
        var logs = new CapturingLoggerProvider();
        await using var stack = await StartAsync(configureLogging: builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            builder.AddProvider(logs);
        });
        var seen = new List<string>();
        var path = $"/api/admin/tenants/{TenantAId}/overrides/{Feature}";

        async Task SendAsync(string method, string url, string? body)
        {
            using var response = await stack.SendAsync(method, url, body);
            seen.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            seen.AddRange(response.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        }

        string Json(string tail) => "{\"reason\":\"" + reason + "\"" + tail + "}";

        await SendAsync("PUT", path, Json(string.Empty));                                    // success
        await SendAsync("PUT", path, Json(",\"expiresAt\":\"2020-01-01T00:00:00Z\""));        // command rejects
        await SendAsync("PUT", path, Json(",\"unknown\":1"));                                 // schema failure
        await SendAsync("PUT", path, "{\"reason\":\"" + reason);                              // malformed
        await SendAsync("PUT", $"/api/admin/tenants/{UnknownTenantId}/overrides/{Feature}", Json(string.Empty)); // 404

        // A real database failure on the write: a check constraint the reason trips. Npgsql's server Detail would
        // echo the failing row (including the reason) unless it is redacted.
        await SendAsync("DELETE", path, null); // the earlier success left a row carrying the marker; the constraint needs a clean table
        await stack.ExecuteAsync(
            "ALTER TABLE entitlements.feature_overrides ADD CONSTRAINT ck_test_no_marker CHECK (reason NOT LIKE '%MARKER-9f3a%')");
        using (var failure = await stack.SendAsync("PUT", path, Json(string.Empty)))
        {
            failure.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            seen.Add(await failure.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        logs.Records.Should().NotBeEmpty();
        logs.Records.Should().Contain(r => r.Level >= LogLevel.Error, "the forced database failure was logged");
        foreach (var canary in canaries)
        {
            seen.Should().NotContain(s => s.Contains(canary, StringComparison.Ordinal), $"no response may contain {canary}");
            logs.Records.Should().NotContain(r => r.Contains(canary), $"no log record at Trace may contain {canary}");
        }

        foreach (var row in await stack.AuditRowsAsync(TenantAId))
        {
            canaries.Should().NotContain(c => row.GetRawText().Contains(c, StringComparison.Ordinal), "no audit column holds the reason");
        }
    }

    // ---- harness ----

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<List<string>> MembersOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private async Task<Stack> StartAsync(
        Action<IServiceCollection>? configureServices = null, Action<ILoggingBuilder>? configureLogging = null, string? acr = "2")
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await _database.CreateFullyMigratedDatabaseAsync(ct);

        foreach (var tenant in new[] { TenantAId, TenantBId })
        {
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TenancyDbContext>();
            TenancyDbContextOptions.Configure(options, connectionString);
            var id = TenantId.From(tenant);
            await using var db = new TenancyDbContext(options.Options, new TestCurrentTenant { Resolution = TenantResolution.For(id) });
            db.Tenants.Add(new Tenant(id, SystemClock.Instance.GetCurrentInstant()));
            await db.SaveChangesAsync(ct);
        }

        var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            extraConfiguration:
            [
                new("ConnectionStrings:tenancy", connectionString),
                new("ConnectionStrings:entitlements", connectionString),
            ],
            configureServices: configureServices,
            configureLogging: configureLogging);

        var claims = TestTokenIssuer.DefaultClaims(subject: AdminSub, tenantId: null);
        claims["roles"] = new[] { "platform-admin" };
        if (acr is not null)
        {
            claims["acr"] = acr; // #121: the MFA proof Keycloak's step-up flow gives an admin after the OTP.
        }
        var token = TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        return new Stack(factory, factory.CreateClient(), connectionString, token);
    }

    private sealed class Stack(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, HttpClient client, string connectionString, string token) : IAsyncDisposable
    {
        public async Task<HttpResponseMessage> SendAsync(string method, string path, string? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<long> CountAsync(string sql, params object[] parameters)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = Build(connection, sql, parameters);
            return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = Build(connection, sql, []);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Every audit record of one tenant, each as the JSON of the whole row (every column).</summary>
        public async Task<List<JsonElement>> AuditRowsAsync(Guid tenant)
        {
            var rows = new List<JsonElement>();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = Build(connection, "SELECT row_to_json(r)::text FROM audit.audit_records r WHERE r.tenant_id = @p0 ORDER BY r.id", [tenant]);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
            }

            return rows;
        }

#pragma warning disable CA2100 // every call site passes a fixed literal.
        private static NpgsqlCommand Build(NpgsqlConnection connection, string sql, object[] parameters)
        {
            var command = new NpgsqlCommand(sql, connection);
            for (var i = 0; i < parameters.Length; i++)
            {
                // The first parameter is @t, @a, @p0 or @k by position; named parameters used in the literals above.
                command.Parameters.AddWithValue(NameFor(sql, i), parameters[i]);
            }

            return command;
        }
#pragma warning restore CA2100

        private static string NameFor(string sql, int index)
        {
            string[] names = ["t", "k"];
            if (sql.Contains("@a", StringComparison.Ordinal) && index == 0)
            {
                return "a";
            }

            if (sql.Contains("@p0", StringComparison.Ordinal))
            {
                return $"p{index}";
            }

            return names[index];
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync();
        }
    }

    private sealed class ThrowingTenantExistence : ITenantExistence
    {
        public Task<bool> ExistsAsync(TenantResolution target, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected fault in the existence check.");
    }
}
