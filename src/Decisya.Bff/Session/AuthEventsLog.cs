namespace Decisya.Bff.Session;

/// <summary>
/// The BFF's authentication events (issue #121, G2 D4, ASVS V16.3.1 and V16.3.2): fixed templates
/// with a closed field list, source-generated, no exception parameter and no free text.
/// <c>tenant_id</c> and the hashed <c>user_id</c> are never message arguments: they reach the record
/// through the existing <c>ILogEnrichmentContext</c> (<see cref="AuthEvents"/> opens it for the one
/// call). Nothing from a token, cookie, claim dictionary or provider message is a parameter.
/// </summary>
internal static partial class AuthEventsLog
{
    [LoggerMessage(
        EventId = 1810,
        EventName = "auth.signin.succeeded",
        Level = LogLevel.Information,
        Message = "auth.signin.succeeded: a user signed in (acr {Acr}).")]
    internal static partial void SignInSucceeded(ILogger logger, string acr);

    [LoggerMessage(
        EventId = 1811,
        EventName = "auth.signin.failed",
        Level = LogLevel.Warning,
        Message = "auth.signin.failed: a sign-in did not complete ({Reason}).")]
    internal static partial void SignInFailed(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1812,
        EventName = "auth.signout",
        Level = LogLevel.Information,
        Message = "auth.signout: a session ended ({Initiator}).")]
    internal static partial void SignOut(ILogger logger, string initiator);
}
