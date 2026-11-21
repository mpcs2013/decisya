using System.Text.Json;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Decisya.Modules.Ledger.Endpoints;

/// <summary>
/// The five <c>/api/ledger</c> operations (issue #341, G2 "API surface"). The group requires the entitlement
/// policy of <c>ledger.transactions</c>, which both plans hold; each write also requires
/// <see cref="LedgerModule.OwnerPolicy"/> and carries a <see cref="LedgerOperationMetadata"/>. There is no
/// <c>NoEntitlementRequired</c> marker. Handlers are thin: they parse, validate, call the module's handler or
/// query, and map the outcome. Every response carries <c>Cache-Control: no-store</c>, and none carries a tenant,
/// actor, role or permission member (F341-04).
/// </summary>
internal static class LedgerEndpoints
{
    /// <summary>The largest request body any write reads (G3 M341-05 (b)): 4 KiB.</summary>
    internal const int MaxBodyBytes = 4096;

    public static RouteGroupBuilder Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ledger")
            .RequireAuthorization(EntitlementPolicies.NameFor(FeatureKeys.LedgerTransactions));

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        group.MapGet("/accounts", ListAccountsAsync)
            .WithName("Ledger_ListAccounts")
            .Produces<AccountListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/accounts/{accountId}", GetAccountAsync)
            .WithName("Ledger_GetAccount")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/accounts", CreateAccountAsync)
            .RequireAuthorization(LedgerModule.OwnerPolicy)
            .WithMetadata(new LedgerOperationMetadata(LedgerTelemetry.OperationCreate))
            .WithName("Ledger_CreateAccount")
            .Accepts<CreateAccountRequest>("application/json")
            .Produces<AccountDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/accounts/{accountId}/name", RenameAccountAsync)
            .RequireAuthorization(LedgerModule.OwnerPolicy)
            .WithMetadata(new LedgerOperationMetadata(LedgerTelemetry.OperationRename))
            .WithName("Ledger_RenameAccount")
            .Accepts<RenameAccountRequest>("application/json")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/accounts/{accountId}/currency", ChangeAccountCurrencyAsync)
            .RequireAuthorization(LedgerModule.OwnerPolicy)
            .WithMetadata(new LedgerOperationMetadata(LedgerTelemetry.OperationCurrencyChange))
            .WithName("Ledger_ChangeAccountCurrency")
            .Accepts<ChangeAccountCurrencyRequest>("application/json")
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> ListAccountsAsync(AccountQueries queries, CancellationToken cancellationToken)
    {
        var response = await queries.ListAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(response);
    }

    private static async Task<IResult> GetAccountAsync(
        string accountId, HttpContext http, AccountQueries queries, CancellationToken cancellationToken)
    {
        // A malformed id is the same 404 as a missing one and as another space's (NFR-80, M341-01 (a)).
        if (!TryReadId(accountId, out var id))
        {
            return NotFound(http);
        }

        var account = await queries.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return account is null ? NotFound(http) : TypedResults.Ok(account);
    }

