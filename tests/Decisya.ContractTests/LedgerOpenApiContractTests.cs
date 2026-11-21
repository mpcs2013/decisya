using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Decisya.Api.Authentication;
using Decisya.Api.OpenApi;
using Decisya.Modules.Ledger.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Decisya.ContractTests;

/// <summary>
/// #341 G2 D9, rule R-L20: the <c>ledger</c> OpenAPI document generated in process by the real Api host
/// deep-equals the committed <c>openapi/ledger.json</c> embedded in <c>Decisya.Modules.Ledger.Contracts</c>.
/// Only <c>servers</c> and <c>info.version</c> are ignored; any other drift fails and names the first
/// differing path. No route serves the document (M341-06) and no database is needed.
/// </summary>
[Trait("Category", "Contract")]
public class LedgerOpenApiContractTests
{
    private const string ResourceName = "Decisya.Modules.Ledger.Contracts.openapi.ledger.json";
    private const int MaxReportedDifferences = 200;
    private const int MaxValueLength = 300;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // Program is internal to Decisya.Api, and Decisya.Api.Tests alone has InternalsVisibleTo. WebApplicationFactory
    // needs only a type of the Api assembly to find its entry point, so a public Api type stands in for Program.
    private sealed class ApiFactory : WebApplicationFactory<WhoAmIResponse>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Program.cs reads these three eagerly, before Build(): only UseSetting reaches that read
            // (see tests/Decisya.Api.Tests/Authentication/ApiTestFactory.cs). Host=db.invalid never connects.
            builder.UseSetting("ConnectionStrings:tenancy", "Host=db.invalid;Port=5432;Database=decisya;Username=decisya_tenancy;Password=placeholder");
            builder.UseSetting("ConnectionStrings:entitlements", "Host=db.invalid;Database=decisya;Username=placeholder;Password=placeholder");
            builder.UseSetting("ConnectionStrings:ledger", "Host=db.invalid;Database=decisya;Username=placeholder;Password=placeholder");

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Api:Jwt:Authority"] = "https://issuer.test/realms/decisya",
                ["AllowedHosts"] = "localhost",
            }));
        }
    }

    [Fact]
    public async Task The_generated_ledger_document_deep_equals_the_committed_openapi_ledger_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = new ApiFactory();
        var provider = factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(LedgerOpenApi.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken);
        var generatedJson = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);

        var generated = JsonNode.Parse(generatedJson)!;
        var committed = JsonNode.Parse(ReadCommittedContract())!;

        Normalise(generated);
        Normalise(committed);

        var differences = new List<string>();
        CollectDifferences(committed, generated, "$", differences);

        // On any difference only: the normalised generated document, so the committed file can be reconciled from real output.
        var generatedPath = Path.Combine(AppContext.BaseDirectory, "ledger.generated.json");
        var failed = differences.Count > 0 || !JsonNode.DeepEquals(committed, generated);
        if (failed)
        {
            await File.WriteAllTextAsync(generatedPath, generated.ToJsonString(Indented), new UTF8Encoding(false), cancellationToken);
        }

        var shown = string.Join(Environment.NewLine, differences.Take(MaxReportedDifferences));
        var rest = differences.Count > MaxReportedDifferences
            ? $"{Environment.NewLine}... and {differences.Count - MaxReportedDifferences} more"
            : string.Empty;
        differences.Should().BeEmpty(
            "the generated ledger document must equal openapi/ledger.json except servers and info.version."
            + " {0} difference(s), committed vs generated:{1}{2}{3}{1}The normalised generated document was written to {4}",
            differences.Count, Environment.NewLine, shown, rest, generatedPath);
        JsonNode.DeepEquals(committed, generated).Should().BeTrue();
    }

    private static string ReadCommittedContract()
    {
        var assembly = typeof(LedgerErrorCodes).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        stream.Should().NotBeNull($"{ResourceName} must be an embedded resource of {assembly.GetName().Name}");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>Removes the only members the comparison ignores: <c>servers</c> and <c>info.version</c>.</summary>
    private static void Normalise(JsonNode document)
    {
        var root = document.AsObject();
        root.Remove("servers");
        root["info"]?.AsObject().Remove("version");
    }

    /// <summary>
    /// Appends every difference below <paramref name="path"/> as <c>path: committed X vs generated Y</c>, or
    /// "missing on one side"; arrays are compared element by element.
    /// </summary>
    private static void CollectDifferences(JsonNode? committed, JsonNode? generated, string path, List<string> differences)
    {
        if (JsonNode.DeepEquals(committed, generated))
        {
            return;
        }

        if (committed is JsonObject committedObject && generated is JsonObject generatedObject)
        {
            foreach (var (key, value) in committedObject)
            {
                if (generatedObject.TryGetPropertyValue(key, out var other))
                {
                    CollectDifferences(value, other, $"{path}.{key}", differences);
                }
                else
                {
                    differences.Add($"{path}.{key}: committed {Show(value)} vs generated (missing on the generated side)");
                }
            }

            foreach (var (key, value) in generatedObject)
            {
                if (!committedObject.ContainsKey(key))
                {
                    differences.Add($"{path}.{key}: committed (missing on the committed side) vs generated {Show(value)}");
                }
            }

            return;
        }

        if (committed is JsonArray committedArray && generated is JsonArray generatedArray)
        {
            var common = Math.Min(committedArray.Count, generatedArray.Count);
            for (var i = 0; i < common; i++)
            {
                CollectDifferences(committedArray[i], generatedArray[i], $"{path}[{i}]", differences);
            }

            for (var i = common; i < committedArray.Count; i++)
            {
                differences.Add($"{path}[{i}]: committed {Show(committedArray[i])} vs generated (missing on the generated side)");
            }

            for (var i = common; i < generatedArray.Count; i++)
            {
                differences.Add($"{path}[{i}]: committed (missing on the committed side) vs generated {Show(generatedArray[i])}");
            }

            return;
        }

        differences.Add($"{path}: committed {Show(committed)} vs generated {Show(generated)}");
    }

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length <= MaxValueLength ? text : string.Concat(text.AsSpan(0, MaxValueLength), "...");
    }
}
