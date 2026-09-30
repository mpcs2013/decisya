using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.SharedKernel.Results;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// The module's instruments (G2 "Telemetry"). Tags are bounded: the feature tag is a catalog
/// key or <c>unknown</c>, the command tag one of three names, the outcome <c>succeeded</c> or
/// an error code. Never the reason, a tenant id or a user id.
/// </summary>
internal static class EntitlementsTelemetry
{
    public const string SourceOverride = "override";
    public const string SourceTrial = "trial";
    public const string SourcePlan = "plan";
    public const string SourceNone = "none";

    private static readonly Counter<long> Evaluations =
        EntitlementsModule.Meter.CreateCounter<long>("decisya.entitlements.evaluations");

    private static readonly Counter<long> AdminCommands =
        EntitlementsModule.Meter.CreateCounter<long>("decisya.entitlements.admin_commands");

    public static void RecordEvaluation(string feature, bool granted, string source) =>
        Evaluations.Add(
            1,
            new KeyValuePair<string, object?>("decisya.entitlements.feature", feature),
            new KeyValuePair<string, object?>("decisya.entitlements.outcome", granted ? "granted" : "denied"),
            new KeyValuePair<string, object?>("decisya.entitlements.source", source));

    public static void RecordCommand(string command, Result result) =>
        AdminCommands.Add(
            1,
            new KeyValuePair<string, object?>("decisya.entitlements.command", command),
            new KeyValuePair<string, object?>("decisya.entitlements.outcome", result.IsSuccess ? "succeeded" : result.Error.Code));

    public static Activity? StartCommand(string name) =>
        EntitlementsModule.ActivitySource.StartActivity($"Entitlements.{name}");
}
