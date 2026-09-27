using Decisya.AppHost;

namespace Decisya.Identity.Tests;

/// <summary>
/// G3 "Changes to the G2 design" item 1 / G4-17-02: <see cref="KeycloakRealmFixture.InitializeAsync"/>
/// resolves <c>DECISYA_BFF_CLIENT_SECRET</c> and <c>DECISYA_DEV_USER_PASSWORD</c> from the
/// environment without ever touching Docker (see its own doc comment), so the three branches
/// of that resolution are exercised here directly, with no container involved: unset on the
/// host (a per-run fallback is generated), unset with <c>CI</c>/<c>GITHUB_ACTIONS</c> set
/// (the fixture fails instead of silently generating a value nobody set), and set but invalid
/// (fails everywhere, regardless of <c>CI</c>).
/// </summary>
/// <remarks>
/// Saves and restores the four environment variables it touches, and never runs concurrently
/// with itself (xUnit does not parallelize test methods within one class): the one process-wide
/// <see cref="KeycloakRealmFixture"/> instance every other Integration test in this project
/// shares already finished resolving its own values, once, before any test in the assembly
/// runs (xUnit v3's assembly-fixture contract), so these mutations cannot affect it.
/// </remarks>
public sealed class KeycloakRealmFixtureSecretResolutionTests
{
    private const string ClientSecretVariable = "DECISYA_BFF_CLIENT_SECRET";
    private const string DevPasswordVariable = "DECISYA_DEV_USER_PASSWORD";
    private const string CiVariable = "CI";
    private const string GitHubActionsVariable = "GITHUB_ACTIONS";

    [Fact]
    public async Task On_the_host_with_both_variables_unset_a_valid_per_run_fallback_is_generated()
    {
        using var scope = new EnvironmentScope();
        scope.Clear(ClientSecretVariable, DevPasswordVariable, CiVariable, GitHubActionsVariable);

        await using var fixture = new KeycloakRealmFixture();
        await fixture.InitializeAsync();

        RealmSecretRules.IsValidClientSecret(fixture.ClientSecret).Should().BeTrue();
        RealmSecretRules.IsValidDevPassword(fixture.DevUserPassword).Should().BeTrue();
    }

    [Theory]
    [InlineData(CiVariable)]
    [InlineData(GitHubActionsVariable)]
    public async Task With_the_client_secret_variable_unset_in_CI_initialisation_fails_naming_that_variable(string ciSignalVariable)
    {
        using var scope = new EnvironmentScope();
        scope.Clear(ClientSecretVariable, DevPasswordVariable, CiVariable, GitHubActionsVariable);
        scope.Set(ciSignalVariable, "true");

        await using var fixture = new KeycloakRealmFixture();
        var exception = await Record.ExceptionAsync(async () => await fixture.InitializeAsync());

        exception.Should().NotBeNull().And.BeOfType<InvalidOperationException>();
        exception!.Message.Should().Contain(ClientSecretVariable);
    }

    [Fact]
    public async Task With_only_the_dev_password_variable_unset_in_CI_initialisation_fails_naming_that_variable()
    {
        using var scope = new EnvironmentScope();
        scope.Clear(ClientSecretVariable, DevPasswordVariable, CiVariable, GitHubActionsVariable);
        scope.Set(CiVariable, "true");
        scope.Set(ClientSecretVariable, Canaries.SecretShaped(RealmSecretRules.ClientSecretMinLength));

        await using var fixture = new KeycloakRealmFixture();
        var exception = await Record.ExceptionAsync(async () => await fixture.InitializeAsync());

        exception.Should().NotBeNull().And.BeOfType<InvalidOperationException>();
        exception!.Message.Should().Contain(DevPasswordVariable);
    }

    [Fact]
    public async Task A_set_but_charset_invalid_value_fails_even_on_the_host_with_CI_unset()
    {
        using var scope = new EnvironmentScope();
        scope.Clear(ClientSecretVariable, DevPasswordVariable, CiVariable, GitHubActionsVariable);
        // Long enough to pass the length rule, but "!" is outside RealmSecretRules' charset,
        // so this is "set but invalid", not "unset" (G3 item 1's third branch).
        scope.Set(ClientSecretVariable, new string('a', RealmSecretRules.ClientSecretMinLength - 1) + "!");

        await using var fixture = new KeycloakRealmFixture();
        var exception = await Record.ExceptionAsync(async () => await fixture.InitializeAsync());

        exception.Should().NotBeNull().And.BeOfType<InvalidOperationException>();
        exception!.Message.Should().Contain(ClientSecretVariable);
        exception.Message.Should().Contain("does not satisfy RealmSecretRules");
    }

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
