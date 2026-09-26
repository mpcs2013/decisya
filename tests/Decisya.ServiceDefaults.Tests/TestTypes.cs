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
