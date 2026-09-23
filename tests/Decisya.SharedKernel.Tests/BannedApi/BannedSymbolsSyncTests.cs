namespace Decisya.SharedKernel.Tests.BannedApi;

/// <summary>
/// Story 5, Scenario 3 — BannedSymbols.txt stays in sync with the members this story
/// requires. This is deliberately a text-based check (unlike
/// <see cref="BannedApiCompilationTests"/>, which proves the ban actually fails a
/// build): the AC is literally "when BannedSymbols.txt is inspected, every member is
/// present with a corrective message".
/// </summary>
public class BannedSymbolsSyncTests
{
    private static readonly (string Member, string RequiredMessageFragment)[] RequiredEntries =
    [
        ("P:System.DateTime.Now", "IClock"),
        ("P:System.DateTime.UtcNow", "IClock"),
        ("P:System.DateTimeOffset.Now", "IClock"),
        ("P:System.DateTimeOffset.UtcNow", "IClock"),
        ("M:System.Convert.ToDouble(System.Decimal)", "double"),
        ("M:System.Decimal.ToDouble(System.Decimal)", "double"),
    ];

    [Fact]
    public void BannedSymbols_txt_lists_every_required_member_with_a_corrective_message()
    {
        var path = RepoPaths.Find("BannedSymbols.txt");
        var lines = File.ReadAllLines(path);

        foreach (var (member, requiredMessageFragment) in RequiredEntries)
        {
            var line = lines.SingleOrDefault(l => l.StartsWith(member + ";", StringComparison.Ordinal));

            line.Should().NotBeNull($"BannedSymbols.txt must ban {member}");
            line!.Split(';', 2)[1].Should().Contain(
                requiredMessageFragment,
                $"the corrective message for {member} should mention '{requiredMessageFragment}'");
        }
    }
}
