namespace Decisya.Bff.Session;

/// <summary>
/// Compiled log messages <c>Decisya.Bff</c> writes. Source-generated so the message template
/// is fixed at compile time; never takes a token, cookie, session key or claim value
/// (CLAUDE.md: "never log tokens, cookies, claims dictionaries or the client secret").
/// </summary>
internal static partial class BffLog
{
    [LoggerMessage(
        EventId = 1800,
        Level = LogLevel.Error,
        Message = "The Redis-backed ticket store failed during {Operation}.")]
    internal static partial void TicketStoreFailure(ILogger logger, Exception exception, string operation);

    [LoggerMessage(
        EventId = 1801,
        Level = LogLevel.Warning,
        Message = "A back-channel logout token was rejected: {Reason}.")]
    internal static partial void BackchannelLogoutTokenRejected(ILogger logger, string reason);
}
