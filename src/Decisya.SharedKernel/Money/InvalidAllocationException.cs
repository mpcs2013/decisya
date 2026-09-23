namespace Decisya.SharedKernel;

/// <summary>
/// Raised when <see cref="Money.Allocate(int)"/> or <see cref="Money.Allocate(System.Collections.Generic.IReadOnlyList{int})"/>
/// is asked to split an amount into an invalid number of parts or by invalid ratios
/// (e.g. zero parts, or ratios that are all zero/negative).
/// </summary>
public sealed class InvalidAllocationException : ArgumentException
{
    public InvalidAllocationException(string message, string? paramName = null)
        : base(message, paramName)
    {
    }

    public InvalidAllocationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
