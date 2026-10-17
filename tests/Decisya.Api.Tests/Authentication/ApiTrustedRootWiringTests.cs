using Decisya.TestSupport.Tls;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #164 Story 6 / stack contract (#120 G4-120-05): the API's back-channel trust goes through
/// <c>Ostium.Backchannel.BackchannelRoot</c> and the key <c>Api:Jwt:TrustedRootPath</c> is still read. A
/// bad root file stops start-up and the message names the key and never the path; a good root makes the
/// Bearer scheme's back-channel client trust a server signed by it and no other.
/// </summary>
[Trait("Category", "Unit")]
public class ApiTrustedRootWiringTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "decisya-api-trust-" + Guid.NewGuid().ToString("N"));

    public ApiTrustedRootWiringTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _issuer.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover folder of public test certificates is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> BadFiles => new(BadTrustedRootFiles.All);

    [Fact]
    public void The_Api_trust_goes_through_the_Ostium_Backchannel_package()
    {
        var jwtAssembly = typeof(Ostium.Authentication.Jwt.ApiJwtOptions).Assembly;

        jwtAssembly.GetReferencedAssemblies().Select(a => a.Name).Should().Contain("Ostium.Backchannel");
        typeof(Ostium.Backchannel.BackchannelRoot).Assembly.GetName().Name.Should().Be("Ostium.Backchannel");
        typeof(Program).Assembly.GetTypes().Select(t => t.Name).Should().NotContain("BackchannelRoot", "Decisya keeps no copy of the type");
    }

    [Theory]
    [MemberData(nameof(BadFiles), DisableDiscoveryEnumeration = true)]
    public async Task A_bad_trusted_root_file_stops_start_up_naming_the_key_and_never_the_path(string kind)
    {
        var path = BadTrustedRootFiles.Write(kind, _folder);
        var configuration = ApiStartHarness.ValidConfiguration();
        configuration[ApiStartHarness.TrustedRootPathKey] = path;

        var failures = await ApiStartHarness.StartAndCollectFailuresAsync(configuration);

        failures.Should().NotBeEmpty($"a {kind} trusted-root file must stop start-up");
        var message = string.Join(" | ", failures);
        message.Should().Contain(ApiStartHarness.TrustedRootPathKey);
        message.Should().NotContain(path, "the message never carries the configured path");
    }

    [Fact]
    public async Task With_no_trusted_root_configured_the_host_starts_on_system_trust()
    {
        var failures = await ApiStartHarness.StartAndCollectFailuresAsync(ApiStartHarness.ValidConfiguration());

        failures.Should().BeEmpty();
    }

    // S-164-01 (V12.3.2): host name validation is kept; a certificate from the pinned root for another name is refused.
    [Fact]
    public async Task A_server_certificate_from_the_pinned_root_for_another_host_name_is_rejected()
    {
        using var trusted = TlsTestPki.Create("decisya-api-trust-a");
        var rootPath = Path.Combine(_folder, "root-name.crt");
        await File.WriteAllTextAsync(rootPath, trusted.RootPem, TestContext.Current.CancellationToken);

        using var wrongName = trusted.IssueServerCertificate(["other.example.test"], includeLoopbackIp: false);
        await using var server = await TlsStubServer.StartAsync(wrongName);

        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new(ApiStartHarness.TrustedRootPathKey, rootPath)]);
        using var scope = factory.Services.CreateScope();
        var backchannel = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).Backchannel;

        var failure = await Record.ExceptionAsync(
            () => backchannel.GetStringAsync(server.Address, TestContext.Current.CancellationToken));

        failure.Should().BeOfType<HttpRequestException>("the chain is trusted but the name does not match");
    }

    [Fact]
    public async Task A_good_root_makes_the_back_channel_trust_a_server_signed_by_it_and_no_other()
    {
        using var trusted = TlsTestPki.Create("decisya-api-trust-a");
        using var other = TlsTestPki.Create("decisya-api-trust-b");
        var rootPath = Path.Combine(_folder, "root.crt");
        await File.WriteAllTextAsync(rootPath, trusted.RootPem, TestContext.Current.CancellationToken);

        using var trustedCertificate = trusted.IssueServerCertificate(["localhost"]);
        using var otherCertificate = other.IssueServerCertificate(["localhost"]);
        await using var trustedServer = await TlsStubServer.StartAsync(trustedCertificate);
        await using var otherServer = await TlsStubServer.StartAsync(otherCertificate);

        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new(ApiStartHarness.TrustedRootPathKey, rootPath)]);
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.Backchannel.Should().NotBeNull("the scheme's back-channel client is built from the pinned handler");
        var backchannel = options.Backchannel!;

        (await backchannel.GetStringAsync(trustedServer.Address, TestContext.Current.CancellationToken)).Should().Be("stub");

        var untrusted = await Record.ExceptionAsync(
            () => backchannel.GetStringAsync(otherServer.Address, TestContext.Current.CancellationToken));
        untrusted.Should().BeOfType<HttpRequestException>("a chain from any other root is refused in the handshake");
    }
}
