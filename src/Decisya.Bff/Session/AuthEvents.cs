using System.Security.Claims;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

namespace Decisya.Bff.Session;

/// <summary>The closed list of <c>reason</c> values of <c>auth.signin.failed</c> (G2 D4).</summary>
internal static class SignInFailureReason
{
    internal const string AccessDenied = "access_denied";
    internal const string StateInvalid = "state_invalid";
    internal const string TokenExchangeFailed = "token_exchange_failed";
    internal const string RemoteFailure = "remote_failure";
    internal const string TicketStoreUnavailable = "ticket_store_unavailable";
    internal const string Other = "other";

    /// <summary>Every value this class names: the test pins this list.</summary>
    internal static readonly string[] All =
        [AccessDenied, StateInvalid, TokenExchangeFailed, RemoteFailure, TicketStoreUnavailable, Other];

    /// <summary>
    /// Classifies a remote-authentication failure by the exception's <b>type</b> only; never its
    /// message, which can carry the provider's own <c>error_description</c> (attacker-controlled on
    /// the callback URL). <paramref name="authorizationCodeReceived"/> says the handler had already
    /// received a code, so a protocol or token-validation failure is the code exchange.
    /// </summary>
    internal static string Classify(Exception? failure, bool authorizationCodeReceived)
    {
        if (failure is AggregateException aggregate)
        {
            failure = aggregate.InnerExceptions.FirstOrDefault();
        }

        return failure switch
        {
            // The handler's string failures: "Correlation failed.", an unprotectable or missing state.
            AuthenticationFailureException => StateInvalid,
            HttpRequestException or TimeoutException or TaskCanceledException => RemoteFailure,
            OpenIdConnectProtocolException or SecurityTokenException =>
                authorizationCodeReceived ? TokenExchangeFailed : RemoteFailure,
            _ => Other,
        };
    }
}

/// <summary>
/// The OIDC and cookie event handlers that emit the BFF's authentication events (issue #121, G3
/// G4-121-04). Every <c>OnRemoteFailure</c> and <c>OnAccessDenied</c> branch calls
/// <c>HandleResponse()</c>, so nothing rethrows into the exception handler (which would log the
/// framework's failure text at Error). The response is the same generic 500 ProblemDetails the
/// exception handler wrote before.
/// </summary>
internal static class AuthEvents
{
    internal const string AuthenticationContextValue = "2";

    private const string LoggerCategory = "Decisya.Bff.Session.AuthEvents";
    private const string CodeReceivedItem = "Decisya.Bff.OidcAuthorizationCodeReceived";

    /// <summary>Always asks Keycloak for level 2 (non-essential): admins are stepped up, tenant users get "1".</summary>
    internal static Task OnRedirectToIdentityProvider(RedirectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ProtocolMessage.RequestType == OpenIdConnectRequestType.Authentication)
        {
            context.ProtocolMessage.AcrValues = AuthenticationContextValue;
        }

        return Task.CompletedTask;
    }

    internal static Task OnAuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.HttpContext.Items[CodeReceivedItem] = true;
        return Task.CompletedTask;
    }

    internal static async Task OnAccessDenied(AccessDeniedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var logger = CreateLogger(context.HttpContext);
        AuthEventsLog.SignInFailed(logger, SignInFailureReason.AccessDenied);
        context.HandleResponse();
        await WriteGenericFailureAsync(context.HttpContext).ConfigureAwait(false);
    }

    internal static async Task OnRemoteFailure(RemoteFailureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var codeReceived = context.HttpContext.Items.ContainsKey(CodeReceivedItem);
        var reason = SignInFailureReason.Classify(context.Failure, codeReceived);
        var logger = CreateLogger(context.HttpContext);
        AuthEventsLog.SignInFailed(logger, reason);
        context.HandleResponse();
        await WriteGenericFailureAsync(context.HttpContext).ConfigureAwait(false);
    }

    /// <summary>Emits <c>auth.signin.succeeded</c> after the ticket is stored (the cookie handler calls this after <c>StoreAsync</c>).</summary>
    internal static Task OnSignedIn(CookieSignedInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.Principal;
        if (principal is null)
        {
            return Task.CompletedTask;
        }

        var enrichment = context.HttpContext.RequestServices.GetRequiredService<ILogEnrichmentContext>();

        // HttpContext.User is still the anonymous principal here, so the scope is opened from the new
        // principal for this one call and restored in the using; the raw sub is hashed by the
        // enrichment and never logged.
        var acr = ClassifyAcr(principal);
        var logger = CreateLogger(context.HttpContext);
        using (enrichment.Begin(TenantOf(principal), SubjectOf(principal)))
        {
            AuthEventsLog.SignInSucceeded(logger, acr);
        }

        return Task.CompletedTask;
    }

    /// <summary><c>auth.signout</c>, <paramref name="initiator"/> "user" or "backchannel", for the given user (hashed by the enrichment).</summary>
    internal static void SignOut(HttpContext context, string initiator, ClaimsPrincipal? principal, string? subjectOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var enrichment = context.RequestServices.GetRequiredService<ILogEnrichmentContext>();
        string? tenant = null;
        var subject = subjectOverride;
        if (principal is not null)
        {
            tenant = TenantOf(principal);
            subject ??= SubjectOf(principal);
        }

        var logger = CreateLogger(context);
        using (enrichment.Begin(tenant, subject))
        {
            AuthEventsLog.SignOut(logger, initiator);
        }
    }

    internal static string ClassifyAcr(ClaimsPrincipal? principal) =>
        principal?.FindFirst("acr")?.Value switch
        {
            "1" => "1",
            "2" => "2",
            _ => "other",
        };

    private static string? SubjectOf(ClaimsPrincipal principal) => principal.FindFirst("sub")?.Value;

    /// <summary>The tenant for the log field only: a canonical, non-empty GUID, or nothing.</summary>
    private static string? TenantOf(ClaimsPrincipal principal) =>
        Guid.TryParseExact(principal.FindFirst("tenant_id")?.Value, "D", out var tenant) && tenant != Guid.Empty
            ? tenant.ToString("D")
            : null;

    private static ILogger CreateLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

    private static async Task WriteGenericFailureAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = StatusCodes.Status500InternalServerError },
        }).ConfigureAwait(false);
    }
}
