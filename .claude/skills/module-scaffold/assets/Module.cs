using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.__Name__;

/// <summary>Registers the __Name__ module (schema <c>__schema__</c>).</summary>
public static class __Name__Module
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.__Name__";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>Adds the __Name__ module's services.</summary>
    public static IServiceCollection Add__Name__Module(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
