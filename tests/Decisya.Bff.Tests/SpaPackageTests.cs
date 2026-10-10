using System.Text.Json;
using System.Text.RegularExpressions;

namespace Decisya.Bff.Tests;

/// <summary>
/// Issue #26, #58 C-1, C-5, C-6 and G3 M1, M4, S-c, S-d: the SPA package rules, read from the files (no
/// install, no network). The same lifecycle rule runs in <c>ci.yml</c> before the install, because a
/// Web-only PR does not start this test lane.
/// </summary>
[Trait("Category", "Architecture")]
public class SpaPackageTests
{
    private static readonly string[] Lifecycle = ["preinstall", "install", "postinstall", "prepublish", "preprepare", "prepare", "postprepare"];

    private static readonly string[] PackageSections = ["dependencies", "devDependencies"];

    private static JsonElement Package() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Find(Path.Combine("src", "Decisya.Web", "package.json")))).RootElement.Clone();

    [Fact]
    public void Package_json_has_no_lifecycle_script_and_no_allowScripts()
    {
        var package = Package();

        package.TryGetProperty("allowScripts", out _).Should().BeFalse();
        var scripts = package.GetProperty("scripts").EnumerateObject().Select(p => p.Name).ToList();
        scripts.Should().NotBeEmpty();
        scripts.Should().NotContain(Lifecycle);
    }

    [Fact]
    public void Package_json_has_the_build_typecheck_lint_test_and_test_e2e_scripts()
    {
        var scripts = Package().GetProperty("scripts").EnumerateObject().Select(p => p.Name).ToList();

        scripts.Should().Contain(["typecheck", "lint", "test", "test:e2e", "build"]);
    }

    [Fact]
    public void Npmrc_sets_ignore_scripts_true()
    {
        var npmrc = File.ReadAllLines(RepoPaths.Find(Path.Combine("src", "Decisya.Web", ".npmrc")));

        npmrc.Should().Contain(line => Regex.IsMatch(line, @"^ignore-scripts\s*=\s*true\s*$"));
    }

    [Fact]
    public void Every_dependency_is_pinned_to_an_exact_version()
    {
        var package = Package();
        foreach (var section in PackageSections)
        {
            foreach (var dependency in package.GetProperty(section).EnumerateObject())
            {
                dependency.Value.GetString().Should().MatchRegex(@"^\d+\.\d+\.\d+$", dependency.Name);
            }
        }
    }

    [Fact]
    public void Package_json_holds_only_the_packages_approved_in_G1_Q6_and_G2()
    {
        string[] approved =
        [
            "react", "react-dom", "react-router", "vite", "@vitejs/plugin-react", "typescript",
            "eslint", "typescript-eslint", "eslint-plugin-jsx-a11y", "eslint-plugin-react-hooks",
            "@playwright/test", "@axe-core/playwright", "vitest", "@types/react", "@types/react-dom", "@types/node",
        ];
        var package = Package();

        var declared = PackageSections
            .SelectMany(section => package.GetProperty(section).EnumerateObject().Select(p => p.Name))
            .ToList();

        declared.Should().BeEquivalentTo(approved);
        package.TryGetProperty("optionalDependencies", out _).Should().BeFalse();
        package.TryGetProperty("overrides", out _).Should().BeFalse();
    }

    [Fact]
    public void Dependabot_has_a_seven_day_cool_down_on_the_SPA_entry()
    {
        var lines = File.ReadAllLines(RepoPaths.Find(Path.Combine(".github", "dependabot.yml")));
        var entry = Array.FindIndex(lines, line => line.Contains("package-ecosystem: npm", StringComparison.Ordinal));

        entry.Should().BeGreaterThanOrEqualTo(0);
        var block = lines.Skip(entry).Take(6).ToList();
        block.Should().Contain(line => line.Contains("/src/Decisya.Web", StringComparison.Ordinal));
        block.Should().Contain(line => Regex.IsMatch(line, @"cooldown:\s*\{\s*default-days:\s*7\s*\}"));
    }

    [Fact]
    public void The_CI_workflow_installs_with_ignore_scripts_runs_the_package_guard_and_runs_npx_only_with_no_install()
    {
        var ci = File.ReadAllText(RepoPaths.Find(Path.Combine(".github", "workflows", "ci.yml")));

        ci.Should().Contain("ci --ignore-scripts");
        ci.Should().Contain("SPA package guard");
        ci.Should().Contain("audit signatures");
        NpxWithoutNoInstall(ci).Should().BeEmpty("G3 M1 (#102), narrowed by #123 G4-123-04: npx only as 'npx --no-install'");

        // Playwright's browser install exists exactly once, inside the e2e job (G3 M1 (#102), narrowed by #123 G4-123-04).
        Regex.Matches(ci, @"playwright\s+install").Should().HaveCount(1, "G3 M1 (#102), narrowed by #123 G4-123-04: one playwright install in ci.yml");
        var lines = ci.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(line => line.TrimEnd() == "  e2e:");
        start.Should().BeGreaterThanOrEqualTo(0, "the e2e job must exist (fail closed)");
        var end = lines.FindIndex(start + 1, line => Regex.IsMatch(line, @"^  [A-Za-z0-9_-]+:\s*$"));
        var job = string.Join("\n", lines.Skip(start + 1).Take((end < 0 ? lines.Count : end) - start - 1));
        Regex.Matches(job, @"playwright\s+install").Should().HaveCount(1, "G3 M1 (#102), narrowed by #123 G4-123-04: playwright install only in the e2e job");
    }

    [Theory]
    [InlineData("npx playwright test", false)]
    [InlineData("npx --yes playwright", false)]
    [InlineData("npx --no-installx playwright", false)]
    [InlineData("run: npx\n", false)]
    [InlineData("npx --no-install playwright test", true)]
    public void The_npx_rule_accepts_only_no_install(string text, bool accepted) =>
        (NpxWithoutNoInstall(text).Count == 0).Should().Be(accepted);

    // Every npx that is not directly followed by the whole token --no-install (it could fetch a package outside the lockfile).
    private static List<string> NpxWithoutNoInstall(string text) =>
        Regex.Matches(text, @"\bnpx\b(?!\s+--no-install(\s|$))").Select(m => m.Value).ToList();

    [Fact]
    public void No_env_file_exists_under_the_SPA_project_outside_node_modules()
    {
        var root = RepoPaths.Find(Path.Combine("src", "Decisya.Web"));
        var files = Directory.EnumerateFiles(root, ".env*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(['/', '\\']).Contains("node_modules"));

        files.Should().BeEmpty();
    }
}
