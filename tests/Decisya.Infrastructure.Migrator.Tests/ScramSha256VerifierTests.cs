namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// G3 G4-21-05: "ScramSha256Verifier against a known vector, and the plaintext never appears in
/// an executed statement." <see cref="ScramSha256Verifier"/> is <c>internal</c>, reachable here
/// via the migrator project's <c>InternalsVisibleTo</c>. The strongest "known vector" proof —
/// that Postgres itself accepts the computed verifier for the exact plaintext password it was
/// derived from — is an end-to-end round trip against a real server, not a hardcoded byte
/// string: see <see cref="MigrationRunnerIntegrationTests.The_computed_SCRAM_verifier_authenticates_with_the_original_plaintext_password_and_is_stored_in_verifier_form"/>.
/// The tests below cover what needs no database: the verifier's own text shape (RFC 5802 /
/// Postgres's on-disk format) and that its own output never leaks the plaintext it was computed
/// from.
/// </summary>
[Trait("Category", "Unit")]
public class ScramSha256VerifierTests
{
    private const string Password = "PlaintextPasswordThatIsLongEnough12345";

    [Fact]
    public void Compute_produces_the_Postgres_SCRAM_SHA_256_verifier_text_shape()
    {
        var verifier = ScramSha256Verifier.Compute(Password);

        // "SCRAM-SHA-256$<iterations>:<salt>$<StoredKey>:<ServerKey>", each component
        // base64-encoded — the exact on-disk shape Postgres's pg_authid.rolpassword stores and
        // that ALTER ROLE ... PASSWORD '<verifier>' accepts directly, per Postgres's own SCRAM
        // verifier format.
        verifier.Should().MatchRegex(@"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/]+=*\$[A-Za-z0-9+/]+=*:[A-Za-z0-9+/]+=*$");
    }

    [Fact]
    public void Compute_never_embeds_the_plaintext_password_in_its_own_output()
    {
        var verifier = ScramSha256Verifier.Compute(Password);

        verifier.Should().NotContain(Password);
    }

    [Fact]
    public void Compute_is_randomly_salted_so_two_calls_for_the_same_password_never_produce_the_same_verifier()
    {
        var first = ScramSha256Verifier.Compute(Password);
        var second = ScramSha256Verifier.Compute(Password);

        first.Should().NotBe(second);
    }

    /// <summary>
    /// Structural proof (T-14): <c>MigrationRunner.ProvisionTenancyRoleAsync</c> reads its
    /// <c>password</c> parameter exactly twice — its own declaration, and the single call that
    /// turns it into a verifier — and never again, so the plaintext can never reach a bound SQL
    /// parameter, a <c>format()</c> argument or an interpolated string anywhere else in that
    /// method.
    /// </summary>
    [Fact]
    public void ProvisionTenancyRoleAsync_uses_the_plaintext_password_only_to_compute_the_verifier()
    {
        var sourcePath = RepoPaths.Find(Path.Combine("src", "Decisya.Infrastructure.Migrator", "MigrationRunner.cs"));
        var content = File.ReadAllText(sourcePath);

        // The exact declaration text, not "ProvisionTenancyRoleAsync" alone: RunAsync's own call
        // site (await ProvisionTenancyRoleAsync(...)) appears earlier in the file.
        var methodBody = ExtractMethodBody(
            content, "private static async Task ProvisionTenancyRoleAsync", "private static string QuoteIdentifier");

        var wordBoundaryPasswordOccurrences = System.Text.RegularExpressions.Regex.Count(methodBody, @"\bpassword\b");

        wordBoundaryPasswordOccurrences.Should().Be(
            2, "the parameter declaration and the single ScramSha256Verifier.Compute(password) call — never in SQL text or a bound parameter");
        methodBody.Should().Contain("ScramSha256Verifier.Compute(password)");
    }

    private static string ExtractMethodBody(string content, string methodName, string nextMethodName)
    {
        var start = content.IndexOf(methodName, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"'{methodName}' should exist in the source file");

        var end = content.IndexOf(nextMethodName, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"'{nextMethodName}' should follow '{methodName}' in the source file");

        return content[start..end];
    }
}
