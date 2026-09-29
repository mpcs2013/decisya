namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The current request's or job's validated caller (issue #21, G2 D5). Modules need the
/// caller's own identity for object-level authorization (BOLA) — for example, "does the
/// caller's own <c>Membership</c> carry the <c>Owner</c> role" — without depending on
/// <c>Decisya.Api.Authentication.CallerIdentity</c>, which is internal to the API host.
/// </summary>
/// <remarks>
/// #21 ships this interface and its production implementation
/// (<c>Decisya.Api.Authentication.RequestCaller</c>), which also implements
/// <see cref="ICurrentTenant"/> and forwards both to the same scoped instance, set exactly
/// once per request from the validated principal's <c>sub</c> claim. Tests supply
/// <see cref="UserId"/> directly, without a host or a real login.
/// </remarks>
public interface ICurrentCaller
{
    /// <summary>
    /// The validated <c>sub</c> claim of the current caller. Throws
    /// <see cref="InvalidOperationException"/> when the current request or job has no
    /// validated caller — the same fail-closed shape <see cref="ICurrentTenant.Resolution"/>
    /// gives before it is set.
    /// </summary>
    string UserId { get; }
}
