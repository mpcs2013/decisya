using Decisya.AppHost;

namespace Decisya.Identity.Tests;

/// <summary>
/// The charset and length rule shared by the AppHost guard and this project's Testcontainers
/// fixture (G2). Accept and reject cases, including every forbidden character named by
/// G3 T-04.
/// </summary>
public class RealmSecretRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_empty_or_whitespace_only_values_are_rejected(string? value)
    {
        RealmSecretRules.IsValidDevPassword(value).Should().BeFalse();
        RealmSecretRules.IsValidClientSecret(value).Should().BeFalse();
    }

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("has\\backslash")]
    [InlineData("has$dollar")]
    [InlineData("has{brace")]
    [InlineData("has}brace")]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    public void Values_with_a_character_outside_the_charset_are_rejected(string value)
    {
        // Padded to at least the dev-password minimum length, so a charset violation
        // (not a length violation) is what fails the check.
        var padded = value.PadRight(RealmSecretRules.DevPasswordMinLength, 'a');

        RealmSecretRules.IsValidDevPassword(padded).Should().BeFalse();
    }

    [Fact]
    public void A_dev_password_shorter_than_the_minimum_is_rejected()
    {
        var tooShort = new string('a', RealmSecretRules.DevPasswordMinLength - 1);

        RealmSecretRules.IsValidDevPassword(tooShort).Should().BeFalse();
    }

    [Fact]
    public void A_dev_password_longer_than_the_maximum_is_rejected()
    {
        var tooLong = new string('a', RealmSecretRules.DevPasswordMaxLength + 1);

        RealmSecretRules.IsValidDevPassword(tooLong).Should().BeFalse();
    }

    [Fact]
    public void A_dev_password_at_the_boundary_lengths_is_accepted()
    {
        RealmSecretRules.IsValidDevPassword(new string('a', RealmSecretRules.DevPasswordMinLength)).Should().BeTrue();
        RealmSecretRules.IsValidDevPassword(new string('a', RealmSecretRules.DevPasswordMaxLength)).Should().BeTrue();
    }

    [Fact]
    public void A_client_secret_shorter_than_the_minimum_is_rejected()
    {
        var tooShort = new string('a', RealmSecretRules.ClientSecretMinLength - 1);

        RealmSecretRules.IsValidClientSecret(tooShort).Should().BeFalse();
    }

    [Fact]
    public void A_client_secret_longer_than_the_maximum_is_rejected()
    {
        var tooLong = new string('a', RealmSecretRules.ClientSecretMaxLength + 1);

        RealmSecretRules.IsValidClientSecret(tooLong).Should().BeFalse();
    }

    [Fact]
    public void A_client_secret_at_the_boundary_lengths_is_accepted()
    {
        RealmSecretRules.IsValidClientSecret(new string('a', RealmSecretRules.ClientSecretMinLength)).Should().BeTrue();
        RealmSecretRules.IsValidClientSecret(new string('a', RealmSecretRules.ClientSecretMaxLength)).Should().BeTrue();
    }

    [Fact]
    public void Hex_from_a_CSPRNG_satisfies_both_rules()
    {
        RealmSecretRules.IsValidClientSecret(Canaries.SecretShaped(RealmSecretRules.ClientSecretMinLength)).Should().BeTrue();
        RealmSecretRules.IsValidDevPassword(Canaries.SecretShaped(RealmSecretRules.DevPasswordMinLength)).Should().BeTrue();
    }

    [Fact]
    public void The_literal_unresolved_placeholder_text_is_rejected_by_both_rules()
    {
        RealmSecretRules.IsValidClientSecret(RealmSecretRules.BffClientSecretPlaceholderLiteral).Should().BeFalse();
        RealmSecretRules.IsValidDevPassword(RealmSecretRules.DevUserPasswordPlaceholderLiteral).Should().BeFalse();
    }

    [Fact]
    public void EnsureDevUserPassword_does_not_throw_for_a_valid_value()
    {
        var value = Canaries.SecretShaped(RealmSecretRules.DevPasswordMinLength);

        var exception = Record.Exception(() => RealmSecretRules.EnsureDevUserPassword(value));

        exception.Should().BeNull();
    }

    [Fact]
    public void EnsureDevUserPassword_throws_for_an_invalid_value_without_ever_naming_it()
    {
        // Deliberately invalid (contains '!', outside the charset) so the guard always
        // throws, and unique so a leak into the message would be detectable.
        var canary = $"canary-{Guid.NewGuid():N}!not-a-valid-secret";

        var exception = Record.Exception(() => RealmSecretRules.EnsureDevUserPassword(canary));

        exception.Should().NotBeNull();
        exception!.Message.Should().NotContain(canary);
        exception.ToString().Should().NotContain(canary);
    }

    [Fact]
    public void EnsureDevUserPassword_throws_for_a_missing_value()
    {
        var exception = Record.Exception(() => RealmSecretRules.EnsureDevUserPassword(null));

        exception.Should().NotBeNull();
        exception!.Message.Should().Contain("Parameters:dev-user-password");
    }
}
