namespace Decisya.Modules.Entitlements.Domain;

/// <summary>The plans a tenant can be on (issue #23, G2). A value, not an entity: Free is implicit, Pro comes only from an active trial.</summary>
internal enum PlanId
{
    Free = 1,
    Pro = 2,
}
