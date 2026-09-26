using Decisya.ServiceDefaults.Logging;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>
/// G4-15-23: outside Development, a missing, blank, non-base64 or too-short key fails
/// ValidateOnStart, and the failure never echoes the configured value.
/// </summary>
public class DecisyaObservabilityOptionsValidatorTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void Fails_when_the_key_is_missing_outside_Development(string environmentName)
    {
        var validator = new DecisyaObservabilityOptionsValidator(new FakeHostEnvironment(environmentName));

        var result = validator.Validate(null, new DecisyaObservabilityOptions { UserIdHashKey = null });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(DecisyaObservabilityOptions.UserIdHashKeyPath);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void Fails_for_every_bad_value_outside_Development(string environmentName)
    {
        var canary = Canaries.Unique("bad-key");
        var badValues = new[] { string.Empty, "not-base64!!!", Canaries.ShortHashKey(), canary };
        var validator = new DecisyaObservabilityOptionsValidator(new FakeHostEnvironment(environmentName));

        foreach (var badValue in badValues)
        {
            var result = validator.Validate(null, new DecisyaObservabilityOptions { UserIdHashKey = badValue });

            result.Failed.Should().BeTrue();
            if (!string.IsNullOrEmpty(badValue))
            {
                result.FailureMessage.Should().NotContain(badValue);
            }
        }
    }

    [Fact]
    public void Accepts_a_well_formed_key_in_any_environment()
    {
        var validator = new DecisyaObservabilityOptionsValidator(new FakeHostEnvironment("Production"));

        var result = validator.Validate(null, new DecisyaObservabilityOptions { UserIdHashKey = Canaries.HashKey() });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_present_but_invalid_key_fails_even_in_Development()
    {
        var validator = new DecisyaObservabilityOptionsValidator(new FakeHostEnvironment("Development"));

        var result = validator.Validate(null, new DecisyaObservabilityOptions { UserIdHashKey = "not-base64!!!" });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void A_missing_key_in_Development_is_accepted_because_the_provisioner_fills_it_in_first()
    {
        var validator = new DecisyaObservabilityOptionsValidator(new FakeHostEnvironment("Development"));

        var result = validator.Validate(null, new DecisyaObservabilityOptions { UserIdHashKey = null });

        result.Succeeded.Should().BeTrue();
    }
}
