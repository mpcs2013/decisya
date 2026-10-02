using System.Text.Json;
using Decisya.Modules.Admin.Contracts;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using NodaTime;
using NodaTime.Text;

namespace Decisya.Modules.Admin.Endpoints;

/// <summary>
/// The three platform-admin routes under <c>/api/admin</c> (issue #25, G2 "HTTP contract"). The
/// group requires <see cref="AdminModule.PlatformAdminPolicy"/>, so every route added to it later
/// inherits the policy. The endpoints parse the path and the one body, call
/// <see cref="IEntitlementAdminCommands"/> and map its result. They carry no <c>[AllowCrossTenant]</c>
/// attribute and mint no tenant scope: the target tenant is a parsed value handed to the facade.
/// </summary>
internal static class AdminEndpoints
{
    /// <summary>The largest request body the grant route reads (G3 G4-25-04).</summary>
    internal const int MaxBodyBytes = 8192;

    public static RouteGroupBuilder Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").RequireAuthorization(AdminModule.PlatformAdminPolicy);

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        group.MapPost("/tenants/{tenantId}/trial", StartTrialAsync)
            .WithMetadata(new AdminActionMetadata(AdminActionMetadata.StartTrial));
        group.MapPut("/tenants/{tenantId}/overrides/{featureKey}", GrantOverrideAsync)
            .WithMetadata(new AdminActionMetadata(AdminActionMetadata.GrantOverride));
        group.MapDelete("/tenants/{tenantId}/overrides/{featureKey}", RevokeOverrideAsync)
            .WithMetadata(new AdminActionMetadata(AdminActionMetadata.RevokeOverride));

        return group;
    }

    // A body on this route is ignored: not bound, not read.
    private static async Task<IResult> StartTrialAsync(
        string tenantId,
        IEntitlementAdminCommands commands,
        ILogger<AdminRequests> logger,
        CancellationToken cancellationToken)
    {
        const string action = AdminActionMetadata.StartTrial;

        if (!TenantId.TryParse(tenantId, out var target))
        {
            return Reject(logger, action, "tenant_invalid", EntitlementAdminErrorCodes.TenantInvalid);
        }

        var result = await commands.StartTrialAsync(target, cancellationToken).ConfigureAwait(false);
        return Complete(logger, action, target, result);
    }

    private static async Task<IResult> GrantOverrideAsync(
        string tenantId,
        string featureKey,
        HttpRequest request,
        IEntitlementAdminCommands commands,
        ILogger<AdminRequests> logger,
        CancellationToken cancellationToken)
    {
        const string action = AdminActionMetadata.GrantOverride;

        if (!TenantId.TryParse(tenantId, out var target))
        {
            return Reject(logger, action, "tenant_invalid", EntitlementAdminErrorCodes.TenantInvalid);
        }

        if (!FeatureKey.TryCreate(featureKey, out var feature))
        {
            return Reject(logger, action, "feature_invalid", EntitlementAdminErrorCodes.FeatureUnknown);
        }

        if (!request.HasJsonContentType() || !HasAcceptableCharset(request))
        {
            return Reject(logger, action, "content_type");
        }

        if (!LimitBody(request))
        {
            return Reject(logger, action, "body_too_large");
        }

        OverrideGrantRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<OverrideGrantRequest>(AdminJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
        {
            // Deliberately not logged and the message never used (G3 G4-25-04): a parser message can
            // echo part of the body, which carries the free-text reason.
            return Reject(logger, action, "body_invalid");
        }

        if (body?.Reason is not { } reason)
        {
            return Reject(logger, action, "body_invalid");
        }

        Instant? expiresAt = null;
        if (body.ExpiresAt is { } expiresAtText)
        {
            var parsed = InstantPattern.ExtendedIso.Parse(expiresAtText);
            if (!parsed.Success)
            {
                return Reject(logger, action, "expires_invalid");
            }

            expiresAt = parsed.Value;
        }

        var result = await commands.GrantOverrideAsync(target, feature, reason, expiresAt, cancellationToken).ConfigureAwait(false);
        return Complete(logger, action, target, result);
    }

    // A body on this route is ignored: not bound, not read.
    private static async Task<IResult> RevokeOverrideAsync(
        string tenantId,
        string featureKey,
        IEntitlementAdminCommands commands,
        ILogger<AdminRequests> logger,
        CancellationToken cancellationToken)
    {
        const string action = AdminActionMetadata.RevokeOverride;

        if (!TenantId.TryParse(tenantId, out var target))
        {
            return Reject(logger, action, "tenant_invalid", EntitlementAdminErrorCodes.TenantInvalid);
        }

        if (!FeatureKey.TryCreate(featureKey, out var feature))
        {
            return Reject(logger, action, "feature_invalid", EntitlementAdminErrorCodes.FeatureUnknown);
        }

        var result = await commands.RevokeOverrideAsync(target, feature, cancellationToken).ConfigureAwait(false);
        return Complete(logger, action, target, result);
    }

    /// <summary>
    /// G6-25-01: only an absent or <c>utf-8</c> charset is accepted. Any other value would make
    /// <c>ReadFromJsonAsync</c> throw <see cref="InvalidOperationException"/> (a logged 500).
    /// </summary>
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
    /// Enforces the 8 KiB limit (G3 G4-25-04). When the server lets the endpoint set it, set it.
    /// When the feature is read-only or absent, the limit would silently vanish, so refuse unless
    /// <c>Content-Length</c> is present and within it. A declared length above the limit is refused
    /// either way.
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

    /// <summary>A 400 at the endpoint: a fixed log line with a bounded reason code, and a generic ProblemDetails (a <c>code</c> only for the two id errors).</summary>
    private static ProblemHttpResult Reject(ILogger logger, string action, string reasonCode, string? code = null)
    {
        AdminLog.RequestRejected(logger, action, reasonCode);
        AdminTelemetry.RecordRequest(action, AdminTelemetry.OutcomeInvalid);
        return Problem(StatusCodes.Status400BadRequest, code);
    }

    private static IResult Complete(ILogger logger, string action, TenantId target, EntitlementAdminResult result)
    {
        var (outcome, status) = result.Status switch
        {
            EntitlementAdminStatus.Succeeded => (AdminTelemetry.OutcomeSucceeded, StatusCodes.Status204NoContent),
            EntitlementAdminStatus.Forbidden => (AdminTelemetry.OutcomeForbidden, StatusCodes.Status403Forbidden),
            EntitlementAdminStatus.Invalid => (AdminTelemetry.OutcomeInvalid, StatusCodes.Status400BadRequest),
            EntitlementAdminStatus.NotFound => (AdminTelemetry.OutcomeNotFound, StatusCodes.Status404NotFound),
            EntitlementAdminStatus.Conflict => (AdminTelemetry.OutcomeConflict, StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException("The entitlement admin facade returned an undefined status."),
        };

        AdminTelemetry.RecordRequest(action, outcome);
        AdminLog.RequestCompleted(logger, action, outcome, target);

        return status switch
        {
            StatusCodes.Status204NoContent => TypedResults.NoContent(),
            // A 403 carries no code: the caller must not learn which precondition refused it.
            StatusCodes.Status403Forbidden => Problem(status, null),
            _ => Problem(status, result.Code),
        };
    }

    private static ProblemHttpResult Problem(int statusCode, string? code) =>
        TypedResults.Problem(
            statusCode: statusCode,
            extensions: code is null ? null : new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>The logger category of <see cref="AdminEndpoints"/> (a static class cannot be a type argument).</summary>
internal sealed class AdminRequests;
