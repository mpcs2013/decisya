namespace Decisya.SharedKernel.Results;

/// <summary>
/// An expected success carrying a value, or a failure — see <see cref="Result"/> for the
/// non-generic form and the authorization-and-tenant-checks caveat.
/// </summary>
/// <remarks>
/// <para>
/// Known limits of the implicit conversions below: C# does not apply a user-defined
/// conversion from an interface type, so when <typeparamref name="T"/> is an interface,
/// write <c>return Result.Success(value);</c> explicitly. <c>Result&lt;DomainError&gt;</c> is
/// not a supported instantiation, because the two implicit operators would collide. Neither this
/// type nor <see cref="Result"/> is a wire type: a Contracts DTO or Wolverine message carries
/// its own shape, never a <see cref="Result{T}"/>.
/// </para>
/// </remarks>
/// <typeparam name="T">The success value's type.</typeparam>
public sealed class Result<T> : IEquatable<Result<T>>
    where T : notnull
{
    private readonly T? _value;
    private readonly DomainError? _error;

    internal Result(bool isSuccess, T? value, DomainError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    /// <summary><see langword="true"/> when this result represents a success.</summary>
    public bool IsSuccess { get; }

    /// <summary><see langword="true"/> when this result represents a failure.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The success value.</summary>
    /// <exception cref="InvalidOperationException">This result is a failure.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            "A failed Result<T> carries no Value; check IsSuccess/IsFailure before reading Value.");

    /// <summary>The failure's <see cref="DomainError"/>.</summary>
    /// <exception cref="InvalidOperationException">This result is a success.</exception>
    public DomainError Error => _error ?? throw new InvalidOperationException(
        "A successful Result<T> carries no Error; check IsSuccess/IsFailure before reading Error.");

    /// <summary>Equivalent to <see cref="Result.Success{T}(T)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static implicit operator Result<T>(T value) => Result.Success(value);

    /// <summary>Equivalent to <see cref="Result.Failure{T}(DomainError)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static implicit operator Result<T>(DomainError error) => Result.Failure<T>(error);

    /// <summary>Invokes exactly one of the two delegates, exactly once.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="onSuccess"/> or <paramref name="onFailure"/> is <see langword="null"/>.</exception>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<DomainError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error);
    }

    public bool Equals(Result<T>? other)
    {
        if (other is null || IsSuccess != other.IsSuccess)
        {
            return false;
        }

        return IsSuccess
            ? EqualityComparer<T>.Default.Equals(_value!, other._value!)
            : Error.Equals(other.Error);
    }

    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    public override int GetHashCode() =>
        IsSuccess ? HashCode.Combine(0, _value) : HashCode.Combine(1, Error);

    /// <summary>Never renders <see cref="Value"/> or <see cref="DomainError.Message"/>.</summary>
    public override string ToString() => IsSuccess ? "Success" : $"Failure: {Error}";

    public static bool operator ==(Result<T>? left, Result<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null && left.Equals(right);
    }

    public static bool operator !=(Result<T>? left, Result<T>? right) => !(left == right);
}
