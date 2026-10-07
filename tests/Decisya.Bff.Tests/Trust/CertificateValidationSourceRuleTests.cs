namespace Decisya.Bff.Tests.Trust;

/// <summary>
/// #120 G4-120-05 / G2 D10 item 11: no production code under <c>src/</c> may take over server
/// certificate validation with a callback or switch it off. Trust is narrowed only by a chain
/// policy (<c>CustomRootTrust</c>), which can never accept more than the one mounted root.
/// Comment lines are ignored, so documentation may name the banned symbols.
/// </summary>
public class CertificateValidationSourceRuleTests
{
    private static readonly string[] BannedSymbols =
    [
        "ServerCertificateCustomValidationCallback",
        "DangerousAcceptAnyServerCertificateValidator",
        "RemoteCertificateValidationCallback",
    ];

    [Fact]
    public void No_production_source_overrides_or_disables_certificate_validation()
    {
        var sourceRoot = RepoPaths.Find("src");
        Directory.Exists(sourceRoot).Should().BeTrue("the rule must scan the real source tree");

        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToList();
        files.Should().NotBeEmpty();

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var banned in BannedSymbols)
                {
                    if (line.Contains(banned, StringComparison.Ordinal))
                    {
                        violations.Add($"{Path.GetRelativePath(sourceRoot, file)}:{lineNumber} uses {banned}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "certificate validation is narrowed by BackchannelRoot's chain policy only, never replaced by a callback");
    }

    private static bool IsBuildOutput(string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path.Split(separators).Any(part =>
            string.Equals(part, "obj", StringComparison.Ordinal) || string.Equals(part, "bin", StringComparison.Ordinal));
    }
}
