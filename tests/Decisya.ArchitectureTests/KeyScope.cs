using System.Reflection;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Every assembly the Wolverine and key rules of issue #231 run against (architecture note: KeyScope;
/// R10, R11, R14, R16, R17): every <c>src/**/*.csproj</c> assembly except <c>Decisya.AppHost</c> and
/// <c>Decisya.Infrastructure.Migrator</c>. The Migrator only applies EF migrations, and referencing it
/// would load EF Core 10.0.13 next to 10.0.4 (MSB3277). The exclusion list is exact, so a third
/// exclusion is a visible change here and fails <see cref="Excluded"/>'s test.
/// </summary>
/// <remarks>
/// The names come from the project files, so a new <c>src</c> project is in scope the moment it exists. The
/// test project references each of them, so <see cref="Assembly.Load(string)"/> finds the assembly; a
/// <c>src</c> project this project does not reference makes <see cref="Assemblies"/> throw, naming it,
/// instead of silently shrinking the scope.
/// </remarks>
public static class KeyScope
{
    /// <summary>The two project names left out, exactly.</summary>
    public static IReadOnlyList<string> Excluded { get; } = ["Decisya.AppHost", "Decisya.Infrastructure.Migrator"];

    /// <summary>The assembly name of every <c>src</c> project in scope, from its project file.</summary>
    public static IReadOnlyList<string> ProjectNames { get; } = Directory
        .EnumerateFiles(RepoPaths.Find("src"), "*.csproj", SearchOption.AllDirectories)
        .Select(Path.GetFileNameWithoutExtension)
        .OfType<string>()
        .Where(name => !Excluded.Contains(name, StringComparer.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>The loaded assemblies of <see cref="ProjectNames"/>.</summary>
    public static Assembly[] Assemblies { get; } = [.. ProjectNames.Select(Load)];

    private static Assembly Load(string name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"KeyScope names the src project '{name}', but Decisya.ArchitectureTests does not reference it. Add the reference; do not drop the project from the scope.",
                exception);
        }
    }
}
