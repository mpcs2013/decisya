using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decisya.Modules.Admin.Endpoints;

/// <summary>
/// The strict serializer options for the one request body (issue #25, G2 D6; G3 G4-25-04): web
/// defaults, an unknown member is an error, a duplicate member is an error, and a shallow depth
/// limit. <c>{"reason":42}</c> fails because a number is not a string.
/// </summary>
internal static class AdminJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };
}
