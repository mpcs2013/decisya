using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;

namespace Decisya.Bff.Tests.Architecture;

/// <summary>
/// Issue #26, G2 boundary facts for the SPA hosting: static files and file providers live only in
/// <c>Decisya.Bff.Spa</c>, no SPA dev-server proxy and no form post exist in the BFF, and the SPA
/// build output is git-ignored.
/// </summary>
[Trait("Category", "Architecture")]
public class BffSpaBoundaryTests
{
    private static readonly Assembly BffAssembly = typeof(Program).Assembly;

    [Theory]
    [InlineData("Microsoft.AspNetCore.StaticFiles")]
    [InlineData("Microsoft.Extensions.FileProviders")]
    public void Only_types_in_Decisya_Bff_Spa_depend_on_StaticFiles_and_FileProviders(string dependency)
    {
        var result = Types.InAssembly(BffAssembly)
            .That().DoNotResideInNamespace("Decisya.Bff.Spa")
            .ShouldNot().HaveDependencyOn(dependency)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Only_the_Spa_folder_names_StaticFiles_or_FileProviders_in_source()
    {
        var banned = new Regex(@"UseStaticFiles|UseDefaultFiles|UseFileServer|StaticFileOptions|IFileProvider|PhysicalFileProvider|MapFallbackToFile", RegexOptions.CultureInvariant);

        BffSourceFiles().Should().Contain(file => Segments(file).Contains("Spa"), "the scan must not be vacuous");
        var offenders = BffSourceFiles()
            .Where(file => !Segments(file).Contains("Spa"))
            .Where(file => banned.IsMatch(File.ReadAllText(file)))
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void No_UseSpa_dev_server_proxy_and_no_FormPost_exist_anywhere_in_Decisya_Bff()
    {
        var files = BffSourceFiles().ToList();
        files.Should().NotBeEmpty();

        files.Where(file => File.ReadAllText(file).Contains("UseSpa(", StringComparison.Ordinal)).Should().BeEmpty();
        files.Where(file => File.ReadAllText(file).Contains("FormPost", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public void The_gitignore_excludes_the_SPA_build_output_in_Decisya_Bff_wwwroot()
    {
        var lines = File.ReadAllLines(RepoPaths.Find(".gitignore")).Select(line => line.Trim());

        lines.Should().Contain("src/Decisya.Bff/wwwroot/");
    }

    private static IEnumerable<string> BffSourceFiles()
    {
        var root = RepoPaths.Find(Path.Combine("src", "Decisya.Bff"));
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Segments(file).Any(s => s is "obj" or "bin" or "wwwroot"));
    }

    private static string[] Segments(string file) =>
        Path.GetRelativePath(RepoPaths.Find(Path.Combine("src", "Decisya.Bff")), file)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
