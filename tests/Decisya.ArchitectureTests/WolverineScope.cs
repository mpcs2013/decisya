using System.Reflection;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Every assembly the Wolverine rules of issue #231 (R6, R7) run against (architecture note: WolverineScope):
/// <see cref="ArchitectureScope"/> plus <c>Decisya.Api</c>, <c>Decisya.Bff</c> and
/// <c>Decisya.ServiceDefaults</c>, the hosts and the shared host code that could reach the bus. The
/// key and construction-time rules use <see cref="KeyScope"/> instead.
/// </summary>
public static class WolverineScope
{
    public static Assembly[] Assemblies { get; } =
    [
        .. ArchitectureScope.Assemblies,
        Assembly.Load("Decisya.Api"),
        Assembly.Load("Decisya.Bff"),
        Assembly.Load("Decisya.ServiceDefaults"),
    ];
}
