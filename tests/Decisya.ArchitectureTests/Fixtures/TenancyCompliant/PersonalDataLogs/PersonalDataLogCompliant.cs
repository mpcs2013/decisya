#pragma warning disable CA1848, CA2254, CA1873, CA1822, IDE0060, CA1859, CA2007
using System.Globalization;
using System.Security.Claims;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.PersonalDataLogs;

// Issue #230 green fixtures (G2 R3, R4, R4a): code that logs, and that the personal-data log rule must leave alone.
// Synthetic values only.

/// <summary>A role, for the membership look-alike.</summary>
public enum RoleKind
{
    Owner,
    Member,
}

/// <summary>Shaped like <c>Membership</c>: the user id is <c>[Sensitive]</c>, the other members are not personal.</summary>
public sealed class MembershipLike
{
    public MembershipLike(TenantId tenantId, string userId, RoleKind role)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    [Sensitive]
    public string UserId { get; private set; }

    public RoleKind Role { get; private set; }
}

/// <summary>A <c>[Sensitive]</c> type: any value of it is masked as a whole.</summary>
[Sensitive]
public sealed class Vault
{
    public string Email { get; init; } = string.Empty;
}

/// <summary>A value object whose type carries <c>[Sensitive]</c> (G3 G4-230-01, green).</summary>
[Sensitive]
public readonly record struct SecretIban(string Value);

/// <summary>An entity with an <c>Iban</c>-named member of a <c>[Sensitive]</c> value object type.</summary>
public sealed class SecureAccount
{
    public SecretIban Iban { get; init; }
}

/// <summary>A pass-through "masker": the rule trusts it only when the catalogue lists it.</summary>
public static class FixtureMasker
{
    public static string Mask(string value) => value;
}

/// <summary>R4: the output of a listed masker (the test adds this method to an in-memory catalogue copy).</summary>
public static class MaskerOutput
{
    public static void Run(ILogger logger, ICurrentCaller caller) => logger.LogInformation("Seen {Who}", FixtureMasker.Mask(caller.UserId));
}

/// <summary>R4: <c>TenantId</c>, an enum, a <c>bool</c>, a length and a constant.</summary>
public static class NonPersonalKinds
{
    public static void Run(ILogger logger, ICurrentCaller caller, TenantId tenant, LogLevel level, bool flag) =>
        logger.LogInformation("Kinds {Tenant} {Level} {Flag} {Length} {Constant}", tenant, level, flag, caller.UserId.Length, "constant");
}

/// <summary>R4: the <c>acr</c> claim mapped to constants (the <c>AuthEvents.ClassifyAcr</c> shape).</summary>
public static class AcrClaim
{
    public static void Run(ILogger logger, ClaimsPrincipal principal)
    {
        var acr = principal.FindFirst("acr")?.Value;
        var level = acr switch
        {
            "pwd" => "password",
            "otp" => "second-factor",
            _ => "other",
        };
        logger.LogInformation("Level {Level}", level);
    }
}

/// <summary>R4: a <c>Guid</c> that is not derived from a source.</summary>
public static class PlainGuid
{
    public static void Run(ILogger logger) => logger.LogInformation("Run {Id}", Guid.NewGuid());
}

/// <summary>R4 (G4-230-02): <c>GetTokenValue("expires_at")</c> parsed and logged is not secret material.</summary>
public static class ExpiryToken
{
    public static void Run(ILogger logger, AuthenticationProperties properties)
    {
        var raw = properties.GetTokenValue("expires_at");
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
        {
            logger.LogInformation("Expires {At}", expires);
        }
    }
}

/// <summary>R4 (G4-230-03): constant <c>Console.Error</c> lines, the <c>KeyRingCertificateGenerator</c> shape.</summary>
public static class ConstantStderr
{
    public static void Run()
    {
        Console.Error.WriteLine("Key ring certificate written.");
        Console.WriteLine("Done.");
    }
}

/// <summary>R4 (G4-230-01, field-level taint): a member read from a field that did not receive the taint.</summary>
public static class FieldLevel
{
    public static void Run(ILogger logger, ICurrentCaller caller, TenantId tenant)
    {
        var membership = new MembershipLike(tenant, caller.UserId, RoleKind.Owner);
        logger.LogInformation("Membership {Id}", membership.Id);
    }
}

/// <summary>R3 and R4a green: the owning object whose member carries <c>[Sensitive]</c>, passed whole.</summary>
public static class OwningObject
{
    public static void Run(ILogger logger, ICurrentCaller caller, TenantId tenant)
    {
        var membership = new MembershipLike(tenant, caller.UserId, RoleKind.Owner);
        logger.LogInformation("Membership {Membership}", membership);
    }
}

/// <summary>R3 green: a <c>[Sensitive]</c> type passed as itself.</summary>
public static class SensitiveTypeWhole
{
    public static void Run(ILogger logger, Vault vault) => logger.LogInformation("Vault {Vault}", vault);
}

/// <summary>R3 green: a list of owning objects (the processor masks a non-scalar collection whole).</summary>
public static class ListOfOwningObjects
{
    public static void Run(ILogger logger, ICurrentCaller caller, TenantId tenant)
    {
        var members = new List<MembershipLike> { new(tenant, caller.UserId, RoleKind.Member) };
        logger.LogInformation("Members {Members}", members);
    }
}

/// <summary>R4a green: a value object whose type carries <c>[Sensitive]</c>, read from an <c>Iban</c>-named member.</summary>
public static class SensitiveValueObject
{
    public static void Run(ILogger logger, SecureAccount account) => logger.LogInformation("Account {Account}", account.Iban);
}
