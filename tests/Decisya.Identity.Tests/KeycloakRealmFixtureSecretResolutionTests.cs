using Decisya.AppHost;

namespace Decisya.Identity.Tests;

/// <summary>
/// G3 "Changes to the G2 design" item 1 / G4-17-02 / G6-01: exercises
/// <see cref="KeycloakRealmFixture.ResolveSecret"/> — the <c>internal static</c> resolver
/// <see cref="KeycloakRealmFixture.EnsureStartedAsync"/> calls, not
/// <see cref="KeycloakRealmFixture.InitializeAsync"/> (a no-op; see its own doc comment for
/// why resolving secrets there broke CI's unit step) — directly, with no container and no
/// assembly-fixture lifecycle involved. The three branches: a per-run fallback on the host,
/// a naming failure when unset in CI, and a failure everywhere when set but invalid.
/// </summary>
/// <remarks>
/// Saves and restores every environment variable it touches, and never runs concurrently
/// with itself (xUnit does not parallelize test methods within one class): the one
/// process-wide <see cref="KeycloakRealmFixture"/> instance every other Integration test in
/// this project shares only resolves its own values inside <c>EnsureStartedAsync</c>'s lock,
/// which none of these tests call, so these mutations cannot affect it.
/// </remarks>
public sealed class KeycloakRealmFixtureSecretResolutionTests
{
    private const string VariableName = "DECISYA_BFF_CLIENT_SECRET";
    private const string CiVariable = "CI";
    private const string GitHubActionsVariable = "GITHUB_ACTIONS";

    [Fact]
    public void On_the_host_with_the_variable_unset_a_valid_per_run_fallback_is_generated()
    {
        using var scope = new EnvironmentScope();
        scope.Clear(VariableName, CiVariable, GitHubActionsVariable);

        var value = ResolveClientSecret();

        RealmSecretRules.IsValidClientSecret(value).Should().BeTrue();
    }

    [Theory]
    [InlineData(CiVariable)]
    [InlineData(GitHubActionsVariable)]
    public void With_the_variable_unset_in_CI_resolution_fails_naming_that_variable(string ciSignalVariable)
    {
        using var scope = new EnvironmentScope();
        scope.Clear(VariableName, CiVariable, GitHubActionsVariable);
        scope.Set(ciSignalVariable, "true");

        var exception = Record.Exception(ResolveClientSecret);

        exception.Should().NotBeNull().And.BeOfType<InvalidOperationException>();
        exception!.Message.Should().Contain(VariableName);
    }

    [Fact]
    public void A_set_but_charset_invalid_value_fails_even_on_the_host_with_CI_unset_and_the_value_never_reaches_the_message()
    {
        using var scope = new EnvironmentScope();
        scope.Clear(VariableName, CiVariable, GitHubActionsVariable);
        // Long enough to pass the length rule, but "!" is outside RealmSecretRules' charset,
        // so this is "set but invalid", not "unset" (G3 item 1's third branch).
        var invalidValue = new string('a', RealmSecretRules.ClientSecretMinLength - 1) + "!";
        scope.Set(VariableName, invalidValue);

        var exception = Record.Exception(ResolveClientSecret);

        exception.Should().NotBeNull().And.BeOfType<InvalidOperationException>();
        exception!.Message.Should().Contain(VariableName);
        exception.Message.Should().Contain("does not satisfy RealmSecretRules");
        // G6-07b: the invalid value itself must never reach the message or ToString().
        exception.Message.Should().NotContain(invalidValue);
        exception.ToString().Should().NotContain(invalidValue);
    }

    private static string ResolveClientSecret() =>
        KeycloakRealmFixture.ResolveSecret(
            VariableName,
            RealmSecretRules.IsValidClientSecret,
            () => KeycloakRealmFixture.GenerateHex(RealmSecretRules.ClientSecretMinLength));

    /// <summary>Saves the prior value of each named variable and restores it on dispose.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = [];

        public void Clear(params string[] variableNames)
        {
            foreach (var name in variableNames)
            {
                Remember(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Set(string variableName, string value)
        {
            Remember(variableName);
            Environment.SetEnvironmentVariable(variableName, value);
        }

        private void Remember(string variableName)
        {
            if (!_originalValues.ContainsKey(variableName))
            {
                _originalValues[variableName] = Environment.GetEnvironmentVariable(variableName);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
