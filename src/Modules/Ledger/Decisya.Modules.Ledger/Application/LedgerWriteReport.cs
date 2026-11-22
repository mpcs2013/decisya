using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// The one place a write's end is turned into its metric, its span tag and its log line (A5: a refusal leaves
/// a log line and a metric, never an audit record). Only bounded values go in; never a name or an amount.
/// </summary>
internal static class LedgerWriteReport
{
    public static void Completed(ILogger logger, Activity? span, string operation, AccountCommandResult result)
    {
        if (result.Outcome == LedgerOutcome.Succeeded)
        {
            LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultOk);
            LedgerTelemetry.Record(operation, LedgerTelemetry.ResultOk, LedgerTelemetry.ReasonNone);
            LedgerLog.Succeeded(logger, operation, LedgerTelemetry.ResultOk, result.AccountId);
            return;
        }

        Refused(logger, span, operation, ReasonFor(result.Outcome));
    }

    public static void Refused(ILogger logger, Activity? span, string operation, string reason)
    {
        LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultRefused);
        LedgerTelemetry.Record(operation, LedgerTelemetry.ResultRefused, reason);
        LedgerLog.Refused(logger, operation, LedgerTelemetry.ResultRefused, reason);
    }

    public static void Failed(Activity? span, string operation)
    {
        LedgerTelemetry.SetResult(span, LedgerTelemetry.ResultError);
        LedgerTelemetry.Record(operation, LedgerTelemetry.ResultError, LedgerTelemetry.ReasonNone);
    }

    private static string ReasonFor(LedgerOutcome outcome) => outcome switch
    {
        LedgerOutcome.Forbidden => LedgerTelemetry.ReasonForbidden,
        LedgerOutcome.NotFound => LedgerTelemetry.ReasonNotFound,
        LedgerOutcome.LimitReached => LedgerTelemetry.ReasonLimitReached,
        LedgerOutcome.CurrencyLocked => LedgerTelemetry.ReasonCurrencyLocked,
        LedgerOutcome.CurrencyUnchanged => LedgerTelemetry.ReasonCurrencyUnchanged,
        _ => throw new InvalidOperationException("The ledger outcome has no refusal reason."),
    };
}
