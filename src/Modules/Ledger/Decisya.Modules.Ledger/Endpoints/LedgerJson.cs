using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decisya.Modules.Ledger.Endpoints;

/// <summary>
/// The strict serializer options for the request bodies (issue #341, G2; G3 M341-05 (a); ADR-0027 point 3).
/// Do not copy <c>AdminJson</c> instead: it lacks <see cref="JsonNumberHandling.Strict"/>, and the Web defaults
/// would read <c>"125000"</c> as an amount.
/// <list type="bullet">
/// <item>Web defaults;</item>
/// <item><see cref="JsonNumberHandling.Strict"/>: a string, a fraction (<c>12.5</c>) or an exponent (<c>1e3</c>) is no amount;</item>
/// <item>an unknown member is an error (so <c>iban</c> and <c>tenantId</c> are refused), and so is a duplicate member;</item>
/// <item>a shallow depth limit;</item>
/// <item>a constructor parameter without a default is required, and a null for a non-nullable member is an error.</item>
/// </list>
/// </summary>
internal static class LedgerJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        // The instance becomes read-only at its first use by the serializer.
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            MaxDepth = 4,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };
    }
}
