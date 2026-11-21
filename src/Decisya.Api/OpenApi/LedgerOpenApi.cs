using System.Text.Json.Nodes;
using Decisya.Modules.Entitlements.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace Decisya.Api.OpenApi;

/// <summary>
/// The generated <c>ledger</c> OpenAPI document (issue #341, G2 "OpenAPI contract"; G3 M341-06). It exists only
/// in process: <c>MapOpenApi</c> is never called and no route serves it (R-L15), so production serves no
/// document. The contract test obtains it through the keyed document provider and compares it with
/// <c>openapi/ledger.json</c> in <c>Decisya.Modules.Ledger.Contracts</c>.
/// </summary>
public static class LedgerOpenApi
{
    /// <summary>The document name and the module's route prefix.</summary>
    public const string DocumentName = "ledger";

    /// <summary>The extension every operation carries: the entitlement key its group policy requires.</summary>
    public const string FeatureExtension = "x-decisya-feature";

    /// <summary>The security scheme name: the BFF session cookie.</summary>
    public const string SessionScheme = "bffSession";

    private const string RoutePrefix = "api/ledger/";
    private const string SessionCookieName = "__Host-decisya-session";
    private const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>
    /// Adds the <c>ledger</c> document: only <c>/api/ledger</c> endpoints, with <c>x-decisya-feature</c> read from
    /// the endpoint's <c>Entitlement:</c> policy, the session-cookie security requirement and, on every non-GET
    /// operation, the required <c>X-XSRF-TOKEN</c> header. It maps no route.
    /// </summary>
    public static IServiceCollection AddLedgerOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description =>
                description.RelativePath is { } path && path.StartsWith(RoutePrefix, StringComparison.Ordinal);

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                document.Components.SecuritySchemes[SessionScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = SessionCookieName,
                    Description = "The BFF session cookie. The SPA never holds a token.",
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var feature = FeatureOf(context.Description.ActionDescriptor.EndpointMetadata);
                if (feature is not null)
                {
                    operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
                    operation.Extensions[FeatureExtension] = new JsonNodeExtension(JsonValue.Create(feature));
                }

                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SessionScheme, context.Document)] = [],
                    },
                ];

                if (!string.Equals(context.Description.HttpMethod, HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = AntiforgeryHeaderName,
                        In = ParameterLocation.Header,
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    });
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>The feature key of the endpoint's first <c>Entitlement:</c> policy, or null when it has none.</summary>
    private static string? FeatureOf(IEnumerable<object> metadata)
    {
        foreach (var item in metadata)
        {
            if (item is IAuthorizeData { Policy: { } policy }
                && policy.StartsWith(EntitlementPolicies.Prefix, StringComparison.Ordinal))
            {
                return policy[EntitlementPolicies.Prefix.Length..];
            }
        }

        return null;
    }
}
