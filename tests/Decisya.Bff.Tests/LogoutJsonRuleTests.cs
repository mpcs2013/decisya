using Decisya.Bff.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Decisya.Bff.Tests;

/// <summary>
/// #26 G2 D4 (unit lane, no containers): the two pure rules behind the JSON variant of
/// <c>POST /bff/logout</c>. The end-to-end behaviour (302 versus 200 with the real handler) is in
/// <see cref="LogoutJsonTests"/>.
/// </summary>
public class LogoutJsonRuleTests
{
    [Theory]
    [InlineData("application/json", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/json;q=0.5", true)]
    [InlineData("text/html, application/json;q=0.9", true)]
    [InlineData("application/json;q=0", false)]
    [InlineData("application/json;q=0.0", false)]
    [InlineData("*/*", false)]
    [InlineData("application/*", false)]
    [InlineData("application/problem+json", false)]
    [InlineData("text/html", false)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", false)]
    [InlineData("application/jsonx", false)]
    [InlineData("not a media type", false)]
    [InlineData("", false)]
    public void Only_an_explicit_application_json_with_a_non_zero_quality_wants_json(string accept, bool expected)
    {
        var context = new DefaultHttpContext();
        if (accept.Length > 0)
        {
            context.Request.Headers.Accept = accept;
        }

        BffEndpoints.WantsJson(context.Request).Should().Be(expected);
    }

    [Fact]
    public void No_Accept_header_wants_the_redirect()
    {
        BffEndpoints.WantsJson(new DefaultHttpContext().Request).Should().BeFalse();
    }

    [Theory]
    [InlineData(302, "https://auth.test/realms/decisya/protocol/openid-connect/logout?client_id=decisya-bff", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "https://AUTH.test:443/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "http://localhost:8080/logout?x=1", "http://localhost:8080/realms/decisya", false)]
    public void A_location_on_the_authority_origin_is_accepted(int status, string location, string authority, bool requireHttps)
    {
        var act = () => BffEndpoints.EnsureEndSessionLocation(status, location, authority, requireHttps);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(200, "https://auth.test/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "/signout-callback-oidc", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "https://evil.test/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "https://auth.test.evil.test/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "https://auth.test:8443/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "https://user@evil.test/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "javascript:alert(1)", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "http://auth.test/logout", "https://auth.test/realms/decisya", true)]
    [InlineData(302, "http://auth.test/logout", "https://auth.test/realms/decisya", false)]
    [InlineData(302, "https://auth.test/logout", "http://auth.test/realms/decisya", false)]
    [InlineData(302, "https://auth.test/logout", null, true)]
    public void Any_other_location_is_refused_with_a_message_that_never_echoes_the_url(
        int status, string location, string? authority, bool requireHttps)
    {
        var act = () => BffEndpoints.EnsureEndSessionLocation(status, location, authority, requireHttps);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        if (location.Length > 0)
        {
            exception.Message.Should().NotContain(location);
        }
    }
}
