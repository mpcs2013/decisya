using Decisya.Bff.Security;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-05 (T-06): <see cref="ReturnUrlValidator.Sanitize"/> is the one place the
/// login-challenge <c>returnUrl</c> and the already-signed-in shortcut are validated, so this
/// runs at the unit level (no Docker) against every open-redirect shape G3 names. The
/// "<c>/%2F%2Fevil.test</c>, only if the decoded form reaches the check" case is not listed
/// separately here: ASP.NET Core's own query-string binding already decodes percent-encoding
/// before <c>returnUrl</c> reaches this method, so it arrives exactly as the
/// <c>//evil.test</c> case below (covered), never as a still-encoded string this method would
/// have to decode itself.
/// </summary>
public class ReturnUrlTests
{
    [Theory]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("/\t/evil.test")] // a decoded tab: browsers strip it, turning this into "//evil.test"
    [InlineData("https://evil.test")]
    [InlineData("\\\\evil.test")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsafe_or_empty_return_urls_sanitize_to_the_default_path(string? returnUrl)
    {
        ReturnUrlValidator.Sanitize(returnUrl).Should().Be(ReturnUrlValidator.Default);
    }

    [Fact]
    public void A_local_return_url_with_a_query_string_is_kept_unchanged()
    {
        ReturnUrlValidator.Sanitize("/dashboard?x=1").Should().Be("/dashboard?x=1");
    }
}
