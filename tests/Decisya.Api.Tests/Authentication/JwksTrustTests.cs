using System.Net;
using Decisya.Api.Authentication;
using Decisya.TestSupport.Tls;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #120 G4-120-05 (B-3): the Api's JwtBearer back channel (OIDC discovery and JWKS) trusts the
/// mounted root and nothing else. The real host is built, <c>JwtBearerOptions.Backchannel</c> is
/// taken from the real service provider, and it talks to an in-process Kestrel HTTPS server with
/// a generated certificate. Start-up must fail for an unusable root file.
/// </summary>
public sealed class JwksTrustTests : IDisposable
{
    private const string RootPathKey = "Api:Jwt:TrustedRootPath";

    private readonly TestTokenIssuer _issuer = new();

    public static IEnumerable<object[]> BadFileKinds() => BadTrustedRootFiles.All.Select(kind => new object[] { kind });

    [Fact]
    public async Task The_mounted_root_is_trusted()
    {
        using var folder = new TempDirectory();
        using var pki = TlsTestPki.Create("mounted-root");
        using var serverCertificate = pki.IssueServerCertificate(["localhost"]);
        await using var server = await TlsStubServer.StartAsync(serverCertificate);
        using var factory = CreateFactory(folder.WriteFile("root.crt", pki.RootPem));

        var status = await GetStatusAsync(factory.Services, server.Address);

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_foreign_root_is_rejected()
    {
        using var folder = new TempDirectory();
        using var mounted = TlsTestPki.Create("mounted-root");
        using var foreign = TlsTestPki.Create("foreign-root");
        using var serverCertificate = foreign.IssueServerCertificate(["localhost"]);
        await using var server = await TlsStubServer.StartAsync(serverCertificate);
        using var factory = CreateFactory(folder.WriteFile("root.crt", mounted.RootPem));

        var exception = await Record.ExceptionAsync(() => GetStatusAsync(factory.Services, server.Address));

        exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_certificate_from_the_mounted_root_for_another_name_is_rejected()
    {
        using var folder = new TempDirectory();
        using var pki = TlsTestPki.Create("mounted-root");
        using var serverCertificate = pki.IssueServerCertificate(["other.example.test"], includeLoopbackIp: false);
        await using var server = await TlsStubServer.StartAsync(serverCertificate);
        using var factory = CreateFactory(folder.WriteFile("root.crt", pki.RootPem));

        var exception = await Record.ExceptionAsync(() => GetStatusAsync(factory.Services, server.Address));

        exception.Should().BeOfType<HttpRequestException>("host name validation still applies under the pinned root");
    }

    [Theory]
    [MemberData(nameof(BadFileKinds))]
    public void Start_up_fails_for_a_bad_trusted_root_file(string kind)
    {
        using var folder = new TempDirectory();
        var path = BadTrustedRootFiles.Write(kind, folder.Path);
        using var factory = CreateFactory(path);

        var exception = Record.Exception(() => factory.Server);

        exception.Should().NotBeNull("a configured root that is unusable must stop the host");
        var messages = AllMessages(exception!);
        messages.Should().Contain(message => message.Contains(RootPathKey, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BadFileKinds))]
    public void The_validator_rejects_a_bad_trusted_root_file(string kind)
    {
        using var folder = new TempDirectory();
        var path = BadTrustedRootFiles.Write(kind, folder.Path);

        var result = new ApiJwtTrustedRootValidator().Validate(null, new ApiJwtOptions { TrustedRootPath = path });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(RootPathKey).And.NotContain(path);
    }

    [Fact]
    public void An_unset_root_means_system_trust_and_a_good_one_validates()
    {
        new ApiJwtTrustedRootValidator().Validate(null, new ApiJwtOptions()).Succeeded.Should().BeTrue();

        using var folder = new TempDirectory();
        using var pki = TlsTestPki.Create("mounted-root");
        var good = new ApiJwtOptions { TrustedRootPath = folder.WriteFile("root.crt", pki.RootPem) };

        new ApiJwtTrustedRootValidator().Validate(null, good).Succeeded.Should().BeTrue();
    }

    public void Dispose() => _issuer.Dispose();

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory(string rootPath) =>
        ApiTestFactory.Create(
            _issuer,
            extraConfiguration: [new KeyValuePair<string, string?>(RootPathKey, rootPath)]);

    private static async Task<HttpStatusCode> GetStatusAsync(IServiceProvider services, Uri address)
    {
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await options.Backchannel.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static IEnumerable<string> AllMessages(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "decisya-api-trust-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string WriteFile(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
