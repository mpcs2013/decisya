using System.Buffers.Text;
using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// Decodes a JWT's header and payload without any JWT or Keycloak-SDK package (G2): a
/// compact JWT is three base64url segments joined by <c>.</c>, and only the first two
/// (header, payload) are ever inspected here — signature verification is #20's job, not
/// this issue's.
/// </summary>
internal static class JwtHelper
{
    public static JsonElement DecodeHeader(string jwt) => Decode(jwt, segmentIndex: 0);

    public static JsonElement DecodePayload(string jwt) => Decode(jwt, segmentIndex: 1);

    private static JsonElement Decode(string jwt, int segmentIndex)
    {
        var segments = jwt.Split('.');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException("A compact JWT has at least a header and a payload segment.");
        }

        var bytes = Base64Url.DecodeFromChars(segments[segmentIndex]);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
}
