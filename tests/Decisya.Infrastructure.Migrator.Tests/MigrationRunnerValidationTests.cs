namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// G3 G4-21-05: "Before it connects, it validates <c>Migrator:TenancyRolePassword</c> against
/// <c>^[A-Za-z0-9]{32,}$</c>. On failure it exits non-zero with a message naming the key, never
/// the value." These never touch Docker: <c>RunAsync</c> validates both configuration keys
/// before opening any connection.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationRunnerValidationTests
{
    private const string ValidPassword = "AValidAlphaNumericPassword12345678";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_throws_naming_only_the_connection_string_key_when_it_is_missing(string? ownerConnectionString)
    {
        var act = () => MigrationRunner.RunAsync(ownerConnectionString, ValidPassword, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("ConnectionStrings:decisya");
    }

    public static IEnumerable<object[]> InvalidPasswords()
    {
        yield return [null!];
        yield return [""];
        yield return ["short"];
        yield return [new string('a', 31)]; // one character short of the 32-character minimum
        yield return ["has-a-hyphen-and-is-long-enough-1234567890"];
        yield return ["has'a'quote'and'is'long'enough'1234567890"];
        yield return ["has a space and is long enough 1234567890"];
        yield return [ValidPassword + "\n"]; // G6-21-01: "$" matches before a trailing newline; "\z" does not
    }

    [Theory]
    [MemberData(nameof(InvalidPasswords))]
    public async Task RunAsync_throws_naming_only_the_password_key_never_its_value_when_the_password_is_invalid(string? password)
    {
        var act = () => MigrationRunner.RunAsync("Host=db.invalid;Database=decisya", password, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("Migrator:TenancyRolePassword");

        if (!string.IsNullOrEmpty(password))
        {
            assertion.Which.Message.Should().NotContain(password);
        }
    }

    [Fact]
    public async Task RunAsync_validates_the_password_shape_before_it_ever_opens_a_connection()
    {
        // An owner connection string that cannot possibly resolve (no such host); if RunAsync
        // tried to connect before validating the password, this would fail with a Npgsql
        // connection error instead of the expected InvalidOperationException.
        const string unreachableOwnerConnectionString = "Host=db.invalid;Database=decisya;Timeout=1";

        var act = () => MigrationRunner.RunAsync(unreachableOwnerConnectionString, "short", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
