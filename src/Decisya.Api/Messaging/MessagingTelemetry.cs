using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Decisya.Api.Messaging;

/// <summary>
/// The one <c>ActivitySource</c> and <c>Meter</c> named <c>Decisya.Messaging</c> (CLAUDE.md
/// observability principle; issue #231, G2). <see cref="EnvelopeRefused"/> is tagged by
/// <c>reason</c> only, a closed set of three fixed words (<see cref="EnvelopeRefusalReason"/>):
/// never a tenant id, a user id, a message type or an amount (G3 T-13). Wolverine's own
/// <c>Wolverine</c> meter and activity source stay unsubscribed: their tags carry tenant ids.
/// </summary>
internal static class MessagingTelemetry
{
    internal const string Name = "Decisya.Messaging";

    internal const string RefusedCounterName = "decisya.messaging.envelope.refused";

    internal const string ReasonTag = "reason";

    internal static readonly ActivitySource ActivitySource = new(Name);

    internal static readonly Meter Meter = new(Name);

    private static readonly Counter<long> EnvelopeRefused = Meter.CreateCounter<long>(RefusedCounterName);

    internal static void RecordRefusal(EnvelopeRefusalReason reason) =>
        EnvelopeRefused.Add(1, new KeyValuePair<string, object?>(ReasonTag, EnvelopeRefusalReasons.Text(reason)));
}
