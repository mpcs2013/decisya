using System.Diagnostics;
using System.Security.Authentication;
using System.Text;
using Decisya.AppHost;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ostium.Backchannel;

namespace Decisya.Api.Tests.Trust;

/// <summary>
/// #164 Story 6: the API's copy of the BFF's <c>CaddyRootTrustTests</c>, retargeted at
/// <c>Ostium.Backchannel.BackchannelRoot</c> (the type the API's JWT block uses for its discovery and JWKS
/// requests). Same expectations as the BFF copy, which stays unchanged because it proves the BFF's own
/// <c>BackchannelRoot</c> until the BFF switch: the pinned Caddy with <c>tls internal</c> serves a stub, the
/// root it generated is exported the way <c>stackctl.py export-root</c> does, and the loader and pinned
/// handler accept that Caddy and reject a second, freshly started Caddy with its own root.
/// </summary>
[Trait("Category", "Integration")]
public class CaddyRootTrustTests
{
    private const string RootInContainer = "/data/caddy/pki/authorities/local/root.crt";
    private const int HttpsPort = 8443;

    // The site name is "localhost" so the leaf certificate's SAN matches the URL's host.
    private const string Caddyfile = """
        {
        	admin off
        	skip_install_trust
        	https_port 8443
        	auto_https disable_redirects
        }
        localhost:8443 {
        	tls internal
        	respond "stub" 200
        }
        """;

    [Fact]
    public async Task The_exported_caddy_root_is_trusted_and_a_second_caddys_root_is_not()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = ContainerImages.Reference(
            ContainerImages.CaddyRegistry, ContainerImages.CaddyImage, ContainerImages.CaddyTag, ContainerImages.CaddySha256);
        using var folder = new TempFolder();

        await using var first = BuildCaddy(image);
        await using var second = BuildCaddy(image);
        await first.StartAsync(cancellationToken);
        await second.StartAsync(cancellationToken);

        var firstRoot = await ExportRootAsync(first, folder, "first.crt", cancellationToken);
        var secondRoot = await ExportRootAsync(second, folder, "second.crt", cancellationToken);
        File.ReadAllText(firstRoot).Should().NotBe(File.ReadAllText(secondRoot), "each Caddy generates its own CA");

        var firstUrl = new Uri($"https://localhost:{first.GetMappedPublicPort(HttpsPort)}/");
        var secondUrl = new Uri($"https://localhost:{second.GetMappedPublicPort(HttpsPort)}/");

        // Each Caddy serves, and is trusted through its own exported root.
        (await GetWhenServingAsync(firstRoot, firstUrl, cancellationToken)).Should().Be("stub");
        (await GetWhenServingAsync(secondRoot, secondUrl, cancellationToken)).Should().Be("stub");

        // The other Caddy's root must not be accepted, and it must fail in the TLS handshake.
        var wrongForFirst = await Record.ExceptionAsync(() => GetAsync(secondRoot, firstUrl, cancellationToken));
        var wrongForSecond = await Record.ExceptionAsync(() => GetAsync(firstRoot, secondUrl, cancellationToken));

        foreach (var exception in new[] { wrongForFirst, wrongForSecond })
        {
            exception.Should().BeOfType<HttpRequestException>();
            exception!.InnerException.Should().BeAssignableTo<AuthenticationException>("the handshake is what rejects it");
        }
    }

    [Fact]
    public void The_trust_type_under_test_is_the_Ostium_package_type()
    {
        typeof(BackchannelRoot).Assembly.GetName().Name.Should().Be("Ostium.Backchannel");
        typeof(BackchannelRoot).FullName.Should().Be("Ostium.Backchannel.BackchannelRoot");
    }

    private static IContainer BuildCaddy(string image) =>
        new ContainerBuilder(image)
            .WithPortBinding(HttpsPort, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Caddyfile.ReplaceLineEndings("\n")), "/etc/caddy/Caddyfile")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("serving initial configuration"))
            .Build();

    private static async Task<string> ExportRootAsync(
        IContainer container, TempFolder folder, string fileName, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var bytes = await container.ReadFileAsync(RootInContainer, cancellationToken);
                if (bytes.Length > 0)
                {
                    return folder.WriteFile(fileName, Encoding.UTF8.GetString(bytes));
                }
            }
            catch (Exception exception) when (stopwatch.Elapsed < TimeSpan.FromSeconds(30)
                && exception is not OperationCanceledException)
            {
                // Caddy creates its CA lazily after the config loads: try again.
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private static async Task<string> GetWhenServingAsync(string rootPath, Uri url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await GetAsync(rootPath, url, cancellationToken);
            }
            catch (HttpRequestException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                // The leaf certificate is issued just after "serving initial configuration".
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private static async Task<string> GetAsync(string rootPath, Uri url, CancellationToken cancellationToken)
    {
        // The package's loader and handler, unchanged: exactly one CA in a PEM file.
        using var root = BackchannelRoot.LoadOrNull(rootPath, "Api:Jwt:TrustedRootPath")
            ?? throw new InvalidOperationException("The exported root did not load.");
        using var handler = BackchannelRoot.CreateHandler(root);
        using var client = new HttpClient(handler);
        return await client.GetStringAsync(url, cancellationToken);
    }

    /// <summary>A per-test temporary directory, removed on dispose. Holds only public test material.</summary>
    private sealed class TempFolder : IDisposable
    {
        internal TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "decisya-api-caddy-trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        private string Path { get; }

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
                // Best effort: a leftover folder of public test certificates is harmless.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
