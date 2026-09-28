using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Decisya.Bff.Session;

/// <summary>
/// The one <c>ActivitySource</c> and <c>Meter</c> named <c>Decisya.Bff</c> (CLAUDE.md
/// observability principle; G2). <see cref="TicketStoreFailures"/> is tagged by
/// <c>operation</c> (<c>retrieve</c>, <c>store</c>, <c>remove</c>).
/// </summary>
internal static class BffTelemetry
{
    internal const string Name = "Decisya.Bff";

    internal static readonly ActivitySource ActivitySource = new(Name);

    internal static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> TicketStoreFailures =
        Meter.CreateCounter<long>("decisya.bff.ticket_store.failures");

    /// <summary>#19 G2: tagged <c>result</c> = <c>success | invalid_grant | error | lock_timeout</c>.</summary>
    internal static readonly Counter<long> TokenRefreshes =
        Meter.CreateCounter<long>("decisya.bff.token_refreshes");

    /// <summary>#19 G2 (B-2, T-13): tagged <c>reason</c>.</summary>
    internal static readonly Counter<long> KeycloakLogoutFailures =
        Meter.CreateCounter<long>("decisya.bff.keycloak_logout.failures");
}
