using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Modules.Governance.Documentation;

public sealed class GovernanceManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _repositoryRoot;

    public GovernanceManifestService(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    public GovernanceManifest LoadAndValidate()
    {
        var manifestPath = Resolve("governance/manifest.yaml");
        var schemaPath = Resolve("governance/schemas/manifest.schema.json");

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
                .Build();
            var manifest = deserializer.Deserialize<GovernanceManifest>(File.ReadAllText(manifestPath));
            var manifestJson = JsonSerializer.SerializeToElement(manifest, JsonOptions);
            var schema = JsonSchema.FromText(
                File.ReadAllText(schemaPath),
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            var evaluation = schema.Evaluate(
                manifestJson,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                var errors = Flatten(evaluation)
                    .Where(detail => !detail.IsValid && detail.Errors is not null)
                    .Take(20)
                    .Select(detail => $"{detail.InstanceLocation}: {string.Join("; ", detail.Errors!.Values)}");
                throw new GovernanceManifestException(
                    $"governance/manifest.yaml does not conform to the canonical JSON Schema: {string.Join(" | ", errors)}");
            }

            return manifest;
        }
        catch (GovernanceManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or YamlDotNet.Core.YamlException)
        {
            throw new GovernanceManifestException("Unable to load and validate governance/manifest.yaml.", exception);
        }
    }

    public void Save(GovernanceManifest manifest)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        var yaml = string.Join(
            '\n',
            serializer.Serialize(manifest)
                .Split('\n')
                .Select(line => line.TrimEnd()));
        File.WriteAllText(Resolve("governance/manifest.yaml"), yaml.TrimEnd() + "\n");
    }

    private string Resolve(string relativePath) => Path.Combine(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults result)
    {
        yield return result;
        foreach (var detail in result.Details ?? [])
        {
            foreach (var descendant in Flatten(detail))
            {
                yield return descendant;
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
