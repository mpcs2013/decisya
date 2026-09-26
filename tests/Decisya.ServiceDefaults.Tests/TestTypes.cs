using Decisya.SharedKernel.Observability;

// These fixtures are read by SensitiveDataMaskingProcessor through instance-property
// reflection (property.GetValue(instance)); several intentionally return a constant or
// throw without touching other instance state, which CA1822 would otherwise ask to make
// static. A static property is invisible to BindingFlags.Instance reflection and would
// silently break the very test it exists for, so the rule is disabled for this file only.
#pragma warning disable CA1822

namespace Decisya.ServiceDefaults.Tests;

/// <summary>A type the masker must replace whole (rule 3).</summary>
[Sensitive]
internal sealed record SensitiveToken(string Value);

/// <summary>A type with a <see cref="SensitiveAttribute"/> member (rule 4).</summary>
internal sealed record CustomerSnapshot(string DisplayName, [property: Sensitive] string Email);

/// <summary>A struct carrying <see cref="SensitiveAttribute"/>, used through <c>T?</c>.</summary>
[Sensitive]
internal readonly record struct SensitiveId(string Value);

/// <summary>A plain type with a fully known, non-sensitive graph (rule 5).</summary>
internal sealed record PlainPoint(int X, int Y);

/// <summary>Base class whose property is marked; the derived record overrides it without restating the attribute.</summary>
internal abstract class MarkedBase
{
    [Sensitive]
    public virtual string? Email { get; set; }

    public string Label { get; set; } = "label";
}

internal sealed class MarkedDerived : MarkedBase
{
    public override string? Email { get; set; }
}

/// <summary>Holds a value in an <c>object</c>-typed property, so rule 4 selects it.</summary>
internal sealed class ObjectHolder
{
    public object? Payload { get; set; }
}

/// <summary>Holds a nullable <see cref="SensitiveId"/>.</summary>
internal sealed class NullableSensitiveHolder
{
    public SensitiveId? Id { get; set; }
}

/// <summary>Counts how often the processor reads its rendered member.</summary>
internal sealed class CountingProbe
{
    private int _reads;

    /// <summary>Not public, so the processor never renders (or reads) it.</summary>
    internal int ReadCount => Volatile.Read(ref _reads);

    /// <summary>Declared as <c>object</c>, so rule 4 selects the type for rendering.</summary>
    public object? Payload
    {
        get
        {
            Interlocked.Increment(ref _reads);
            return "payload";
        }
    }
}

/// <summary>A getter that throws: the member is masked, the rest of the record survives.</summary>
internal sealed class ThrowingProbe
{
    public object? Boom => throw new InvalidOperationException("getter blew up");

    public string Safe => "safe";
}

/// <summary>A <c>ToString()</c> that would leak; rule 4 must never call it.</summary>
internal sealed class LoudToString(string canary)
{
    public object? Payload => "payload";

    public override string ToString() => canary;
}

/// <summary>A member whose name alone is on the deny-list.</summary>
internal sealed class DenyListedMemberHolder
{
    public object? AccessToken { get; set; }

    public string Safe { get; set; } = "safe";
}

/// <summary>Nested three deep, with the marked member at the bottom.</summary>
internal sealed record DeepLevel3([property: Sensitive] string Email);

internal sealed record DeepLevel2(DeepLevel3 Inner);

internal sealed record DeepLevel1(DeepLevel2 Inner);

internal sealed record DeepRoot(DeepLevel1 Inner);

/// <summary>Interface whose member carries the attribute.</summary>
internal interface IHasMarkedMember
{
    [Sensitive]
    string Email { get; }
}

internal sealed class ImplementsMarkedInterface : IHasMarkedMember
{
    public string Email { get; set; } = "unset";

    public object? Payload => "payload";
}

/// <summary>Nested one level beyond
/// <see cref="Decisya.ServiceDefaults.Logging.SensitiveDataMaskingProcessor"/>'s render-depth
/// cap, so the branch holding the leaf is masked whole rather than rendered (G4-15-13).</summary>
internal sealed record TooDeepLevel4([property: Sensitive] string Email);

internal sealed record TooDeepLevel3(TooDeepLevel4 Inner);

internal sealed record TooDeepLevel2(TooDeepLevel3 Inner);

internal sealed record TooDeepLevel1(TooDeepLevel2 Inner);

internal sealed record TooDeepRoot(TooDeepLevel1 Inner);

/// <summary>More public properties than
/// <see cref="Decisya.ServiceDefaults.Logging.SensitiveDataMaskingProcessor"/>'s per-object
/// member cap, so the renderer caps the member count and collapses the remainder into a
/// single mask (G4-15-13). <see cref="Trigger"/> is <c>object</c>-typed purely to force rule 4
/// to select the whole type for rendering; its own value is irrelevant to the test.</summary>
internal sealed class ManyMembersHolder
{
    public object? Trigger { get; set; }

    public int P00 { get; set; }
    public int P01 { get; set; }
    public int P02 { get; set; }
    public int P03 { get; set; }
    public int P04 { get; set; }
    public int P05 { get; set; }
    public int P06 { get; set; }
    public int P07 { get; set; }
    public int P08 { get; set; }
    public int P09 { get; set; }
    public int P10 { get; set; }
    public int P11 { get; set; }
    public int P12 { get; set; }
    public int P13 { get; set; }
    public int P14 { get; set; }
    public int P15 { get; set; }
    public int P16 { get; set; }
    public int P17 { get; set; }
    public int P18 { get; set; }
    public int P19 { get; set; }
    public int P20 { get; set; }
    public int P21 { get; set; }
    public int P22 { get; set; }
    public int P23 { get; set; }
    public int P24 { get; set; }
    public int P25 { get; set; }
    public int P26 { get; set; }
    public int P27 { get; set; }
    public int P28 { get; set; }
    public int P29 { get; set; }
    public int P30 { get; set; }
    public int P31 { get; set; }
    public int P32 { get; set; }
    public int P33 { get; set; }
    public int P34 { get; set; }
}

/// <summary>Every deny-list fragment as a member name (rather than a state or scope key),
/// for G4-15-18's nested-member-position coverage. Property names cannot carry the
/// separators (<c>_</c>, <c>-</c>, <c>.</c>) the state/scope theories use, but the deny-list
/// match is on the normalized, separator-stripped name, so the plain fragment name is
/// enough to prove the same match applies inside a rendered object.</summary>
internal sealed class DenyListedMembersProbe
{
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? Authorization { get; set; }
    public string? Cookie { get; set; }
    public string? ConnectionString { get; set; }
    public string? QueryString { get; set; }
    public string? Pwd { get; set; }
    public string? Credential { get; set; }
    public string? PrivateKey { get; set; }
    public string? SigningKey { get; set; }
    public string? UserIdHashKey { get; set; }
    public string? Bearer { get; set; }
    public string? Jwt { get; set; }
    public string? Session { get; set; }
}
