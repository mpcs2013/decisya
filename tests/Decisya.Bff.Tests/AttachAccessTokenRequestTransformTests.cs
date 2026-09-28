using Decisya.Bff.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Transforms;

namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G3 point 2 (T-02): the final-resolved-destination https check, exercised directly and
/// without Docker or a login — service discovery resolving <c>Bff:Api:Address</c>'s configured
/// <c>https://decisya-api</c> to an <c>http</c> endpoint is an infrastructure condition
/// (<c>services__decisya-api__https__0=http://...</c>) that the Testcontainers harness cannot
/// reproduce end-to-end (it has no https Keycloak to run a "Production"-labelled BFF against),
/// so this proves the transform's own decision in isolation instead.
/// </summary>
public class AttachAccessTokenRequestTransformTests
{
    [Fact]
    public async Task Refuses_an_http_destination_outside_Development()
    {
        var transform = new AttachAccessTokenRequestTransform(new StubHostEnvironment("Production"));
        var context = BuildContext("http://127.0.0.1:54321");

        var exception = await Record.ExceptionAsync(() => transform.ApplyAsync(context).AsTask());

        exception.Should().BeOfType<InvalidOperationException>();
        context.ProxyRequest.Headers.Authorization.Should().BeNull("no bearer is attached when the destination is refused");
    }

    [Fact]
    public async Task Attaches_the_bearer_to_an_https_destination_outside_Development()
    {
        var transform = new AttachAccessTokenRequestTransform(new StubHostEnvironment("Production"));
        var context = BuildContext("https://127.0.0.1:54321");

        await transform.ApplyAsync(context);

        context.ProxyRequest.Headers.Authorization.Should().NotBeNull();
        context.ProxyRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        context.ProxyRequest.Headers.Authorization.Parameter.Should().Be("the-access-token");
    }

    [Fact]
    public async Task Allows_an_http_destination_in_Development()
    {
        var transform = new AttachAccessTokenRequestTransform(new StubHostEnvironment("Development"));
        var context = BuildContext("http://127.0.0.1:54321");

        await transform.ApplyAsync(context);

        context.ProxyRequest.Headers.Authorization.Should().NotBeNull("the ApiDouble is always http, and only reachable in Development");
    }

    private static RequestTransformContext BuildContext(string destinationPrefix)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set(new ForwardedAccessTokenFeature("the-access-token"));

        return new RequestTransformContext
        {
            HttpContext = httpContext,
            ProxyRequest = new HttpRequestMessage(),
            DestinationPrefix = destinationPrefix,
        };
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Decisya.Bff.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
