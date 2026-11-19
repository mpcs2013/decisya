using System.Linq.Dynamic.Core;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.DynamicLinq;

// G4-326-06 red fixture (#326, TD-326-09): one call into System.Linq.Dynamic.Core, which reaches the
// build only as a transitive package of JasperFx 2.102.0.

/// <summary>Filters a query with a string predicate: a dynamic LINQ call the banned-reference rule must catch.</summary>
public static class UsesDynamicLinq
{
    public static IQueryable<int> Apply(IQueryable<int> source, string predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DynamicQueryableExtensions.Where(source, predicate);
    }
}
