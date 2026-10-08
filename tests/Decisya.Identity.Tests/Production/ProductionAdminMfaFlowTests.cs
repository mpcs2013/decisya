using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G3 G4-121-01 (c) at the Keycloak level, against the production realm (not the
/// spike's): which <c>acr</c> an admin and a tenant user get, and what the BFF's
/// <c>acr_values=2</c> changes. The Api half (a token with <c>acr</c> "2" is accepted by
/// <c>/api/admin</c>, one without it is refused) is in <c>Decisya.Api.Tests</c>, driven by this
/// same stack class.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ProductionKeycloakDefinition.Name)]
public sealed class ProductionAdminMfaFlowTests
{
    private readonly ProductionKeycloak _stack;

    public ProductionAdminMfaFlowTests(ProductionKeycloak stack)
    {
        _stack = stack;
    }

    [Fact]
    public async Task An_admin_with_a_totp_credential_gets_exactly_one_string_acr_2()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var admin = await _stack.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: true, ct);

        var login = await _stack.LoginAsync(admin.Username, admin.Password, "2", admin.TotpSecret, ct);
        var tokens = await _stack.ExchangeAsync(login, ct);

        var acr = ProductionKeycloak.ReadAcr(tokens.AccessToken);
        ProductionKeycloak.Record($"MFA admin+TOTP acr_values=2: otpPrompt={login.OtpPromptShown} acrCount={acr.Count} acr={acr.Value}");
        login.OtpPromptShown.Should().BeTrue();
        acr.Count.Should().Be(1);
        acr.Kind.Should().Be(JsonValueKind.String);
        acr.Value.Should().Be("2");
        ProductionKeycloak.ReadAcr(tokens.IdToken).Value.Should().Be("2");
    }

    [Fact]
    public async Task A_tenant_user_who_requests_acr_values_2_gets_acr_1_and_no_otp_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var tenant = await _stack.CreateUserAsync(ProductionKeycloak.TenantRole, withTotp: false, ct);

        var login = await _stack.LoginAsync(tenant.Username, tenant.Password, "2", null, ct);
        var tokens = await _stack.ExchangeAsync(login, ct);

        var acr = ProductionKeycloak.ReadAcr(tokens.AccessToken);
        ProductionKeycloak.Record($"MFA tenant acr_values=2: otpPrompt={login.OtpPromptShown} configureTotp={login.ConfigureTotpShown} acr={acr.Value}");
        login.OtpPromptShown.Should().BeFalse();
        login.ConfigureTotpShown.Should().BeFalse();
        acr.Count.Should().Be(1);
        acr.Value.Should().Be("1");
    }

    /// <summary>An admin who has no OTP credential yet cannot get a code with the password alone.</summary>
    [Fact]
    public async Task An_admin_without_a_totp_credential_cannot_get_a_code_before_enrolling_then_gets_acr_2()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var admin = await _stack.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: false, ct);

        var stopped = await _stack.LoginAsync(admin.Username, admin.Password, "2", null, ct, stopAtConfigureTotp: true);
        stopped.ConfigureTotpShown.Should().BeTrue("CONFIGURE_TOTP comes before any code is issued");
        stopped.Code.Should().BeEmpty("password alone must not yield an authorization code for an admin");

        var login = await _stack.LoginAsync(admin.Username, admin.Password, "2", null, ct);
        login.ConfigureTotpShown.Should().BeTrue();
        var acr = ProductionKeycloak.ReadAcr((await _stack.ExchangeAsync(login, ct)).AccessToken);
        acr.Value.Should().Be("2");
        acr.Count.Should().Be(1);
    }

    /// <summary>Why the BFF parameter is load-bearing: without acr_values an admin who types the OTP is not asked for it at all.</summary>
    [Fact]
    public async Task Without_acr_values_an_admin_gets_acr_1_so_the_bff_parameter_is_load_bearing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var admin = await _stack.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: true, ct);

        var login = await _stack.LoginAsync(admin.Username, admin.Password, null, admin.TotpSecret, ct);
        var acr = ProductionKeycloak.ReadAcr((await _stack.ExchangeAsync(login, ct)).AccessToken);

        ProductionKeycloak.Record($"MFA admin+TOTP NO acr_values: otpPrompt={login.OtpPromptShown} acr={acr.Value}");
        acr.Value.Should().Be("1");
        login.OtpPromptShown.Should().BeFalse();
    }

    [Fact]
    public async Task The_admin_acr_survives_refresh_of_the_access_token()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var admin = await _stack.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: true, ct);

        var login = await _stack.LoginAsync(admin.Username, admin.Password, "2", admin.TotpSecret, ct);
        var tokens = await _stack.ExchangeAsync(login, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/realms/{ProductionKeycloak.Realm}/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{ProductionKeycloak.ClientId}:{_stack.ClientSecret}")));
        using var response = await login.Client.SendAsync(request, ct);
        response.IsSuccessStatusCode.Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var refreshed = document.RootElement.GetProperty("access_token").GetString()!;

        ProductionKeycloak.ReadAcr(refreshed).Value.Should().Be("2");
    }
}
