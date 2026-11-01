using System.Diagnostics.Metrics;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// Collects the reason tag of every <c>decisya.messaging.envelope.refused</c> measurement while it
/// lives. The meter is process-wide, so every class that refuses a message belongs to the "ChainHost"
/// collection, which runs its classes one after another.
/// </summary>
internal sealed class RefusalMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<string> _reasons = [];
    private readonly List<int> _tagCounts = [];

    public RefusalMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Decisya.Messaging" && instrument.Name == "decisya.messaging.envelope.refused")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            lock (_reasons)
            {
                measurement.Should().Be(1);
                _tagCounts.Add(tags.Length);
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason")
                    {
                        _reasons.Add((string)tag.Value!);
                    }
                }
            }
        });
        _listener.Start();
    }

    public IReadOnlyList<string> Reasons
    {
        get
        {
            lock (_reasons)
            {
                _tagCounts.Should().OnlyContain(count => count == 1, "the reason is the metric's only tag");
                return [.. _reasons];
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
