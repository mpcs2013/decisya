using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// Serializes <see cref="TenantId"/> as a plain JSON string in its canonical, lowercase
/// hyphenated form, and rejects anything else rather than substituting a default value.
/// Applied via <see cref="JsonConverterAttribute"/> on <see cref="TenantId"/> itself, so no
/// caller registers it on <see cref="JsonSerializerOptions"/> (G1 Story 2 scenario 5) — this
/// covers <c>System.Text.Json</c>-based ASP.NET Core model binding and Wolverine's default
/// serializer alike.
/// </summary>
/// <remarks>
/// N32-02 (G6 review): a DTO or message member of type <see cref="TenantId"/> that is
/// <em>absent</em> from the JSON becomes <c>default(TenantId)</c>, because this converter is
/// never invoked for a missing property — mark every such member <see langword="required"/>
/// so a missing tenant fails to deserialize instead of silently becoming uninitialized.
/// </remarks>
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    /// <summary>
    /// The raw token is rejected above this many UTF-8 bytes, before <see cref="Utf8JsonReader.GetString"/>
    /// is ever called, so an oversized token never gets allocated as a string (G3 T-03,
    /// G4-32-06). A canonical 36-character GUID plus generous quoting headroom fits many
    /// times over.
    /// </summary>
    private const int MaxRawTokenLength = 128;

    private const string InvalidTokenMessage = "A tenant identifier must be a JSON string in the canonical GUID form.";

    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A JSON null for a non-nullable TenantId member reaches this method (G1 Story 2
        // scenario 4); TenantId? is instead handled by the serializer's built-in nullable
        // wrapper before Read is ever called.
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(InvalidTokenMessage);
        }

        var rawLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (rawLength > MaxRawTokenLength)
        {
            throw new JsonException(InvalidTokenMessage);
        }

        var text = reader.GetString();
        if (!TenantId.TryParse(text.AsSpan(), out var tenantId))
        {
            throw new JsonException(InvalidTokenMessage);
        }

        return tenantId;
    }

    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
    {
        if (!value.IsInitialized)
        {
            // A message or DTO with no tenant must fail at the sender, not at the receiver
            // (ADR-0005). Writing "" would only produce JSON this converter's own Read then
            // rejects, further down the pipe.
            throw new JsonException("An uninitialized TenantId cannot be serialized.");
        }

        writer.WriteStringValue(value.Value);
    }
}
