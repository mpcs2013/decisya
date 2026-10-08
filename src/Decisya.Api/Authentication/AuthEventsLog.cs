namespace Decisya.Api.Authentication;

/// <summary>
/// The Api's authentication events (issue #121, G2 D4, ASVS V16.3.1 and V16.3.2): fixed templates
/// with a closed field list, source-generated, no exception parameter and no free text. The user id
/// never goes in as an argument; it reaches the record through the request's enrichment
/// (<c>CallerContextMiddleware</c>), hashed. Nothing in a token, header or claim is ever a parameter.
/// </summary>
internal static partial class AuthEventsLog
{
    [LoggerMessage(
        EventId = 2003,
        EventName = "auth.token.rejected",
        Level = LogLevel.Warning,
        Message = "auth.token.rejected: a bearer token was rejected ({Reason}).")]
    internal static partial void TokenRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 2004,
        EventName = "auth.admin.mfa_required",
        Level = LogLevel.Warning,
        Message = "auth.admin.mfa_required: an admin request was refused because the access token does not prove MFA.")]
    internal static partial void AdminMfaRequired(ILogger logger);
}
