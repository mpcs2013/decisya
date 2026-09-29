using System.Security.Cryptography;
using System.Text;

namespace Decisya.Infrastructure.Migrator;

/// <summary>
/// Computes a Postgres <c>SCRAM-SHA-256</c> password verifier client-side (issue #21, G3
/// T-14, G4-21-05, preferred mitigation): the module role's plaintext password never crosses
/// into a SQL statement, so a statement that later fails and is logged through Postgres's
/// default <c>log_min_error_statement</c> (or any other statement log) can only ever expose
/// this irreversible verifier — the same shape already stored in <c>pg_authid</c> — never the
/// password itself. This also works for a future non-superuser migrator (0.16), where
/// <c>SET LOCAL log_*</c> is not available.
/// </summary>
/// <remarks>
/// Implements RFC 5802's <c>SaltedPassword</c>/<c>ClientKey</c>/<c>StoredKey</c>/<c>ServerKey</c>
/// derivation with PBKDF2-HMAC-SHA-256, using Postgres's own default iteration count (4096) and
/// verifier text shape: <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>,
/// each binary component base64-encoded. <c>ALTER ROLE … PASSWORD '&lt;verifier&gt;'</c> accepts
/// this pre-hashed form directly; Postgres never re-derives it from a plaintext password.
/// </remarks>
internal static class ScramSha256Verifier
{
    private const int Iterations = 4096;
    private const int KeyLengthBytes = 32;
    private const int SaltLengthBytes = 16;

    private static readonly byte[] ClientKeyLabel = "Client Key"u8.ToArray();
    private static readonly byte[] ServerKeyLabel = "Server Key"u8.ToArray();

    public static string Compute(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyLengthBytes);

        var clientKey = HMACSHA256.HashData(saltedPassword, ClientKeyLabel);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(saltedPassword, ServerKeyLabel);

        return $"SCRAM-SHA-256${Iterations}:{Convert.ToBase64String(salt)}$" +
            $"{Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
