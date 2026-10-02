using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// The actor check of the admin commands (issue #24, G2 D5; G3 G4-24-04). A command is audited
/// under the caller's validated user id, so a command with no such id is refused, never
/// attributed to a placeholder.
/// </summary>
internal static class CallerActor
{
    /// <summary>
    /// <see langword="false"/> when <see cref="ICurrentCaller.UserId"/> throws
    /// <see cref="InvalidOperationException"/> (the documented "no validated caller" contract) or
    /// is blank. No other exception is swallowed.
    /// </summary>
    public static bool IsKnown(ICurrentCaller caller)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(caller.UserId);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
