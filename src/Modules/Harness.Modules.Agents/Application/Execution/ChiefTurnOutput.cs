using System.Text.Json;

namespace Harness.Modules.Agents.Application.Execution;

/// <param name="Intent">
/// B14: a INTENÇÃO que o modelo atribuiu ao turno. É a única decisão de rota que ele toma — a
/// sequência de passos e as ações permitidas saem da tabela de despacho, que é código.
/// </param>
/// <param name="IntentConfidence">
/// Confiança declarada na classificação. Abaixo do mínimo o turno cai em `unmatched` e perde
/// permissão de agir: aceitar classificação incerta transformaria um palpite do modelo numa rota
/// com permissão de criar trabalho.
/// </param>
public sealed record ChiefTurnOutput(
    string Response,
    IReadOnlyList<ChiefDemandProposal> Demands,
    IReadOnlyList<ChiefTeamAction>? TeamActions = null,
    ChiefTurnIntent Intent = ChiefTurnIntent.Unmatched,
    double IntentConfidence = 0);

/// <summary>
/// Uma intenção de GESTÃO DE EQUIPE emitida pela chefe. Formar e reorganizar a equipe é atribuição
/// dela — o dono do projeto é o stakeholder, não o gerente operacional que escolhe agentes. O que
/// chega aqui é proposta: a policy valida, o catálogo decide e o ledger registra.
/// </summary>
public sealed record ChiefTeamAction(
    string Action,
    string Reason,
    ChiefProposedPersona? Persona = null,
    string? PersonaKey = null);

public sealed record ChiefProposedPersona(
    string Key,
    string Name,
    string Purpose,
    string Specialty,
    IReadOnlyList<string> Responsibilities,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RiskTiers);

/// <summary>
/// A SUPERFÍCIE declarada pelo Chefe para uma demanda: o julgamento dele sobre a natureza do
/// trabalho, que até aqui morria na conversa. Cada campo é uma tri-state deliberada — <c>true</c>
/// afirma que a superfície existe, <c>false</c> afirma que NÃO existe, e ausente/nulo significa
/// "não declarei", deixando o planner inferir do texto (o comportamento anterior).
/// </summary>
public sealed record ChiefDemandSurfaces(
    bool? Frontend = null,
    bool? Backend = null,
    bool? ExternalCredential = null,
    bool? TechnicalUncertainty = null,
    bool? Decision = null);

public sealed record ChiefDemandProposal(
    string Title,
    string Description,
    string RiskTier,
    IReadOnlyList<string> AcceptanceCriteria,
    string? Specialty = null,
    ChiefDemandSurfaces? Surfaces = null);

public static class ChiefTurnOutputContract
{
    private static readonly HashSet<string> RootProperties =
        new(["response", "demands", "teamActions", "intent", "intentConfidence"], StringComparer.Ordinal);
    private static readonly HashSet<string> TeamActionProperties =
        new(["action", "reason", "persona", "personaKey"], StringComparer.Ordinal);
    private static readonly HashSet<string> PersonaProperties =
        new(
            ["key", "name", "purpose", "specialty", "responsibilities", "constraints",
             "requiredCapabilities", "riskTiers"],
            StringComparer.Ordinal);

