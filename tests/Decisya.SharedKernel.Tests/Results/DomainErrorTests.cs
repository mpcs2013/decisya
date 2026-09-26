using System.Text.Json;
using Decisya.SharedKernel.Results;

namespace Decisya.SharedKernel.Tests.Results;

/// <summary>
/// Story 5 — DomainError carries a stable code and category without leaking detail to a client.
/// </summary>
public class DomainErrorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_null_empty_or_whitespace_only_code_is_rejected(string? code)
    {
        var act = () => DomainError.New(code!, "message");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_null_code_specifically_throws_ArgumentNullException()
    {
        var act = () => DomainError.New(null!, "message");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_null_message_throws_ArgumentNullException()
    {
        var act = () => DomainError.New("tenant.not_found", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void An_empty_message_is_allowed()
    {
        var error = DomainError.New("tenant.not_found", string.Empty);

        error.Message.Should().BeEmpty();
    }

    [Fact]
    public void A_code_and_category_classify_the_failure()
    {
        var error = DomainError.New("tenant.not_found", "no such tenant", ErrorCategory.NotFound);

        error.Code.Should().Be("tenant.not_found");
        error.Category.Should().Be(ErrorCategory.NotFound);
    }

    [Fact]
    public void Error_exposes_no_HTTP_status_code_member()
    {
        var properties = typeof(DomainError).GetProperties();

        properties.Should().NotContain(p =>
            p.Name.Contains("Status", StringComparison.OrdinalIgnoreCase)
            || p.PropertyType == typeof(int)
            || p.PropertyType == typeof(int?));
    }

    [Fact]
    public void Category_defaults_to_Failure_when_not_specified()
    {
        var error = DomainError.New("some.code", "message");

        error.Category.Should().Be(ErrorCategory.Failure);
    }

    [Fact]
    public void An_undefined_category_throws_ArgumentOutOfRangeException()
    {
        var act = () => DomainError.New("some.code", "message", (ErrorCategory)42);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Tenant.NotFound")] // upper-case
    [InlineData("tenant..not_found")] // consecutive dots
    [InlineData(".tenant")] // leading dot
    [InlineData("tenant.")] // trailing dot
    [InlineData("1tenant")] // leading digit
    [InlineData("tenant not_found")] // embedded space
    [InlineData("tenant-not_found")] // hyphen instead of underscore
    public void A_code_that_does_not_match_the_required_pattern_is_rejected(string code)
    {
        var act = () => DomainError.New(code, "message");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("tenant.not_found")]
    [InlineData("money.currency_mismatch")]
    [InlineData("a")]
    [InlineData("a.b.c")]
    [InlineData("tenant.not_found2")]
    public void A_code_matching_the_required_pattern_is_accepted(string code)
    {
        var act = () => DomainError.New(code, "message");

        act.Should().NotThrow();
    }

    [Fact]
    public void A_code_longer_than_the_maximum_is_rejected()
    {
        var tooLong = "a" + new string('b', DomainError.MaxCodeLength);

        var act = () => DomainError.New(tooLong, "message");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Message_is_readable_by_application_code()
    {
        var error = DomainError.New("tenant.not_found", "the tenant row does not exist");

        error.Message.Should().Be("the tenant row does not exist");
    }

    [Fact]
    public void Two_errors_with_the_same_code_and_category_are_equal_regardless_of_message()
    {
        var first = DomainError.New("tenant.not_found", "message one", ErrorCategory.NotFound);
        var second = DomainError.New("tenant.not_found", "message two", ErrorCategory.NotFound);

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
        (first == second).Should().BeTrue();
    }

    [Fact]
    public void Errors_with_a_different_code_or_category_are_not_equal()
    {
        var byCode = DomainError.New("tenant.not_found", "m");
        var byOtherCode = DomainError.New("money.currency_mismatch", "m");
        var byCategory = DomainError.New("tenant.not_found", "m", ErrorCategory.NotFound);

        byCode.Should().NotBe(byOtherCode);
        byCode.Should().NotBe(byCategory);
        (byCode != byOtherCode).Should().BeTrue();
    }

    [Fact]
    public void An_error_compared_with_itself_by_reference_is_equal()
    {
        var error = DomainError.New("tenant.not_found", "m");

#pragma warning disable CS1718 // deliberate self-comparison: exercises the ReferenceEquals fast path of operator==
        (error == error).Should().BeTrue();
#pragma warning restore CS1718
    }

    [Fact]
    public void ToString_never_renders_Message()
    {
        var canary = Canaries.Unique("error-message");
        var error = DomainError.New("tenant.not_found", canary, ErrorCategory.NotFound);

        error.ToString().Should().Be("tenant.not_found (NotFound)");
        error.ToString().Should().NotContain(canary);
    }

    [Fact]
    public void An_invalid_codes_exception_never_echoes_the_code()
    {
        var canary = Canaries.Unique("invalid-code");
        var invalidCode = "Invalid " + canary;

        var act = () => DomainError.New(invalidCode, "message");

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(canary);
    }

    [Fact]
    public void Serializing_a_DomainError_never_includes_the_Message()
    {
        var canary = Canaries.Unique("serialized-message");
        var error = DomainError.New("tenant.not_found", canary, ErrorCategory.NotFound);

        var json = JsonSerializer.Serialize(error);

        json.Should().NotContain(canary);
        json.Should().Contain("\"Code\":\"tenant.not_found\"");
    }
}
