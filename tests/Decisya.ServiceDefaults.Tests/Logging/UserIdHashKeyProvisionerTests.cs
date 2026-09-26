using Decisya.ServiceDefaults.Logging;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>
/// G4-15-24: only an absent or blank key triggers generation, only in Development, and a
/// present-but-invalid key is left alone so the validator rejects it.
/// </summary>
public class UserIdHashKeyProvisionerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Generates_a_key_in_Development_when_absent_or_blank(string? initial)
    {
        var provisioner = new UserIdHashKeyProvisioner(new FakeHostEnvironment("Development"));
        var options = new DecisyaObservabilityOptions { UserIdHashKey = initial };

        provisioner.PostConfigure(null, options);

        options.UserIdHashKey.Should().NotBeNullOrWhiteSpace();
        DecisyaObservabilityOptionsValidator.IsAcceptableKey(options.UserIdHashKey).Should().BeTrue();
        provisioner.GeneratedEphemeralKey.Should().BeTrue();
    }

    [Fact]
    public void Never_overwrites_a_present_key_even_if_invalid()
    {
        var provisioner = new UserIdHashKeyProvisioner(new FakeHostEnvironment("Development"));
        var options = new DecisyaObservabilityOptions { UserIdHashKey = "not-base64!!!" };

        provisioner.PostConfigure(null, options);

        options.UserIdHashKey.Should().Be("not-base64!!!");
        provisioner.GeneratedEphemeralKey.Should().BeFalse();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void Never_generates_a_key_outside_Development(string environmentName)
    {
        var provisioner = new UserIdHashKeyProvisioner(new FakeHostEnvironment(environmentName));
        var options = new DecisyaObservabilityOptions { UserIdHashKey = null };

        provisioner.PostConfigure(null, options);

        options.UserIdHashKey.Should().BeNull();
        provisioner.GeneratedEphemeralKey.Should().BeFalse();
    }

    [Fact]
    public void Two_provisioners_generate_different_keys()
    {
        var first = new UserIdHashKeyProvisioner(new FakeHostEnvironment("Development"));
        var firstOptions = new DecisyaObservabilityOptions();
        first.PostConfigure(null, firstOptions);

        var second = new UserIdHashKeyProvisioner(new FakeHostEnvironment("Development"));
        var secondOptions = new DecisyaObservabilityOptions();
        second.PostConfigure(null, secondOptions);

        firstOptions.UserIdHashKey.Should().NotBe(secondOptions.UserIdHashKey);
    }
}
