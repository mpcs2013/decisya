using Decisya.SharedKernel.Results;

namespace Decisya.SharedKernel.Tests.Results;

/// <summary>
/// Story 4 (generic <see cref="Result{T}"/>) — Result and Result&lt;T&gt; represent an
/// expected success or failure without throwing.
/// </summary>
public class ResultOfTTests
{
    [Fact]
    public void A_successful_ResultOfT_exposes_its_value()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);

        var act = () => result.Error;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_failed_ResultOfT_exposes_no_value()
    {
        var error = DomainError.New("money.currency_mismatch", "mismatched currencies");

        var result = Result.Failure<int>(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);

        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_value_converts_implicitly_to_a_successful_ResultOfT()
    {
        static Result<int> Handler() => 42;

        var result = Handler();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void An_error_converts_implicitly_to_a_failed_ResultOfT()
    {
        var error = DomainError.New("money.currency_mismatch", "mismatched currencies");

        Result<int> Handler() => error;

        var result = Handler();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Match_dispatches_to_the_success_branch_exactly_once_with_the_value()
    {
        var result = Result.Success(42);
        var observed = 0;
        var failureCalls = 0;

        var output = result.Match(
            onSuccess: v => { observed = v; return "ok"; },
            onFailure: _ => { failureCalls++; return "fail"; });

        output.Should().Be("ok");
        observed.Should().Be(42);
        failureCalls.Should().Be(0);
    }

    [Fact]
    public void Match_dispatches_to_the_failure_branch_exactly_once()
    {
        var error = DomainError.New("money.currency_mismatch", "mismatched currencies");
        var result = Result.Failure<int>(error);
        var successCalls = 0;

        var output = result.Match(
            onSuccess: _ => { successCalls++; return "ok"; },
            onFailure: e => e.Code);

        output.Should().Be("money.currency_mismatch");
        successCalls.Should().Be(0);
    }

    [Fact]
    public void Two_successful_results_with_equal_values_are_equal()
    {
        Result.Success(42).Should().Be(Result.Success(42));
    }

    [Fact]
    public void Two_failed_results_with_equal_errors_are_equal()
    {
        var first = Result.Failure<int>(DomainError.New("money.currency_mismatch", "m1"));
        var second = Result.Failure<int>(DomainError.New("money.currency_mismatch", "m2"));

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void A_success_and_a_failure_are_never_equal()
    {
        var success = Result.Success(42);
        var failure = Result.Failure<int>(DomainError.New("money.currency_mismatch", "m"));

        success.Should().NotBe(failure);
    }

    [Fact]
    public void Successes_with_different_values_are_not_equal()
    {
        Result.Success(1).Should().NotBe(Result.Success(2));
        (Result.Success(1) != Result.Success(2)).Should().BeTrue();
    }

    [Fact]
    public void A_result_compared_with_itself_by_reference_is_equal()
    {
        var result = Result.Success(42);

#pragma warning disable CS1718 // deliberate self-comparison: exercises the ReferenceEquals fast path of operator==
        (result == result).Should().BeTrue();
#pragma warning restore CS1718
    }

    // --- G4-32-09: accessors throw fixed messages that never leak Value or Error.Message ---

    [Fact]
    public void Reading_Value_on_a_failure_throws_InvalidOperationException_without_leaking_the_errors_message()
    {
        var canary = Canaries.Unique("error-message-on-failure");
        var result = Result.Failure<CanaryValue>(DomainError.New("tenant.not_found", canary));

        var act = () => result.Value;

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().NotContain(canary);
    }

    [Fact]
    public void Reading_Error_on_a_success_throws_InvalidOperationException_without_leaking_the_value()
    {
        var canary = Canaries.Unique("value-on-success");
        var result = Result.Success(new CanaryValue(canary));

        var act = () => result.Error;

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().NotContain(canary);
    }

    [Fact]
    public void ToString_never_renders_the_value_or_the_errors_message_for_either_outcome()
    {
        var valueCanary = Canaries.Unique("tostring-value");
        var messageCanary = Canaries.Unique("tostring-message");

        var success = Result.Success(new CanaryValue(valueCanary));
        var failure = Result.Failure<CanaryValue>(DomainError.New("tenant.not_found", messageCanary));

        success.ToString().Should().Be("Success");
        success.ToString().Should().NotContain(valueCanary);
        failure.ToString().Should().Be("Failure: tenant.not_found (Failure)");
        failure.ToString().Should().NotContain(messageCanary);
    }

    // --- G4-32-10: no null success, no null failure ---

    [Fact]
    public void Success_throws_ArgumentNullException_for_a_null_value()
    {
        var act = () => Result.Success<string>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_implicit_conversion_from_a_null_value_throws_ArgumentNullException()
    {
        string? value = null;

        var act = () =>
        {
            Result<string> result = value!;
            return result;
        };

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FailureOfT_throws_ArgumentNullException_for_a_null_error()
    {
        var act = () => Result.Failure<int>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_implicit_conversion_from_a_null_error_throws_ArgumentNullException()
    {
        DomainError? error = null;

        var act = () =>
        {
            Result<int> result = error!;
            return result;
        };

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>A reference-type stand-in whose <see cref="ToString"/> exposes a canary if leaked.</summary>
    private sealed record CanaryValue(string Text)
    {
        public override string ToString() => Text;
    }
}
