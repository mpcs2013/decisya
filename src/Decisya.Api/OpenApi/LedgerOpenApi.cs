using System.Globalization;
using System.Text.Json.Nodes;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Ledger.Contracts;
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

            // G2 / ADR-0027: the generator reads the host's default JSON options, not LedgerJson's, so it shows the
            // number members as "integer or string" and knows neither the safe range, the closed request shapes nor
            // the problem codes. State what the code does (Strict numbers, Disallow unknown members, bounded codes).
            options.AddDocumentTransformer((document, _, _) =>
            {
                var schemas = document.Components?.Schemas;
                if (schemas is null)
                {
                    return Task.CompletedTask;
                }

                if (Schema(schemas, "MoneyDto") is { } money)
                {
                    money.AdditionalPropertiesAllowed = false;
                    if (Property(money, "amountMinor") is { } amount)
                    {
                        amount.Type = JsonSchemaType.Integer;
                        amount.Pattern = null;
                        amount.Minimum = MoneyDto.MinAmountMinor.ToString(CultureInfo.InvariantCulture);
                        amount.Maximum = MoneyDto.MaxAmountMinor.ToString(CultureInfo.InvariantCulture);
                    }

                    if (Property(money, "currency") is { } currency)
                    {
                        currency.Pattern = "^[A-Z]{3}$";
                    }
                }

                if (Schema(schemas, "AccountUsageDto") is { } usage)
                {
                    if (Property(usage, "used") is { } used)
                    {
                        used.Type = JsonSchemaType.Integer;
                        used.Pattern = null;
                    }

                    if (Property(usage, "limit") is { } limit)
                    {
                        limit.Type = JsonSchemaType.Null | JsonSchemaType.Integer;
                        limit.Pattern = null;
                    }
                }

                foreach (var request in RequestSchemas)
                {
                    if (Schema(schemas, request) is { } requestSchema)
                    {
                        requestSchema.AdditionalPropertiesAllowed = false;
                    }
                }

                foreach (var (schemaName, member) in DateMembers)
                {
                    if (Schema(schemas, schemaName) is { } owner && Property(owner, member) is { } date)
                    {
                        date.Format = "date";
                    }
                }

                schemas["ProblemDetails"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Required = new HashSet<string>(StringComparer.Ordinal) { "status", "title", "traceId" },
                    Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                    {
                        ["type"] = new OpenApiSchema { Type = JsonSchemaType.Null | JsonSchemaType.String },
                        ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                        ["detail"] = new OpenApiSchema { Type = JsonSchemaType.Null | JsonSchemaType.String },
                        ["instance"] = new OpenApiSchema { Type = JsonSchemaType.Null | JsonSchemaType.String },
                        ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["code"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Enum = [.. ErrorCodes.Select(code => (JsonNode)JsonValue.Create(code)!)],
                        },
                        ["limit"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                    },
                };

                // The XML-doc descriptions carry the source file's line endings and indentation, so they would make
                // the committed contract depend on the checkout. The contract states shapes, not prose.
                foreach (var schema in schemas.Values)
                {
                    StripDescriptions(schema);
                }

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                // G2: a created account answers 201 with Location: /api/ledger/accounts/{id}.
                if (string.Equals(operation.OperationId, "Ledger_CreateAccount", StringComparison.Ordinal)
                    && operation.Responses is { } responses
                    && responses.TryGetValue("201", out var created)
                    && created is OpenApiResponse createdResponse)
                {
                    createdResponse.Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal)
                    {
                        ["Location"] = new OpenApiHeader
                        {
                            Description = "/api/ledger/accounts/{id}",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                        },
                    };
                }

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

    /// <summary>Every code a problem body may carry (G2 "Problem bodies"), in <c>LedgerErrorCodes</c> order.</summary>
    private static readonly string[] ErrorCodes =
    [
        LedgerErrorCodes.BodyInvalid,
        LedgerErrorCodes.NameInvalid,
        LedgerErrorCodes.CurrencyInvalid,
        LedgerErrorCodes.AmountInvalid,
        LedgerErrorCodes.CurrencyMismatch,
        LedgerErrorCodes.OpeningDateInvalid,
        LedgerErrorCodes.OpeningPairInvalid,
        LedgerErrorCodes.CurrencyUnchanged,
        LedgerErrorCodes.AccountLimitReached,
        LedgerErrorCodes.CurrencyLocked,
    ];

    private static readonly string[] RequestSchemas =
        ["CreateAccountRequest", "RenameAccountRequest", "ChangeAccountCurrencyRequest"];

    private static readonly (string Schema, string Member)[] DateMembers =
    [
        ("AccountDto", "openingDate"),
        ("CreateAccountRequest", "openingDate"),
        ("ChangeAccountCurrencyRequest", "openingDate"),
    ];

    private static void StripDescriptions(IOpenApiSchema schema)
    {
        switch (schema)
        {
            case OpenApiSchemaReference reference:
                reference.Description = null;
                break;
            case OpenApiSchema concrete:
                concrete.Description = null;
                if (concrete.Properties is { } properties)
                {
                    foreach (var property in properties.Values)
                    {
                        StripDescriptions(property);
                    }
                }

                if (concrete.OneOf is { } oneOf)
                {
                    foreach (var member in oneOf)
                    {
                        StripDescriptions(member);
                    }
                }

                if (concrete.Items is { } items)
                {
                    StripDescriptions(items);
                }

                break;
        }
    }

    private static OpenApiSchema? Schema(IDictionary<string, IOpenApiSchema> schemas, string name) =>
        schemas.TryGetValue(name, out var schema) ? schema as OpenApiSchema : null;

    private static OpenApiSchema? Property(OpenApiSchema owner, string name) =>
        owner.Properties is { } properties && properties.TryGetValue(name, out var property) ? property as OpenApiSchema : null;

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
