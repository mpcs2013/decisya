namespace Decisya.SharedKernel.Authorization;

/// <summary>ADR-0008 (amendment 1): endpoint metadata stating that an /api endpoint is deliberately not gated by a feature key.
/// Every /api endpoint carries this marker or an entitlement policy; Decisya.Api.Tests enumerates them.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, Inherited = false, AllowMultiple = false)]
public sealed class NoEntitlementRequiredAttribute : Attribute;
