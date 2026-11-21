using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Ledger;

/// <summary>Registers the Ledger module (schema <c>ledger</c>).</summary>
public static class LedgerModule
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.Ledger";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>Adds the Ledger module's services.</summary>
    public static IServiceCollection AddLedgerModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
