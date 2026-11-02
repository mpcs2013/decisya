using Decisya.SharedKernel.Observability;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.PersonalDataLogs;

// Issue #230 red fixtures: the sources the personal-data log rule must follow. The member names (UserId, Email,
// DisplayName, Iban, Description, Prompt, ClientSecret) are in the catalogue's memberNames on Decisya.* types, so a
// read of any of them is personal data whatever the placeholder in the log call says. Synthetic values only.

/// <summary>A payment line; <c>Description</c> is the free-text category.</summary>
public sealed record Tx(string Description);

/// <summary>One property per category, none of them marked <c>[Sensitive]</c>.</summary>
public sealed class Person
{
    public string UserId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Iban { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;
}

/// <summary>A record whose receiving member has an innocent name (G3 G4-230-01).</summary>
public sealed record PaymentNote(string Payee);

/// <summary>A mutable view filled by an object initializer (G3 G4-230-01).</summary>
public sealed class View
{
    public string? Text { get; set; }
}

/// <summary>A value object read from an <c>Iban</c>-named member; the type is not <c>[Sensitive]</c>.</summary>
public readonly record struct Iban(string Value);

/// <summary>An entity with an <c>Iban</c>-named member of a Decisya value object type.</summary>
public sealed class Account
{
    public Iban Iban { get; init; }
}

/// <summary>A non-string personal member: a <c>Guid</c> named <c>UserId</c>.</summary>
public sealed class GuidIdentified
{
    public Guid UserId { get; init; }
}

/// <summary>A <c>[Sensitive]</c> member that is in no catalogue category.</summary>
public sealed class SensitiveHolder
{
    [Sensitive]
    public string Opaque { get; init; } = string.Empty;
}

/// <summary>A <c>[Sensitive]</c> member that is also in the user-identifier category.</summary>
public sealed class Vault
{
    [Sensitive]
    public string UserId { get; init; } = string.Empty;
}

/// <summary>An object whose personal member lacks <c>[Sensitive]</c>.</summary>
public sealed class OpenProfile
{
    public string Email { get; init; } = string.Empty;
}

/// <summary>A record with a <c>[Sensitive]</c> member and a synthesized <c>ToString</c>.</summary>
public sealed record MarkedRecord([property: Sensitive] string Opaque);
