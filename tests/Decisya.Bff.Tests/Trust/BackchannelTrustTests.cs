using System.Net;
using System.Security.Cryptography.X509Certificates;
using Decisya.Bff.Proxy;
using Decisya.Bff.Security;
using Decisya.TestSupport.Tls;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Decisya.Bff.Tests.Trust;

/// <summary>
/// #120 G4-120-05 (B-3): each BFF back-channel client trusts the mounted root and nothing else.
/// The real host is built (Development, no Docker, no Keycloak, no Redis), the client is taken
/// from the real service provider, and it talks to an in-process Kestrel HTTPS server holding a
/// generated certificate. Clients covered: the OIDC handler's back channel (discovery, JWKS,
/// token and logout calls all use it, <c>KeycloakTokenClient</c> included) and the YARP
/// forwarder's HttpClient. The Api's JWKS client is covered in <c>Decisya.Api.Tests</c>.
/// </summary>
public class BackchannelTrustTests
{
    private const string RootPathKey = "Bff:Backchannel:TrustedRootPath";

    public static IEnumerable<object[]> Clients() => [["oidc"], ["forwarder"]];

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task The_mounted_root_is_trusted(string client)
    {
        using var folder = new TempFolder();
        using var pki = TlsTestPki.Create("mounted-root");
        using var serverCertificate = pki.IssueServerCertificate(["localhost"]);
        await using var server = await TlsStubServer.StartAsync(serverCertificate);
        using var factory = new TrustBffFactory(folder.WriteFile("root.crt", pki.RootPem));

        var status = await GetStatusAsync(client, factory.Services, server.Address);

        status.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_foreign_root_is_rejected(string client)
    {
        using var folder = new TempFolder();
        using var mounted = TlsTestPki.Create("mounted-root");
        using var foreign = TlsTestPki.Create("foreign-root");
        using var serverCertificate = foreign.IssueServerCertificate(["localhost"]);
        await using var server = await TlsStubServer.StartAsync(serverCertificate);
        using var factory = new TrustBffFactory(folder.WriteFile("root.crt", mounted.RootPem));

        var exception = await Record.ExceptionAsync(() => GetStatusAsync(client, factory.Services, server.Address));

        exception.Should().BeOfType<HttpRequestException>();
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_certificate_from_the_mounted_root_for_another_name_is_rejected(string client)
    {
        using var folder = new TempFolder();
        using var pki = TlsTestPki.Create("mounted-root");
        using var serverCertificate = pki.IssueServerCertificate(["other.example.test"], includeLoopbackIp: false);
        await using var server = await TlsStubServer.StartAsync(serverCertificate);
        using var factory = new TrustBffFactory(folder.WriteFile("root.crt", pki.RootPem));

        var exception = await Record.ExceptionAsync(() => GetStatusAsync(client, factory.Services, server.Address));

        exception.Should().BeOfType<HttpRequestException>("host name validation still applies under the pinned root");
    }

    [Fact]
    public void The_forwarder_factory_registered_in_the_host_is_the_pinned_one()
    {
        using var folder = new TempFolder();
        using var pki = TlsTestPki.Create("mounted-root");
        using var factory = new TrustBffFactory(folder.WriteFile("root.crt", pki.RootPem));

        factory.Services.GetRequiredService<IForwarderHttpClientFactory>()
            .Should().BeOfType<TrustedForwarderHttpClientFactory>();
    }

    [Fact]
    public void The_chain_policy_trusts_the_one_root_and_not_the_system_store()
    {
        using var pki = TlsTestPki.Create("mounted-root");

        var policy = BackchannelRoot.CreateChainPolicy(pki.Root);

        policy.TrustMode.Should().Be(X509ChainTrustMode.CustomRootTrust);
        policy.CustomTrustStore.Should().ContainSingle().Which.Thumbprint.Should().Be(pki.Root.Thumbprint);
        policy.DisableCertificateDownloads.Should().BeTrue();
    }

    [Fact]
    public void A_certificate_issued_by_a_system_trusted_root_is_rejected_under_the_pinned_policy()
    {
        // No network: take a real root from the operating-system store, show that the system
        // policy trusts it (the control), and that the pinned policy does not.
        using var pki = TlsTestPki.Create("mounted-root");
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        X509Certificate2? systemRoot = null;
        foreach (var candidate in store.Certificates)
        {
            using var control = new X509Chain();
            control.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            control.ChainPolicy.DisableCertificateDownloads = true;
            if (control.Build(candidate))
            {
                systemRoot = candidate;
                break;
            }
        }

        if (systemRoot is null)
        {
            Assert.Skip("No usable system-trusted root certificate on this machine.");
            return;
        }

        using var pinned = new X509Chain { ChainPolicy = BackchannelRoot.CreateChainPolicy(pki.Root) };

        pinned.Build(systemRoot).Should().BeFalse("a public root is not the mounted root");
        // .NET reports a self-signed root absent from the custom store as PartialChain on some
        // platforms and UntrustedRoot on others; both mean "no trust anchor".
        pinned.ChainStatus.Should().Contain(status =>
            status.Status == X509ChainStatusFlags.UntrustedRoot || status.Status == X509ChainStatusFlags.PartialChain);
    }

    [Theory]
    [MemberData(nameof(BadFileKinds))]
    public void Start_up_fails_for_a_bad_trusted_root_file(string kind)
    {
        using var folder = new TempFolder();
        var path = BadTrustedRootFiles.Write(kind, folder.Path);
        using var factory = new TrustBffFactory(path);

        var exception = Record.Exception(() => factory.Server);

        exception.Should().NotBeNull("a configured root that is unusable must stop the host");
        var messages = AllMessages(exception!);
        messages.Should().Contain(message => message.Contains(RootPathKey, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(path, StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> BadFileKinds() => BadTrustedRootFiles.All.Select(kind => new object[] { kind });

    [Fact]
    public void An_unset_root_means_system_trust_and_a_set_one_loads()
    {
        BackchannelRoot.LoadOrNull(null, RootPathKey).Should().BeNull();
        BackchannelRoot.LoadOrNull("  ", RootPathKey).Should().BeNull();

        using var folder = new TempFolder();
        using var pki = TlsTestPki.Create("mounted-root");
        using var loaded = BackchannelRoot.LoadOrNull(folder.WriteFile("root.crt", pki.RootPem), RootPathKey);

        loaded.Should().NotBeNull();
        loaded!.Thumbprint.Should().Be(pki.Root.Thumbprint);
    }

    private static IEnumerable<string> AllMessages(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    private static async Task<HttpStatusCode> GetStatusAsync(string client, IServiceProvider services, Uri address)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, address);

        switch (client)
        {
            case "oidc":
                var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                    .Get(OpenIdConnectDefaults.AuthenticationScheme);
                using (var response = await oidc.Backchannel.SendAsync(request, cancellationToken))
                {
                    return response.StatusCode;
                }

            case "forwarder":
                var forwarderFactory = services.GetRequiredService<IForwarderHttpClientFactory>();
                using (var invoker = forwarderFactory.CreateClient(
                    new ForwarderHttpClientContext { ClusterId = "decisya-api", NewConfig = HttpClientConfig.Empty }))
                using (var response = await invoker.SendAsync(request, cancellationToken))
                {
                    return response.StatusCode;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown client.");
        }
    }

    private sealed class TrustBffFactory(string? rootPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var overrides = TestConfiguration.GoodOverrides();
            if (rootPath is not null)
            {
                overrides[RootPathKey] = rootPath;
            }

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(overrides));
        }
    }
}
