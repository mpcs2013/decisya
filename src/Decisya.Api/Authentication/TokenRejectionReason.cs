using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Authentication;

/// <summary>
/// The closed list of reasons an <c>auth.token.rejected</c> event can carry (G2 D4, G3 G4-121-04 a).
/// The reason comes from the exception's <b>type</b> only, never its message, and a bad algorithm is
/// decided from the unvalidated header <c>alg</c> not being on the allow-list; the header value
/// itself is never logged.
/// </summary>
internal static class TokenRejectionReason
{
    internal const string Expired = "expired";
    internal const string BadSignature = "bad_signature";
    internal const string BadIssuer = "bad_issuer";
    internal const string BadAudience = "bad_audience";
    internal const string BadAlgorithm = "bad_algorithm";
    internal const string Malformed = "malformed";
    internal const string BadType = "bad_type";
    internal const string Other = "other";

    /// <summary>Every value this class can return: the test pins this list.</summary>
    internal static readonly string[] All =
        [Expired, BadSignature, BadIssuer, BadAudience, BadAlgorithm, Malformed, BadType, Other];

    private static readonly string[] AllowedAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256];

    /// <summary>Maps a failed validation to its reason. <paramref name="authorizationHeader"/> is read only to decide <see cref="BadAlgorithm"/>.</summary>
    internal static string From(Exception? exception, string? authorizationHeader)
    {
        // The handler wraps several validator failures in an AggregateException; the first one decides.
        if (exception is AggregateException aggregate)
        {
            exception = aggregate.InnerExceptions.FirstOrDefault();
        }

        if (exception is SecurityTokenMalformedException or ArgumentException)
        {
            return Malformed;
        }

        // alg=none and HS256 can surface as a signature failure; the header decides, not the exception.
        if (exception is SecurityTokenInvalidAlgorithmException || HeaderAlgorithmIsOffList(authorizationHeader))
        {
            return BadAlgorithm;
        }

        return exception switch
        {
            SecurityTokenExpiredException => Expired,
            // Includes SecurityTokenSignatureKeyNotFoundException, which derives from it.
            SecurityTokenInvalidSignatureException => BadSignature,
            SecurityTokenInvalidIssuerException => BadIssuer,
            SecurityTokenInvalidAudienceException => BadAudience,
            _ => Other,
        };
    }

    private static bool HeaderAlgorithmIsOffList(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorizationHeader[prefix.Length..].Trim();
        var firstDot = token.IndexOf('.', StringComparison.Ordinal);
        if (firstDot <= 0)
        {
            return false;
        }

        try
        {
            var headerBytes = Base64UrlEncoder.DecodeBytes(token[..firstDot]);
            using var header = JsonDocument.Parse(Encoding.UTF8.GetString(headerBytes));
            if (header.RootElement.ValueKind != JsonValueKind.Object
                || !header.RootElement.TryGetProperty("alg", out var alg)
                || alg.ValueKind != JsonValueKind.String)
            {
                // No readable alg: not "an off-list alg"; the exception type decides.
                return false;
            }

            return !AllowedAlgorithms.Contains(alg.GetString(), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}
