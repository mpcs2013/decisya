using Decisya.SharedKernel.Results;

namespace Decisya.SharedKernel.Tests.Results;

/// <summary>
/// Story 4 (non-generic <see cref="Result"/>) — Result and Result&lt;T&gt; represent an
/// expected success or failure without throwing.
/// </summary>
public class ResultTests
{
    [Fact]
    public void A_successful_Result_carries_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();

        var act = () => result.Error;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_failed_Result_exposes_its_error()
    {
        var error = DomainError.New("tenant.not_found", "no such tenant");

        var result = Result.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Failure_throws_ArgumentNullException_for_a_null_error()
    {
        var act = () => Result.Failure(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void An_error_converts_implicitly_to_a_failed_Result()
    {
        var error = DomainError.New("tenant.not_found", "no such tenant");

        Result Act() => error;

        var result = Act();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Match_dispatches_to_the_success_branch_exactly_once()
    {
        var result = Result.Success();
        var successCalls = 0;
        var failureCalls = 0;

        var output = result.Match(
            onSuccess: () => { successCalls++; return "ok"; },
            onFailure: _ => { failureCalls++; return "fail"; });

        output.Should().Be("ok");
        successCalls.Should().Be(1);
        failureCalls.Should().Be(0);
    }

    [Fact]
    public void Match_dispatches_to_the_failure_branch_exactly_once_with_the_error()
    {
        var error = DomainError.New("tenant.not_found", "no such tenant");
        var result = Result.Failure(error);
        var successCalls = 0;
        DomainError? observed = null;

        var output = result.Match(
            onSuccess: () => { successCalls++; return "ok"; },
            onFailure: e => { observed = e; return "fail"; });

        output.Should().Be("fail");
        successCalls.Should().Be(0);
        observed.Should().Be(error);
    }

    [Fact]
    public void Match_throws_ArgumentNullException_for_a_null_delegate()
    {
        var result = Result.Success();

        var actNullSuccess = () => result.Match<string>(null!, _ => "fail");
        var actNullFailure = () => result.Match(() => "ok", null!);

        actNullSuccess.Should().Throw<ArgumentNullException>();
        actNullFailure.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Two_successful_results_are_equal()
    {
        Result.Success().Should().Be(Result.Success());
        (Result.Success() == Result.Success()).Should().BeTrue();
    }

    [Fact]
    public void Two_failed_results_with_equal_errors_are_equal()
    {
        var first = Result.Failure(DomainError.New("tenant.not_found", "m1"));
        var second = Result.Failure(DomainError.New("tenant.not_found", "m2"));

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void A_success_and_a_failure_are_never_equal()
    {
        var success = Result.Success();
        var failure = Result.Failure(DomainError.New("tenant.not_found", "m"));

        success.Should().NotBe(failure);
        (success == failure).Should().BeFalse();
        (success != failure).Should().BeTrue();
    }

    [Fact]
    public void A_result_compared_with_itself_by_reference_is_equal()
    {
        var result = Result.Failure(DomainError.New("tenant.not_found", "m"));

#pragma warning disable CS1718 // deliberate self-comparison: exercises the ReferenceEquals fast path of operator==
        (result == result).Should().BeTrue();
#pragma warning restore CS1718
    }

    [Fact]
    public void ToString_never_renders_the_errors_message()
    {
        var canary = Canaries.Unique("result-error-message");
        var result = Result.Failure(DomainError.New("tenant.not_found", canary));

        result.ToString().Should().Be("Failure: tenant.not_found (Failure)");
        result.ToString().Should().NotContain(canary);
    }

    [Fact]
    public void A_successful_ToString_is_Success()
    {
        Result.Success().ToString().Should().Be("Success");
    }
}
