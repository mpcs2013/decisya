namespace Decisya.SharedKernel.Observability;

/// <summary>
/// Marks a type, property or field as carrying personally identifiable or otherwise
/// confidential data. The logging pipeline in <c>Decisya.ServiceDefaults</c>
/// (<c>SensitiveDataMaskingProcessor</c>) replaces every value it reaches through a
/// member or type carrying this attribute with <c>***</c>, on stdout and on OTLP alike.
/// </summary>
/// <remarks>
/// <para>
/// The attribute lives in <c>Decisya.SharedKernel</c> and not in
/// <c>Decisya.ServiceDefaults</c> because the types that carry PII are domain entities,
/// DTOs and <c>Contracts</c> messages, and ADR-0005 allows a <c>Contracts</c> assembly to
/// reference only <c>Decisya.SharedKernel</c> and other <c>Contracts</c> assemblies.
/// </para>
/// <para>
/// <see cref="AttributeTargets.Parameter"/> is deliberately absent: a parameter-level
/// attribute is invisible in Microsoft.Extensions.Logging state at run time, so the
/// masker could never honour it. The compiler rejects such a use with <c>CS0592</c>
/// rather than the attribute being silently ignored.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field,
    Inherited = true,
    AllowMultiple = false)]
public sealed class SensitiveAttribute : Attribute;
