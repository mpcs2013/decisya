using Decisya.SharedKernel.Tenancy;

namespace Decisya.TestInfrastructure;

/// <summary>
/// A settable <see cref="ICurrentCaller"/> for tests (issue #24). Like the production caller it
/// throws <see cref="InvalidOperationException"/> when no user id is set, so a test that forgets
/// one gets the fail-closed "no validated caller" behaviour, never a placeholder.
/// </summary>
public sealed class TestCurrentCaller : ICurrentCaller
{
    /// <summary>The default admin user id the #24 scenarios use.</summary>
    public const string DefaultUserId = "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59";

    /// <summary>The caller's user id; <see langword="null"/> means "no validated caller".</summary>
    public string? Id { get; set; } = DefaultUserId;

    /// <summary>Whether the caller is a tenant-less platform admin (issue #25). Defaults to <see langword="false"/>, like every caller that carries no role information.</summary>
    public bool IsPlatformAdmin { get; set; }

    public string UserId => Id ?? throw new InvalidOperationException("No validated caller (test stub).");
}
