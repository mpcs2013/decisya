using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Decisya.Modules.Admin.Tests.TestSupport;

/// <summary>
/// Records every activity (any source: ASP.NET Core's included) name, tag, tag object and event tag, and every
/// measurement tag of every meter, as <c>key=value</c> strings, so a scan can prove a canary never reaches telemetry.
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private readonly ConcurrentQueue<string> _values = new();
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                _values.Enqueue(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    _values.Enqueue($"{tag.Key}={tag.Value}");
                }

                foreach (var tag in activity.Events.SelectMany(e => e.Tags))
                {
                    _values.Enqueue($"{tag.Key}={tag.Value}");
                }
            },
        };
        ActivitySource.AddActivityListener(_activities);

        _meters = new MeterListener { InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument) };
        _meters.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(tags));
        _meters.SetMeasurementEventCallback<double>((_, _, tags, _) => Record(tags));
        _meters.SetMeasurementEventCallback<int>((_, _, tags, _) => Record(tags));
        _meters.Start();
    }

    public IReadOnlyList<string> Values => [.. _values];

    /// <summary>Only the tags of the Admin module's own counter (<c>decisya.admin.requests</c>).</summary>
    public IReadOnlyList<string> AdminTags => [.. _values.Where(v => v.StartsWith("decisya.admin.", StringComparison.Ordinal))];

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    private void Record(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            _values.Enqueue($"{tag.Key}={tag.Value}");
        }
    }
}
