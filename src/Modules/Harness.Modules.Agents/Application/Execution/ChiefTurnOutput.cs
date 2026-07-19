using System.Text.Json;

namespace Harness.Modules.Agents.Application.Execution;

public sealed record ChiefTurnOutput(
    string Response,
    IReadOnlyList<ChiefDemandProposal> Demands);

public sealed record ChiefDemandProposal(
    string Title,
    string Description,
    string RiskTier,
    IReadOnlyList<string> AcceptanceCriteria);

public static class ChiefTurnOutputContract
{
    private static readonly HashSet<string> RootProperties =
        new(["response", "demands"], StringComparer.Ordinal);
    private static readonly HashSet<string> DemandProperties =
        new(["title", "description", "riskTier", "acceptanceCriteria"], StringComparer.Ordinal);
    private static readonly HashSet<string> RiskTiers =
        new(["low", "medium", "high", "critical"], StringComparer.Ordinal);

    public static JsonElement JsonSchema { get; } = CreateSchema();

    public static ChiefTurnOutput Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AgentOutputValidationException("Chief output must be a JSON object.");
        }

        EnsureOnlyProperties(root, RootProperties, "chief output");
        var response = ReadRequiredText(root, "response", 1, 100_000);
        if (!root.TryGetProperty("demands", out var demandsNode) ||
            demandsNode.ValueKind != JsonValueKind.Array ||
            demandsNode.GetArrayLength() > 20)
        {
            throw new AgentOutputValidationException("Chief output demands must be an array with at most 20 items.");
        }

        var demands = new List<ChiefDemandProposal>();
        foreach (var demand in demandsNode.EnumerateArray())
        {
            if (demand.ValueKind != JsonValueKind.Object)
            {
                throw new AgentOutputValidationException("Every demand proposal must be an object.");
            }

            EnsureOnlyProperties(demand, DemandProperties, "demand proposal");
            var riskTier = ReadRequiredText(demand, "riskTier", 1, 20);
            if (!RiskTiers.Contains(riskTier))
            {
                throw new AgentOutputValidationException("Demand riskTier is invalid.");
            }

            if (!demand.TryGetProperty("acceptanceCriteria", out var criteriaNode) ||
                criteriaNode.ValueKind != JsonValueKind.Array ||
                criteriaNode.GetArrayLength() is < 1 or > 30)
            {
                throw new AgentOutputValidationException("Demand acceptanceCriteria must contain 1 to 30 items.");
            }

            var criteria = criteriaNode.EnumerateArray()
                .Select(item => ReadText(item, "acceptance criterion", 1, 2_000))
                .ToArray();
            demands.Add(new ChiefDemandProposal(
                ReadRequiredText(demand, "title", 1, 200),
                ReadRequiredText(demand, "description", 1, 10_000),
                riskTier,
                criteria));
        }

        return new ChiefTurnOutput(response, demands);
    }

    private static JsonElement CreateSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["response", "demands"],
              "properties": {
                "response": { "type": "string", "minLength": 1, "maxLength": 100000 },
                "demands": {
                  "type": "array",
                  "maxItems": 20,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["title", "description", "riskTier", "acceptanceCriteria"],
                    "properties": {
                      "title": { "type": "string", "minLength": 1, "maxLength": 200 },
                      "description": { "type": "string", "minLength": 1, "maxLength": 10000 },
                      "riskTier": { "type": "string", "enum": ["low", "medium", "high", "critical"] },
                      "acceptanceCriteria": {
                        "type": "array",
                        "minItems": 1,
                        "maxItems": 30,
                        "items": { "type": "string", "minLength": 1, "maxLength": 2000 }
                      }
                    }
                  }
                }
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private static void EnsureOnlyProperties(
        JsonElement value,
        HashSet<string> allowed,
        string description)
    {
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw new AgentOutputValidationException($"The {description} contains an unknown property.");
        }
    }

    private static string ReadRequiredText(
        JsonElement parent,
        string property,
        int minimumLength,
        int maximumLength)
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            throw new AgentOutputValidationException($"Chief output property '{property}' is required.");
        }

        return ReadText(value, property, minimumLength, maximumLength);
    }

    private static string ReadText(
        JsonElement value,
        string description,
        int minimumLength,
        int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AgentOutputValidationException($"{description} must be a string.");
        }

        var text = value.GetString()?.Trim() ?? string.Empty;
        if (text.Length < minimumLength || text.Length > maximumLength)
        {
            throw new AgentOutputValidationException($"{description} length is invalid.");
        }

        return text;
    }
}

public sealed class AgentOutputValidationException(string message) : Exception(message);
