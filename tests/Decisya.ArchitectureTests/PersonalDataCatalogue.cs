using System.Text.Json;

namespace Decisya.ArchitectureTests;

/// <summary>One category of <c>docs/security/personal-data-catalogue.json</c> (schema version 2).</summary>
internal sealed record CatalogueCategory
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>When <see langword="false"/> the category can never be suppressed (secret material).</summary>
    public bool Suppressible { get; init; } = true;

    public IReadOnlyList<string> Members { get; init; } = [];

    public IReadOnlyList<string> MemberNames { get; init; } = [];

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> ClaimTypes { get; init; } = [];
}

/// <summary>A framework reader keyed by a name argument (G3 change 2): <c>GetTokenValue("refresh_token")</c>.</summary>
internal sealed record ClaimReaderEntry(string Member, string? NameParameter, string FallbackCategory);

/// <summary>One pinned <c>[PersonalDataLogReviewed]</c> use (G3 G4-230-04). Matching is on all three fields.</summary>
internal sealed record SuppressionEntry(string Site, string Category, string Justification);

/// <summary>
/// Reader for the personal-data catalogue (issue #230, G2 D2; schema version 2). The rule knows only this schema,
/// never a category name. Any other version is rejected. Tests make variants with <c>with</c>.
/// </summary>
internal sealed record PersonalDataCatalogue
{
    public const string FilePath = "docs/security/personal-data-catalogue.json";

    public const int SupportedVersion = 2;

    public required int Version { get; init; }

    public IReadOnlyList<CatalogueCategory> Categories { get; init; } = [];

    public IReadOnlyList<ClaimReaderEntry> ClaimReaders { get; init; } = [];

    public IReadOnlyList<string> Maskers { get; init; } = [];

    public IReadOnlyList<string> RedactingTypes { get; init; } = [];

    public IReadOnlyList<SuppressionEntry> Suppressions { get; init; } = [];

    /// <summary>Reads the checked-in catalogue. A missing file throws (the rule never fails open).</summary>
    public static PersonalDataCatalogue Load() => Parse(File.ReadAllText(RepoPaths.Find(FilePath)));

    public static PersonalDataCatalogue Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var version = root.GetProperty("version").GetInt32();
        if (version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"The personal-data catalogue has schema version {version}; this rule reads version {SupportedVersion} only.");
        }

        return new PersonalDataCatalogue
        {
            Version = version,
            Categories = [.. root.GetProperty("categories").EnumerateArray().Select(ReadCategory)],
            ClaimReaders = [.. root.GetProperty("claimReaders").EnumerateArray().Select(ReadReader)],
            Maskers = Strings(root, "maskers"),
            RedactingTypes = Strings(root, "redactingTypes"),
            Suppressions = [.. root.GetProperty("suppressions").EnumerateArray().Select(ReadSuppression)],
        };
    }

    private static CatalogueCategory ReadCategory(JsonElement element) => new()
    {
        Id = element.GetProperty("id").GetString()!,
        Name = element.GetProperty("name").GetString()!,
        Suppressible = element.GetProperty("suppressible").GetBoolean(),
        Members = Strings(element, "members"),
        MemberNames = Strings(element, "memberNames"),
        Types = Strings(element, "types"),
        ClaimTypes = Strings(element, "claimTypes"),
    };

    private static ClaimReaderEntry ReadReader(JsonElement element) => new(
        element.GetProperty("member").GetString()!,
        element.GetProperty("nameParameter").GetString(),
        element.GetProperty("fallbackCategory").GetString()!);

    private static SuppressionEntry ReadSuppression(JsonElement element) => new(
        element.GetProperty("site").GetString()!,
        element.GetProperty("category").GetString()!,
        element.GetProperty("justification").GetString()!);

    private static string[] Strings(JsonElement parent, string name) =>
        [.. parent.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];
}
