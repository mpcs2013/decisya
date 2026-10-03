namespace Decisya.Identity.Tests;

[Trait("Category", "Unit")]
public class ScanExclusionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "decisya-scan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("src/Decisya.Web/node_modules", true)]
    [InlineData("src/Decisya.Web/node_modules/react/index.js", true)]
    [InlineData("src/Decisya.Web/node_modules/.bin/x.json", true)]
    [InlineData("src/Decisya.Web/src/App.tsx", false)]
    [InlineData("src/Decisya.Web/node_modules_extra/x.json", false)]
    [InlineData("src/Decisya.Web/sub/node_modules/x.json", false)]
    [InlineData("src/Decisya.Bff/node_modules/x.json", false)]
    [InlineData("src/Decisya.Bff/wwwroot/assets/app.js", false)]
    [InlineData("node_modules/x.json", false)]
    [InlineData("docs/src/Decisya.Web/node_modules/x.json", false)]
    [InlineData("src/Decisya.Web2/node_modules/x.json", false)]
    public void Only_the_exact_repo_relative_node_modules_directory_is_excluded(string relativePath, bool excluded) =>
        ScanExclusions.IsExcluded(relativePath).Should().Be(excluded);

    [Fact]
    public void A_planted_file_outside_the_exact_folder_is_still_enumerated()
    {
        string[] skipped = ["src/Decisya.Web/node_modules/pkg/skip.json"];
        string[] scanned =
        [
            "src/Decisya.Web/src/keep.json",
            "src/Decisya.Web/sub/node_modules/keep.json",
            "src/Other/node_modules/keep.json",
            "src/Decisya.Bff/wwwroot/keep.json",
            "tools/node_modules/keep.json",
        ];
        foreach (var rel in skipped.Concat(scanned))
        {
            var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "{}");
        }

        var found = ScanExclusions.EnumerateFiles(_root, _root, "*.json")
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        found.Should().BeEquivalentTo(scanned);

        ScanExclusions.EnumerateFiles(_root, Path.Combine(_root, "src"), "*.json")
            .Should().HaveCount(scanned.Count(s => s.StartsWith("src/", StringComparison.Ordinal)));
    }
}