    /// <summary>
    /// Ações de equipe que a chefe pode emitir. Conjunto FECHADO: uma ação desconhecida é recusada
    /// em vez de interpretada, porque interpretar texto do modelo como comando é exatamente o
    /// caminho por onde a autoridade vaza.
    /// </summary>
    private static readonly HashSet<string> TeamActions =
        new(
            ["create_persona", "observe_persona", "suspend_persona", "reactivate_persona", "promote_persona"],
            StringComparer.Ordinal);
    private static readonly HashSet<string> DemandProperties =
        new(
            ["title", "description", "riskTier", "acceptanceCriteria", "specialty", "surfaces"],
            StringComparer.Ordinal);
    private static readonly HashSet<string> SurfaceProperties =
        new(
            ["frontend", "backend", "externalCredential", "technicalUncertainty", "decision"],
            StringComparer.Ordinal);
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
                criteria,
                ReadOptionalText(demand, "specialty", 1, 100),
                ReadSurfaces(demand)));
        }

        // B14: a classificação é OBRIGATÓRIA no contrato. Ausente ou fora da taxonomia vira
        // `unmatched` — que é uma rota real (turno livre, sem permissão de agir), não um erro.
        var intent = ChiefIntentDispatchTable.Parse(
            root.TryGetProperty("intent", out var intentNode) && intentNode.ValueKind == JsonValueKind.String
                ? intentNode.GetString()
                : null);
        var confidence = root.TryGetProperty("intentConfidence", out var confidenceNode) &&
            confidenceNode.ValueKind == JsonValueKind.Number &&
            confidenceNode.TryGetDouble(out var parsed)
                ? Math.Clamp(parsed, 0, 1)
                : 0;

        return new ChiefTurnOutput(
            response, demands, ReadTeamActions(root),
            ChiefIntentDispatchTable.Resolve(intent, confidence), confidence);
    }

    /// <summary>
    /// Lê as ações de equipe. Ausente devolve nulo — a chefe não precisa mexer na equipe em todo
    /// turno, e um array vazio significa exatamente o mesmo que não declarar.
    /// </summary>
    private static List<ChiefTeamAction>? ReadTeamActions(JsonElement root)
    {
        if (!root.TryGetProperty("teamActions", out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() > 10)
        {
            throw new AgentOutputValidationException(
                "Chief team actions must be an array with at most 10 items.");
        }

        var actions = new List<ChiefTeamAction>();
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new AgentOutputValidationException("Every team action must be an object.");
            }

            EnsureOnlyProperties(entry, TeamActionProperties, "team action");
            var action = ReadRequiredText(entry, "action", 1, 40);
            if (!TeamActions.Contains(action))
            {
                throw new AgentOutputValidationException("Team action is not part of the closed set.");
            }

            // Sem o PORQUÊ não há auditoria possível: o dono precisa poder olhar depois e julgar
            // se a chefe tinha razão em mexer na equipe.
            var reason = ReadRequiredText(entry, "reason", 10, 2_000);
            actions.Add(new ChiefTeamAction(
                action, reason, ReadPersona(entry), ReadOptionalText(entry, "personaKey", 1, 100)));
        }

        return actions.Count == 0 ? null : actions;
    }

    private static ChiefProposedPersona? ReadPersona(JsonElement action)
    {
        if (!action.TryGetProperty("persona", out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new AgentOutputValidationException("Team action persona must be an object.");
        }

        EnsureOnlyProperties(node, PersonaProperties, "team action persona");
        return new ChiefProposedPersona(
            ReadRequiredText(node, "key", 3, 100),
            ReadRequiredText(node, "name", 1, 200),
            ReadRequiredText(node, "purpose", 20, 2_000),
            ReadOptionalText(node, "specialty", 1, 200) ?? string.Empty,
            ReadTextArray(node, "responsibilities"),
            ReadTextArray(node, "constraints"),
            ReadTextArray(node, "requiredCapabilities"),
            ReadTextArray(node, "riskTiers"));
    }

    private static IReadOnlyList<string> ReadTextArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() > 20)
        {
            throw new AgentOutputValidationException(
                $"Persona '{property}' must be an array with at most 20 items.");
        }

        return [.. node.EnumerateArray().Select(item => ReadText(item, property, 1, 500))];
    }

    /// <summary>
    /// Lê a superfície declarada. Ausente (ou totalmente nula) devolve nulo — "não declarei" é um
    /// estado legítimo, distinto de "declarei que não existe", e mantém o planner inferindo do
    /// texto exatamente como antes.
    /// </summary>
    private static ChiefDemandSurfaces? ReadSurfaces(JsonElement demand)
    {
        if (!demand.TryGetProperty("surfaces", out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new AgentOutputValidationException("Demand surfaces must be an object.");
        }

        EnsureOnlyProperties(node, SurfaceProperties, "demand surfaces");
        var surfaces = new ChiefDemandSurfaces(
            ReadOptionalBoolean(node, "frontend"),
            ReadOptionalBoolean(node, "backend"),
            ReadOptionalBoolean(node, "externalCredential"),
            ReadOptionalBoolean(node, "technicalUncertainty"),
            ReadOptionalBoolean(node, "decision"));
        return surfaces == new ChiefDemandSurfaces() ? null : surfaces;
    }

    private static bool? ReadOptionalBoolean(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new AgentOutputValidationException($"Demand surface '{property}' must be a boolean."),
        };
    }

    private static string? ReadOptionalText(
        JsonElement parent,
        string property,
        int minimumLength,
        int maximumLength)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadText(value, property, minimumLength, maximumLength);
    }

    private static JsonElement CreateSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["response", "demands", "intent", "intentConfidence"],
              "properties": {
                "intent": {
                  "type": "string",
                  "enum": ["planejar_demanda", "responder_pergunta", "resumir_progresso",
                           "decidir_escalacao", "aprovar_documento", "decidir_gate_de_fase",
                           "tratar_barreira_externa", "ajustar_projeto",
                           "pedir_status_pessoa_equipe", "conversa_geral"]
                },
                "intentConfidence": { "type": "number", "minimum": 0, "maximum": 1 },
                "teamActions": {
                  "type": ["array", "null"],
                  "maxItems": 10,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["action", "reason"],
                    "properties": {
                      "action": {
                        "type": "string",
                        "enum": ["create_persona", "observe_persona", "suspend_persona",
                                 "reactivate_persona", "promote_persona"]
                      },
                      "reason": { "type": "string", "minLength": 10, "maxLength": 2000 },
                      "personaKey": { "type": ["string", "null"], "minLength": 1, "maxLength": 100 },
                      "persona": {
                        "type": ["object", "null"],
                        "additionalProperties": false,
                        "required": ["key", "name", "purpose"],
                        "properties": {
                          "key": { "type": "string", "minLength": 3, "maxLength": 100 },
                          "name": { "type": "string", "minLength": 1, "maxLength": 200 },
                          "purpose": { "type": "string", "minLength": 20, "maxLength": 2000 },
                          "specialty": { "type": ["string", "null"], "minLength": 1, "maxLength": 200 },
                          "responsibilities": { "type": "array", "maxItems": 20, "items": { "type": "string" } },
                          "constraints": { "type": "array", "maxItems": 20, "items": { "type": "string" } },
                          "requiredCapabilities": { "type": "array", "maxItems": 20, "items": { "type": "string" } },
                          "riskTiers": { "type": "array", "maxItems": 20, "items": { "type": "string" } }
                        }
                      }
                    }
                  }
                },
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
                      },
                      "specialty": { "type": ["string", "null"], "minLength": 1, "maxLength": 100 },
                      "surfaces": {
                        "type": ["object", "null"],
                        "additionalProperties": false,
                        "properties": {
                          "frontend": { "type": ["boolean", "null"] },
                          "backend": { "type": ["boolean", "null"] },
                          "externalCredential": { "type": ["boolean", "null"] },
                          "technicalUncertainty": { "type": ["boolean", "null"] },
                          "decision": { "type": ["boolean", "null"] }
                        }
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
