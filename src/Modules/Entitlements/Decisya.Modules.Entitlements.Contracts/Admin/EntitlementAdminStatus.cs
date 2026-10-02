namespace Decisya.Modules.Entitlements.Contracts.Admin;

/// <summary>
/// The coarse outcome of an <see cref="IEntitlementAdminCommands"/> call, for the HTTP mapping in
/// <c>Decisya.Modules.Admin</c> (issue #25, G2). There is no zero value, so an uninitialized
/// status is never read as a success.
/// </summary>
public enum EntitlementAdminStatus
{
    /// <summary>The change and its audit record committed. HTTP 204.</summary>
    Succeeded = 1,

    /// <summary>The handler's caller precondition refused the call. HTTP 403, with no code in the body.</summary>
    Forbidden = 2,

    /// <summary>The command failed validation. HTTP 400 with the code.</summary>
    Invalid = 3,

    /// <summary>The target tenant does not exist. HTTP 404 with the code.</summary>
    NotFound = 4,

    /// <summary>The command conflicts with the stored state (a used trial). HTTP 409 with the code.</summary>
    Conflict = 5,
}
