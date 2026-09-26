namespace Decisya.SharedKernel.Results;

/// <summary>
/// An expected success or failure, returned by value instead of thrown, so an expected
/// domain outcome (e.g. "tenant not found", "insufficient funds") doesn't pay the cost or the
/// control-flow surprise of an exception.
/// </summary>
/// <remarks>
/// Authorization and tenant checks must not be expressed as a <see cref="Result"/> a caller
/// can silently ignore: C# does not warn on a discarded non-<see cref="Task"/> return value,
/// so an ownership or authorization check that returns a <see cref="Result"/> can be called
/// and its failure never observed. Such checks belong in a pipeline that enforces them, or
/// must throw.
/// </remarks>
public sealed class Result : IEquatable<Result>
{
    private static readonly Result SuccessSingleton = new(isSuccess: true, error: null);

    private readonly DomainError? _error;

    private Result(bool isSuccess, DomainError? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    /// <summary><see langword="true"/> when this result represents a success.</summary>
    public bool IsSuccess { get; }

    /// <summary><see langword="true"/> when this result represents a failure.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure's <see cref="DomainError"/>.</summary>
    /// <exception cref="InvalidOperationException">This result is a success.</exception>
    public DomainError Error => _error ?? throw new InvalidOperationException(
        "A successful Result carries no Error; check IsSuccess/IsFailure before reading Error.");

    /// <summary>A successful, non-generic result. May return a cached instance.</summary>
    public static Result Success() => SuccessSingleton;

    /// <summary>A failed, non-generic result carrying <paramref name="error"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static Result Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(isSuccess: false, error);
    }

    /// <summary>
    /// A successful <see cref="Result{T}"/> carrying <paramref name="value"/>. Declared here,
    /// not on <see cref="Result{T}"/>, because <c>CA1000</c> ("do not declare static members
    /// on generic types") would otherwise fail the build.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static Result<T> Success<T>(T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(isSuccess: true, value, error: null);
    }

    /// <summary>
    /// A failed <see cref="Result{T}"/> carrying <paramref name="error"/>. Declared here for
    /// the same <c>CA1000</c> reason as <see cref="Success{T}"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static Result<T> Failure<T>(DomainError error) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(isSuccess: false, value: default, error);
    }

    /// <summary>Equivalent to <see cref="Failure(DomainError)"/>.</summary>
    public static implicit operator Result(DomainError error) => Failure(error);

    /// <summary>Invokes exactly one of the two delegates, exactly once.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> or <paramref name="onFailure"/> is <see langword="null"/>.</exception>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<DomainError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }

    public bool Equals(Result? other)
    {
        if (other is null || IsSuccess != other.IsSuccess)
        {
            return false;
        }

        return IsSuccess || Error.Equals(other.Error);
    }

    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    public override int GetHashCode() => IsSuccess ? 0 : HashCode.Combine(1, Error);

    /// <summary>Never renders <see cref="DomainError.Message"/> (only <see cref="DomainError.ToString"/>).</summary>
    public override string ToString() => IsSuccess ? "Success" : $"Failure: {Error}";

    public static bool operator ==(Result? left, Result? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null && left.Equals(right);
    }

    public static bool operator !=(Result? left, Result? right) => !(left == right);
}
