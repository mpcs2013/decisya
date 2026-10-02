using System.Diagnostics.Metrics;

namespace Decisya.Modules.Admin.Endpoints;

/// <summary>
/// The module's instruments (issue #25, G2 "Telemetry"). Both tags are bounded sets: the action
/// (three names or <c>unknown</c>) and the outcome. Never a tenant id, user id, reason or claim.
/// </summary>
internal static class AdminTelemetry
{
    public const string OutcomeSucceeded = "succeeded";
    public const string OutcomeForbidden = "forbidden";
    public const string OutcomeInvalid = "invalid";
    public const string OutcomeNotFound = "not_found";
    public const string OutcomeConflict = "conflict";

    private static readonly Counter<long> Requests = AdminModule.Meter.CreateCounter<long>("decisya.admin.requests");

    public static void RecordRequest(string action, string outcome) =>
        Requests.Add(
            1,
            new KeyValuePair<string, object?>("decisya.admin.action", action),
            new KeyValuePair<string, object?>("decisya.admin.outcome", outcome));
}