    private static async Task<IResult> CreateAccountAsync(
        HttpContext http,
        CreateAccountHandler handler,
        ILogger<LedgerRequests> logger,
        CancellationToken cancellationToken)
    {
        const string operation = LedgerTelemetry.OperationCreate;

        var body = await ReadBodyAsync<CreateAccountRequest>(http, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Reject(http, logger, operation, LedgerErrorCodes.BodyInvalid);
        }

        if (!AccountName.TryCreate(body.Name, out var name))
        {
            return Reject(http, logger, operation, LedgerErrorCodes.NameInvalid);
        }

        if (!AccountInput.TryReadCurrency(body.Currency, out var currency))
        {
            return Reject(http, logger, operation, LedgerErrorCodes.CurrencyInvalid);
        }

        var openingError = AccountInput.ReadOpening(currency, body.OpeningBalance, body.OpeningDate, out var balance, out var date);
        if (openingError is not null)
        {
            return Reject(http, logger, operation, openingError);
        }

        var result = await handler
            .HandleAsync(new CreateAccount(name, currency, balance, date), cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome == LedgerOutcome.Succeeded
            ? TypedResults.Created($"/api/ledger/accounts/{result.Account!.Id:D}", result.Account)
            : Refusal(http, result.Outcome);
    }

    private static async Task<IResult> RenameAccountAsync(
        string accountId,
        HttpContext http,
        RenameAccountHandler handler,
        ILogger<LedgerRequests> logger,
        CancellationToken cancellationToken)
    {
        const string operation = LedgerTelemetry.OperationRename;

        if (!TryReadId(accountId, out var id))
        {
            return NotFound(http);
        }

        var body = await ReadBodyAsync<RenameAccountRequest>(http, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Reject(http, logger, operation, LedgerErrorCodes.BodyInvalid);
        }

        if (!AccountName.TryCreate(body.Name, out var name))
        {
            return Reject(http, logger, operation, LedgerErrorCodes.NameInvalid);
        }

        var result = await handler.HandleAsync(new RenameAccount(id, name), cancellationToken).ConfigureAwait(false);

        return result.Outcome == LedgerOutcome.Succeeded
            ? TypedResults.Ok(result.Account!)
            : Refusal(http, result.Outcome);
    }

    private static async Task<IResult> ChangeAccountCurrencyAsync(
        string accountId,
        HttpContext http,
        ChangeAccountCurrencyHandler handler,
        ILogger<LedgerRequests> logger,
        CancellationToken cancellationToken)
    {
        const string operation = LedgerTelemetry.OperationCurrencyChange;

        if (!TryReadId(accountId, out var id))
        {
            return NotFound(http);
        }

        var body = await ReadBodyAsync<ChangeAccountCurrencyRequest>(http, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Reject(http, logger, operation, LedgerErrorCodes.BodyInvalid);
        }

        if (!AccountInput.TryReadCurrency(body.Currency, out var currency))
        {
            return Reject(http, logger, operation, LedgerErrorCodes.CurrencyInvalid);
        }

        var openingError = AccountInput.ReadOpening(currency, body.OpeningBalance, body.OpeningDate, out var balance, out var date);
        if (openingError is not null)
        {
            return Reject(http, logger, operation, openingError);
        }

        var result = await handler
            .HandleAsync(new ChangeAccountCurrency(id, currency, balance, date), cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome == LedgerOutcome.Succeeded
            ? TypedResults.Ok(result.Account!)
            : Refusal(http, result.Outcome);
    }

    /// <summary>
    /// Only the canonical hyphenated form (<c>D</c>) is an id; anything else, including braces, no hyphens and an
    /// empty guid's look-alikes, is "not found" (G3 M341-01 (a)).
    /// </summary>
    private static bool TryReadId(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    /// <summary>
    /// Reads the body in the G2 order, each failure a <see langword="null"/> (a 400 that writes nothing): a JSON
    /// content type with an absent or <c>utf-8</c> charset, the 4 KiB limit, then the strict parse. A parser
    /// exception is never logged and its message never used: it can echo a member name or a value.
    /// </summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpContext http, CancellationToken cancellationToken)
        where T : class
    {
        var request = http.Request;

        if (!request.HasJsonContentType() || !HasAcceptableCharset(request))
        {
            return null;
        }

        if (!LimitBody(request))
        {
            return null;
        }

        try
        {
            return await request.ReadFromJsonAsync<T>(LedgerJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Only an absent or <c>utf-8</c> charset is accepted; any other would make <c>ReadFromJsonAsync</c> throw a logged 500 (G6-25-01).</summary>
    private static bool HasAcceptableCharset(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
        {
            return false;
        }

        var charset = mediaType.Charset.Value;
        return string.IsNullOrEmpty(charset) || string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enforces the 4 KiB limit. When the server lets the endpoint set it, set it. When the feature is read-only
    /// or absent, the limit would silently vanish, so refuse unless <c>Content-Length</c> is present and within it.
    /// A declared length above the limit is refused either way.
    /// </summary>
    private static bool LimitBody(HttpRequest request)
    {
        var declared = request.ContentLength;
        if (declared is > MaxBodyBytes)
        {
            return false;
        }

        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = MaxBodyBytes;
            return true;
        }

        return declared is >= 0 and <= MaxBodyBytes;
    }

    /// <summary>A 400 at the endpoint: a count and a Warning with the bounded reason <c>invalid</c>, and a body with only the code.</summary>
    private static ProblemHttpResult Reject(HttpContext http, ILogger logger, string operation, string code)
    {
        LedgerWriteReport.Refused(logger, null, operation, LedgerTelemetry.ReasonInvalid);
        return Problem(http, StatusCodes.Status400BadRequest, code);
    }

    private static ProblemHttpResult Refusal(HttpContext http, LedgerOutcome outcome) => outcome switch
    {
        LedgerOutcome.Forbidden => Problem(http, StatusCodes.Status403Forbidden),
        LedgerOutcome.NotFound => NotFound(http),
        LedgerOutcome.LimitReached => Problem(
            http,
            StatusCodes.Status409Conflict,
            LedgerErrorCodes.AccountLimitReached,
            detail: $"A space can hold at most {AccountLimits.PerSpace} accounts.",
            limit: AccountLimits.PerSpace),
        LedgerOutcome.CurrencyLocked => Problem(http, StatusCodes.Status409Conflict, LedgerErrorCodes.CurrencyLocked),
        LedgerOutcome.CurrencyUnchanged => Problem(http, StatusCodes.Status400BadRequest, LedgerErrorCodes.CurrencyUnchanged),
        _ => throw new InvalidOperationException("The ledger handler returned an undefined outcome."),
    };

    /// <summary>The one 404: the same body for a missing id, another space's id and a malformed id. No code.</summary>
    private static ProblemHttpResult NotFound(HttpContext http) => Problem(http, StatusCodes.Status404NotFound);

    /// <summary>
    /// Every problem body carries <c>status</c>, <c>title</c> and <c>traceId</c>, plus a <c>code</c> for a 400 and the
    /// two 409s (and <c>limit</c> and <c>detail</c> for the limit). A 403 and a 404 carry no code. No body
    /// contains an entity, column, value or name.
    /// </summary>
    private static ProblemHttpResult Problem(
        HttpContext http, int statusCode, string? code = null, string? detail = null, int? limit = null)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["traceId"] = http.TraceIdentifier,
        };

        if (code is not null)
        {
            extensions["code"] = code;
        }

        if (limit is { } value)
        {
            extensions["limit"] = value;
        }

        return TypedResults.Problem(statusCode: statusCode, detail: detail, extensions: extensions);
    }
}

/// <summary>The logger category of <see cref="LedgerEndpoints"/> (a static class cannot be a type argument).</summary>
internal sealed class LedgerRequests;
