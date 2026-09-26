using Decisya.ServiceDefaults.Logging;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>Story 4 scenario 3 and NFR-11: user_id is a one-way, keyed hash, never the raw id.</summary>
public class UserIdHasherTests
{
    [Fact]
    public void Is_deterministic_for_a_fixed_key()
    {
        var hasher = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));

        hasher.Hash("user-42").Should().Be(hasher.Hash("user-42"));
    }

    [Fact]
    public void Differs_across_keys()
    {
        var userId = "user-42";
        var first = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));
        var second = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));

        first.Hash(userId).Should().NotBe(second.Hash(userId));
    }

    [Fact]
    public void Output_never_contains_the_input()
    {
        var userId = Canaries.Unique("user-id");
        var hasher = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));

        var hash = hasher.Hash(userId);

        hash.Should().NotContain(userId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_or_null_input_hashes_to_null(string? userId)
    {
        var hasher = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));

        hasher.Hash(userId).Should().BeNull();
    }

    [Fact]
    public void Output_is_32_lowercase_hex_characters()
    {
        var hasher = new UserIdHasher(Convert.FromBase64String(Canaries.HashKey()));

        var hash = hasher.Hash("user-42");

        hash.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
