using System.Net;
using System.Net.Sockets;
using Decisya.ServiceDefaults.Production;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting;

/// <summary>Production configuration for every Decisya host (issue #120, D6).</summary>
public static class ProductionExtensions
{
    /// <summary>Where Compose mounts the file secrets.</summary>
    public const string DefaultSecretsDirectory = "/run/secrets";

    /// <summary>The configuration key that lists the one trusted proxy (Caddy's fixed address).</summary>
    public const string TrustedProxiesKey = "Decisya:Edge:TrustedProxies";

    /// <summary>
    /// Outside Development, adds a key-per-file configuration source over
    /// <see cref="DefaultSecretsDirectory"/>. A file named <c>ConnectionStrings__tenancy</c>
    /// supplies <c>ConnectionStrings:tenancy</c> (the <c>__</c> becomes the key delimiter),
    /// and its value is the file content without one trailing platform newline
    /// (<see cref="Environment.NewLine"/>, which is <c>\n</c> in the Linux containers).
    /// </summary>
    /// <remarks>
    /// The source is added last, so a secret file wins over every other source. It is
    /// optional: a missing directory is not an error (the migrator and a local run have
    /// none). In Development it is not added at all, so a stray folder never overrides
    /// user-secrets. The source never reloads and never logs a value.
    /// </remarks>
    public static TBuilder AddSecretFiles<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
        => builder.AddSecretFiles(DefaultSecretsDirectory);

    internal static TBuilder AddSecretFiles<TBuilder>(this TBuilder builder, string directory)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (builder.Environment.IsDevelopment())
        {
            return builder;
        }

        builder.Configuration.AddKeyPerFile(directoryPath: directory, optional: true, reloadOnChange: false);
        return builder;
    }

    /// <summary>
    /// Production hosting for a web host: <c>AllowedHosts</c> must be set and must not be a
    /// wildcard, and the health endpoints move to the management port (served for requests that
    /// arrived on that local port only). No effect in Development, and none on a non-web host.
    /// </summary>
    internal static TBuilder AddProductionHosting<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder is not WebApplicationBuilder)
        {
            return builder;
        }

        var services = builder.Services;

        services.AddOptions<ProductionHostingOptions>()
            .Configure<IConfiguration>(static (options, configuration) =>
                options.AllowedHosts = configuration[ProductionHostingOptions.AllowedHostsKey])
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ProductionHostingOptions>, ProductionHostingOptionsValidator>());

        if (!builder.Environment.IsDevelopment())
        {
            // Index 0: the management branch must be the outermost middleware (see its remarks).
            services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter, ManagementHealthStartupFilter>());
        }

        return builder;
    }

    /// <summary>
    /// Honours <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from the proxies listed in
    /// <see cref="TrustedProxiesKey"/> and from nobody else. Call it first in the pipeline.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Development: no effect (there is no proxy).</item>
    /// <item>Outside Development with the key unset: no effect, so nothing is trusted.
    /// The deploy guard checks that the stack sets it.</item>
    /// <item>Each entry must be one unicast IP address. A CIDR range, a wildcard, an
    /// unspecified address or text that is not an address fails start-up, naming the key.</item>
    /// <item>The default loopback proxy and every trusted network are cleared, so exactly
    /// the configured addresses are trusted. One hop, and no <c>X-Forwarded-Host</c>.</item>
    /// </list>
    /// </remarks>
    public static WebApplication UseDecisyaForwardedHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Environment.IsDevelopment())
        {
            return app;
        }

        var proxies = ReadTrustedProxies(app.Configuration);
        if (proxies.Count == 0)
        {
            return app;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
            RequireHeaderSymmetry = false,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        app.UseForwardedHeaders(options);
        return app;
    }

    internal static IReadOnlyList<IPAddress> ReadTrustedProxies(IConfiguration configuration)
    {
        var section = configuration.GetSection(TrustedProxiesKey);
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            raw.AddRange(section.Value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                raw.Add(child.Value.Trim());
            }
        }

        var result = new List<IPAddress>(raw.Count);
        foreach (var entry in raw)
        {
            if (entry.Contains('/', StringComparison.Ordinal)
                || !IPAddress.TryParse(entry, out var address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.Broadcast)
                || address.Equals(IPAddress.None)
                || address.IsIPv6Multicast
                || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224))
            {
                throw new InvalidOperationException(
                    $"{TrustedProxiesKey} must list single unicast IP addresses (no range, no wildcard).");
            }

            result.Add(address);
        }

        return result;
    }
}
