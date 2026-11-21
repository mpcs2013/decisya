using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// The compiled log messages the module writes (CA1848; NFR-87). The fields are bounded: <c>operation</c>,
/// <c>result</c> and <c>reason</c> come from the sets in <see cref="LedgerTelemetry"/>, and <c>account_id</c> is an
/// opaque version-7 uuid, not personal data. Deliberately no parameter for an account name, an amount, a
/// balance or a currency amount, and none for an exception message (a parser message can echo the body). The
/// tenant and the hashed user come from the request's enrichment scope.
/// </summary>
internal static partial class LedgerLog
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Ledger: {Operation} {Result}, account_id {AccountId}.")]
    internal static partial void Succeeded(ILogger logger, string operation, string result, Guid? accountId);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Ledger: {Operation} {Result}, reason {Reason}.")]
    internal static partial void Refused(ILogger logger, string operation, string result, string reason);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Ledger: {Operation} refused, the caller is not the owner of the space.")]
    internal static partial void WriteForbidden(ILogger logger, string operation);
}
