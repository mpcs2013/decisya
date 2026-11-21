using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// The module's instruments (G2 "Telemetry"; NFR-87). Every tag value comes from a bounded set below. There is
/// no user id, account id, tenant id, currency or amount in any metric attribute. A span carries the tenant id
/// and the result, never an account id, name or amount, and no exception text.
/// </summary>
internal static class LedgerTelemetry
{
    public const string OperationList = "list";
    public const string OperationGet = "get";
    public const string OperationCreate = "create";
    public const string OperationRename = "rename";
    public const string OperationCurrencyChange = "currency_change";

    public const string ResultOk = "ok";
    public const string ResultRefused = "refused";
    public const string ResultError = "error";

    public const string ReasonNone = "none";
    public const string ReasonInvalid = "invalid";
    public const string ReasonForbidden = "forbidden";
    public const string ReasonNotFound = "not_found";
    public const string ReasonLimitReached = "limit_reached";
    public const string ReasonCurrencyLocked = "currency_locked";
    public const string ReasonCurrencyUnchanged = "currency_unchanged";

    private static readonly Counter<long> Requests =
        LedgerModule.Meter.CreateCounter<long>("decisya.ledger.requests", unit: "{request}");

    public static void Record(string operation, string result, string reason) =>
        Requests.Add(
            1,
            new KeyValuePair<string, object?>("decisya.ledger.operation", operation),
            new KeyValuePair<string, object?>("result", result),
            new KeyValuePair<string, object?>("decisya.ledger.reason", reason));

    /// <summary>Starts the operation's span (<c>Ledger.CreateAccount</c> and so on). The caller sets the result tag when it ends.</summary>
    public static Activity? StartSpan(string spanName, ICurrentTenant tenant)
    {
        var activity = LedgerModule.ActivitySource.StartActivity(spanName);
        if (activity is not null && tenant.Resolution.Kind == TenantResolutionKind.Tenant)
        {
            activity.SetTag("decisya.tenant_id", tenant.Resolution.TenantId.ToString());
        }

        return activity;
    }

    public static void SetResult(Activity? activity, string result) =>
        activity?.SetTag("decisya.ledger.result", result);
}
