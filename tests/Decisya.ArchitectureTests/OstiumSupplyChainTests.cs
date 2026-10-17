using System.Text.Json;

namespace Decisya.ArchitectureTests;

/// <summary>
/// #164 Story 1 / NFR-59 (G2 D4, G3 M-1 and M-4): the file-based half of the supply-chain proof for the
/// Ostium packages. The pins, <c>NuGet.config</c>, the source mapping and the signer line are checked by
/// <c>.claude/tests/test_build_policy.py</c> (no duplicate here); this class checks what that file cannot
/// cheaply see: a lock file next to every project, the supplier's signed content hashes in every lock
/// entry, and where <c>Ostium.*</c> may appear. Static, no restore and no network.
/// </summary>
[Trait("Category", "Architecture")]
public class OstiumSupplyChainTests
{
    // O-006 / O-010: the SHA-512 content hashes of the 0.1.1 nupkgs, equal to the signed checksums file.
    private static readonly Dictionary<string, string> SignedContentHashes = new(StringComparer.Ordinal)
    {
        ["Ostium.Authentication.Jwt"] = "o1xy0E2N6Mtw098r6/OnIyN9VVcEKd8pD4sJDUx0qXe9hU1OaB1HTKCpcfC+7zt0VY9n2rKAbynh6QFq4VtWug==",
        ["Ostium.Authentication.Jwt.Testing"] = "KqtHKbI+FwIKCRVT0CKDHZUPVcwoZQ/EYukaxI4GRMWCHX8tmlUSA6M8Q5j1MAwO4nWxbT641OItWvrxyWtqvQ==",
        ["Ostium.Backchannel"] = "+KugNDi/hNEaQfvFovO/zGjBXp7QzBuk8w5QC2y4qSTCE+rIX1N06FBKURcAVfWFvBI3DU+1cYAWVwaWf0fpaw==",
    };

    [Fact]
    public void Every_project_under_src_and_tests_has_a_sibling_packages_lock_file()
    {
        var missing = ProjectFiles()
            .Where(project => !File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")))
            .Select(Relative)
            .ToList();

        ProjectFiles().Should().NotBeEmpty();
        missing.Should().BeEmpty("locked restore (NFR-59) needs a committed lock file for every restorable project");
    }

    [Fact]
    public void Every_Ostium_lock_entry_resolves_0_1_1_with_the_signed_content_hash()
    {
        var entries = LockFiles().SelectMany(OstiumEntries).ToList();

        entries.Should().NotBeEmpty("the Api and its test project restore the Ostium packages");
        entries.Select(e => e.Id).Distinct().Should().BeEquivalentTo(SignedContentHashes.Keys,
            "all three packages are in the graph of the solution");

        foreach (var entry in entries)
        {
            entry.Resolved.Should().Be("0.1.1", $"{entry.File}: {entry.Id}");
            entry.ContentHash.Should().Be(SignedContentHashes[entry.Id], $"{entry.File}: {entry.Id}");
        }
    }

    [Fact]
    public void Version_0_1_0_of_an_Ostium_package_is_referenced_nowhere()
    {
        // Project and props files: no line that names an Ostium id also names 0.1.0. Lock files: every
        // Ostium entry resolves 0.1.1 (the test above), and none requests another version either.
        var files = ProjectFiles()
            .Append(RepoPaths.Find("Directory.Packages.props"))
            .Append(RepoPaths.Find("Directory.Build.props"))
            .Append(RepoPaths.Find("Directory.Build.targets"));

        var offenders = files
            .Where(file => File.ReadLines(file).Any(line =>
                line.Contains("Ostium.", StringComparison.Ordinal) && line.Contains("0.1.0", StringComparison.Ordinal)))
            .Select(Relative)
            .ToList();

        offenders.Should().BeEmpty("0.1.0 never existed on the feed (O-006)");

        var lockOffenders = LockFiles()
            .Where(file => File.ReadAllText(file).Contains("0.1.0", StringComparison.Ordinal)
                && OstiumEntries(file).Any())
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), @"""(requested|resolved)"": ""\[?0\.1\.0\b"))
            .Select(Relative)
            .ToList();

        lockOffenders.Should().BeEmpty();
    }

    [Fact]
    public void The_BFF_and_the_identity_tests_have_no_Ostium_entry_in_project_or_lock_file()
    {
        // The BFF keeps its own code until its own switch, and Decisya.Identity.Tests must stay free of
        // Ostium.* so the required realm-guard job needs no feed access (G2 D2, Story 6).
        var offenders = new[]
            {
                RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "Decisya.Bff.csproj")),
                RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "packages.lock.json")),
                RepoPaths.Find(Path.Combine("tests", "Decisya.Identity.Tests", "Decisya.Identity.Tests.csproj")),
                RepoPaths.Find(Path.Combine("tests", "Decisya.Identity.Tests", "packages.lock.json")),
            }
            .Where(file => File.ReadAllText(file).Contains("Ostium.", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Under_src_only_Decisya_Api_references_an_Ostium_package()
    {
        var referencing = ProjectFiles()
            .Where(project => Relative(project).StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(project => File.ReadAllText(project).Contains("Ostium.", StringComparison.Ordinal))
            .Select(project => Path.GetFileName(project))
            .ToList();

        referencing.Should().Equal(["Decisya.Api.csproj"]);
    }

    [Fact]
    public void Only_test_projects_reference_the_Ostium_test_kit()
    {
        var referencing = ProjectFiles()
            .Where(project => File.ReadAllText(project).Contains("Ostium.Authentication.Jwt.Testing", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        referencing.Should().NotBeEmpty();
        referencing.Should().OnlyContain(path => path.StartsWith("tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static readonly string[] Roots = ["src", "tests"];

    private static List<string> ProjectFiles() => FilesNamed("*.csproj");

    private static List<string> LockFiles() => FilesNamed("packages.lock.json");

    private static List<string> FilesNamed(string pattern) =>
        Roots
            .SelectMany(root => Directory.EnumerateFiles(RepoPaths.Find(root), pattern, SearchOption.AllDirectories))
            .Where(IsSourceFile)
            .ToList();

    private static bool IsSourceFile(string path)
    {
        var relative = Relative(path);
        var separator = Path.DirectorySeparatorChar;
        return !relative.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            && !relative.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            && !ScanExclusions.IsExcluded(RepoPaths.Find(string.Empty), path);
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoPaths.Find(string.Empty), path);

    private static IEnumerable<(string File, string Id, string Resolved, string ContentHash)> OstiumEntries(string lockFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
        foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var package in target.Value.EnumerateObject()
                .Where(p => p.Name.StartsWith("Ostium.", StringComparison.Ordinal)))
            {
                yield return (
                    Relative(lockFile),
                    package.Name,
                    package.Value.GetProperty("resolved").GetString()!,
                    package.Value.GetProperty("contentHash").GetString()!);
            }
        }
    }
}
