namespace Decisya.ArchitectureTests;

/// <summary>
/// One personal-data log finding (issue #230, G2 "Check design", Output). Holds metadata only: a repo-relative file
/// (or a file name for a path outside the repository), a line, <c>Type::Method</c>, the category, the source member
/// and the sink. Never an IL string literal, a drive letter or a user name (G3 S-02).
/// </summary>
internal sealed record PersonalDataFinding(
    string File,
    int Line,
    string Site,
    string CategoryId,
    string CategoryName,
    string Source,
    string Sink)
{
    /// <summary>The three fixes, in the failure text (G1 Story 5).</summary>
    public const string Fixes =
        "Fix: pass the owning object whose member carries [Sensitive] (a string read from it is not masked), "
        + "rely on the hashed user_id from ILogEnrichmentContext, or drop the value. "
        + "Suppress only with [PersonalDataLogReviewed(\"<category id>\", \"<justification>\")].";

    public override string ToString() => $"{File}:{Line}: {Site} passes {CategoryName} ({Source}) to {Sink}. {Fixes}";
}

/// <summary>A valid <c>[PersonalDataLogReviewed]</c> use that turned a finding into a counted suppression.</summary>
internal sealed record PersonalDataSuppression(string Site, string Category, string Justification);

/// <summary>What the run saw, for the non-vacuity checks (R7) and the test output. No paths, no literals.</summary>
internal sealed class PersonalDataStats
{
    public int AssembliesRead { get; set; }

    public int MethodsAnalyzed { get; set; }

    /// <summary>Every <c>[LoggerMessage]</c> call site the analysis examined, as <c>Caller -&gt; Callee</c>.</summary>
    public SortedSet<string> LoggerMessageCallSites { get; } = new(StringComparer.Ordinal);

    /// <summary>Every <c>[LoggerMessage]</c> call site found by a plain IL scan, independent of the analysis.</summary>
    public SortedSet<string> LoggerMessageCallSitesInIl { get; } = new(StringComparer.Ordinal);

    /// <summary>Where a source was read, as <c>Caller &lt;- Source</c>.</summary>
    public SortedSet<string> SourcesSeen { get; } = new(StringComparer.Ordinal);

    /// <summary>Catalogue entries whose assembly is not referenced at all (listed, not failed).</summary>
    public List<string> NotReferenced { get; } = [];

    public override string ToString() =>
        $"assemblies={AssembliesRead}, methods={MethodsAnalyzed}, loggerMessageCallSites={LoggerMessageCallSites.Count}"
        + $" (in IL: {LoggerMessageCallSitesInIl.Count}), sourcesSeen={SourcesSeen.Count}, notReferenced=[{string.Join("; ", NotReferenced)}]";
}

/// <summary>The result of one run of <see cref="PersonalDataLogRule.Evaluate"/>.</summary>
internal sealed record PersonalDataEvaluation(
    IReadOnlyList<PersonalDataFinding> Findings,
    IReadOnlyList<PersonalDataSuppression> Suppressions,
    IReadOnlyList<string> Problems,
    PersonalDataStats Stats);

/// <summary>
/// Issue #230 (register finding C-12). A Mono.Cecil IL taint check over built assemblies: a log call that passes
/// personal data without masking fails a test. The catalogue (<c>docs/security/personal-data-catalogue.json</c>) says
/// what is personal data; the rule knows only its schema. Sinks: <c>ILogger</c> and <c>LoggerExtensions</c>, every
/// method carrying <c>LoggerMessageAttribute</c>, delegates from <c>LoggerMessage.Define*</c>, exception
/// construction, and <c>Console</c>, <c>Debug</c> and <c>Trace</c> writes. Taint flows through locals, closures and
/// state machines, interpolation, concatenation, builders, containers and direct calls (method summaries), and is
/// field-level on Decisya objects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Masked</b> means an argument whose static type carries <c>[Sensitive]</c>, passed as itself; an object of a
/// Decisya type whose tainted members all carry <c>[Sensitive]</c> and which has no personal member without it;
/// the output of a listed masker. A scalar <i>read from</i> a <c>[Sensitive]</c> member is NOT masked: the
/// processor judges a <c>string</c> by its run-time type, which does not carry the attribute (G2, "How masking
/// really works").
/// </para>
/// <para>
/// Known residuals (G2 and G3): virtual and interface dispatch, reflection, <c>dynamic</c>, expression trees and
/// delegate bodies are not followed; implicit flows; collections another method fills; framework-emitted logs.
/// </para>
/// </remarks>
internal static class PersonalDataLogRule
{
    /// <summary>Runs the rule over the given assemblies (each with its portable PDB beside it).</summary>
    public static PersonalDataEvaluation Evaluate(IEnumerable<string> assemblyPaths, PersonalDataCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        ArgumentNullException.ThrowIfNull(catalogue);

        using var world = new PersonalDataWorld(assemblyPaths, catalogue);
        return world.Run();
    }

    /// <summary>
    /// R5: the existence checks over the catalogue (G3 S-01). Every <c>Decisya.*</c> entry must name an existing member
    /// or type in the scope; every third-party entry whose assembly is in the reference closure must too; an entry whose
    /// assembly is not referenced at all goes to <paramref name="notReferenced"/>.
    /// </summary>
    public static IReadOnlyList<string> CatalogueProblems(
        IEnumerable<string> assemblyPaths,
        PersonalDataCatalogue catalogue,
        out IReadOnlyList<string> notReferenced)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        ArgumentNullException.ThrowIfNull(catalogue);

        using var world = new PersonalDataWorld(assemblyPaths, catalogue);
        return world.CheckCatalogue(out notReferenced);
    }
}
