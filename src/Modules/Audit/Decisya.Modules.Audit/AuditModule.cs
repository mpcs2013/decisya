using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.Modules.Audit.Application;
using Decisya.Modules.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Audit;

/// <summary>Registers the Audit module (schema <c>audit</c>; issue #24, G2; ADR-0013).</summary>
public static class AuditModule
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.Audit";

    /// <summary>The module's Postgres schema.</summary>
    public const string Schema = "audit";

    /// <summary>The module's one table.</summary>
    public const string RecordsTable = "audit_records";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>
    /// Adds <see cref="IAuditWriter"/> (scoped). It takes no connection string and registers no
    /// <c>DbContext</c>: nothing reads, and the writer never connects on its own: it runs on the
    /// audited command's connection and transaction (ADR-0013). No endpoint.
    /// </summary>
    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAuditWriter, AuditWriter>();

        return services;
    }
}
