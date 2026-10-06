using System.Diagnostics;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using Decisya.AppHost;
using Decisya.Bff.Security;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Decisya.Bff.Tests.Trust;

/// <summary>
/// #120 G4-120-05 (B-3), the real thing: the pinned Caddy with <c>tls internal</c> serves a stub,
/// the root it generated is exported the way <c>stackctl.py export-root</c> does (a file copy out
/// of <c>/data/caddy/pki/authorities/local/root.crt</c>), and the production loader and pinned
/// handler accept that Caddy and reject a second, freshly started Caddy with its own root.
/// </summary>
/// <remarks>
/// The Caddy pin is <c>ContainerImages.CaddyRegistry</c>, <c>CaddyImage</c>, <c>CaddyTag</c> and
/// <c>CaddySha256</c>, which platform-dev adds to <c>ContainerImages.cs</c> in #120. They are read
/// by name at run time (not referenced), so the project builds before the pin exists and this
/// test fails loudly, naming the missing constant, until it does.
/// </remarks>
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
        var image = CaddyImageReference();
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
        // The production loader and handler, unchanged: exactly one CA in a PEM file.
        using var root = BackchannelRoot.LoadOrNull(rootPath, "Bff:Backchannel:TrustedRootPath")
            ?? throw new InvalidOperationException("The exported root did not load.");
        using var handler = BackchannelRoot.CreateHandler(root);
        using var client = new HttpClient(handler);
        return await client.GetStringAsync(url, cancellationToken);
    }

    private static string CaddyImageReference()
    {
        static string Constant(string name) =>
            typeof(ContainerImages).GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string
            ?? throw new InvalidOperationException(
                $"ContainerImages.{name} does not exist yet. platform-dev adds the Caddy pin in #120 " +
                "(CaddyRegistry, CaddyImage, CaddyTag, CaddySha256); this test fails until then, on purpose.");

        return ContainerImages.Reference(
            Constant("CaddyRegistry"), Constant("CaddyImage"), Constant("CaddyTag"), Constant("CaddySha256"));
    }
}
