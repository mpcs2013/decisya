namespace Decisya.SharedKernel;

/// <summary>
/// Raised when an arithmetic or comparison operation is attempted between two
/// <see cref="Money"/> values of different currencies. Decisya never silently converts
/// between currencies (out of scope per docs/requirements/phase-0/shared-kernel.md).
/// </summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    public CurrencyMismatchException(Currency left, Currency right)
        // Currency.ToString() (not .Code) never throws, even for an uninitialised
        // default(Currency); Money's constructor rejects default(Currency), so this
        // exception should never actually see one, but the message stays safe either way.
        : base($"Cannot combine {left} and {right}: currencies must match.")
    {
        Left = left;
        Right = right;
    }

    public Currency Left { get; }

    public Currency Right { get; }
}
