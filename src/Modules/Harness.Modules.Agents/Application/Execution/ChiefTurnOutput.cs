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
    double IntentConfidence = 0,
    IReadOnlyList<ChiefCardAction>? CardActions = null,
    IReadOnlyList<ChiefContextRequest>? ContextRequests = null,
    ChiefUnderstandingUpdate? UnderstandingUpdate = null);

public sealed record ChiefUnderstandingUpdate(
    string? ProjectSummary = null,
    string? ProductGoal = null,
    IReadOnlyList<string>? PrimaryUsers = null,
    IReadOnlyList<string>? Requirements = null,
    IReadOnlyList<string>? AcceptanceCriteria = null,
    IReadOnlyList<string>? ImportantConstraints = null,
    IReadOnlyList<string>? Assumptions = null,
    IReadOnlyList<string>? Decisions = null);

/// <summary>
/// Pedido da chefe por seções INTEGRAIS de um anexo (Onda 0.7). É consumido DENTRO do executor —
/// que busca as seções e reinvoca o turno com elas — e nunca chega ao worker: pedir contexto não
/// é uma ação sobre o mundo, é uma etapa da resposta.
/// </summary>
public sealed record ChiefContextRequest(string File, IReadOnlyList<string> Sections);

/// <summary>
/// Decisão do dono sobre um card ESCALADO, traduzida em ação.
///
/// Existe porque o laço de escalação ficava aberto: a Bruna chamava o dono, ele respondia
/// reduzindo o escopo, ela registrava a decisão na conversa — e o card continuava escalado. Pior,
/// ela anunciava que "essa parte volta a andar" enquanto nada mudava, relatando um progresso que
/// não houve. Sem uma ação estruturada, a decisão humana morria como texto.
///
/// O que chega aqui é PROPOSTA: a instrução do dono vira a nova instrução do card e o
/// replanejamento é aplicado pela cadeia, que valida estado e versão. A chefe não escreve no
/// banco por conta própria.
/// </summary>
public sealed record ChiefCardAction(string Action, string CardId, string Instruction);

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
        new(["response", "demands", "teamActions", "cardActions", "intent", "intentConfidence",
             "contextRequests", "understandingUpdate"],
            StringComparer.Ordinal);
    private static readonly HashSet<string> V3RootProperties =
        new(["response", "intent", "intentConfidence", "understandingUpdate", "contextRequests"],
            StringComparer.Ordinal);
    private static readonly HashSet<string> UnderstandingUpdateProperties =
        new(["projectSummary", "productGoal", "primaryUsers", "requirements", "acceptanceCriteria",
             "importantConstraints", "assumptions", "decisions"],
            StringComparer.Ordinal);
    private static readonly HashSet<string> ContextRequestProperties =
        new(["file", "sections"], StringComparer.Ordinal);
    private static readonly HashSet<string> TeamActionProperties =
        new(["action", "reason", "persona", "personaKey"], StringComparer.Ordinal);
    private static readonly HashSet<string> CardActionProperties =
        new(["action", "cardId", "instruction", "reason"], StringComparer.Ordinal);
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

        var v3Output = !root.TryGetProperty("demands", out _);
        EnsureOnlyProperties(root, v3Output ? V3RootProperties : RootProperties, "chief output");
        var response = ReadRequiredText(root, "response", 1, 100_000);
        var demands = new List<ChiefDemandProposal>();
        if (!v3Output)
        {
            if (!root.TryGetProperty("demands", out var demandsNode) ||
                demandsNode.ValueKind != JsonValueKind.Array ||
                demandsNode.GetArrayLength() > 20)
            {
                throw new AgentOutputValidationException("Chief output demands must be an array with at most 20 items.");
            }

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
        }

        // Este contrato é COMPARTILHADO: além do turno da chefe, validam por aqui a detecção de
        // serviços, os executores de CLI e o simulado. A classificação de intenção só faz sentido
        // num TURNO — exigi-la aqui cobraria de contextos que não tomam decisão de rota.
        //
        // Quem exige a classificação é `ParseChiefTurn`, usado somente no caminho do turno.
        var intent = ChiefIntentDispatchTable.Parse(
            root.TryGetProperty("intent", out var intentNode) && intentNode.ValueKind == JsonValueKind.String
                ? intentNode.GetString()
                : null);
        var confidence = root.TryGetProperty("intentConfidence", out var confidenceNode) &&
            confidenceNode.ValueKind == JsonValueKind.Number &&
            confidenceNode.TryGetDouble(out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0, 1)
                : 0;

        return new ChiefTurnOutput(
            response, demands, ReadTeamActions(root),
            ChiefIntentDispatchTable.Resolve(intent, confidence), confidence,
            ReadCardActions(root),
            ReadContextRequests(root),
            ReadUnderstandingUpdate(root));
    }

    private static ChiefUnderstandingUpdate? ReadUnderstandingUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("understandingUpdate", out var node) ||
            node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new AgentOutputValidationException("understandingUpdate must be an object.");
        }

        EnsureOnlyProperties(node, UnderstandingUpdateProperties, "understanding update");
        return new ChiefUnderstandingUpdate(
            ReadOptionalText(node, "projectSummary", 1, 3_000),
            ReadOptionalText(node, "productGoal", 1, 2_000),
            ReadOptionalStringArray(node, "primaryUsers", 50, 500),
            ReadOptionalStringArray(node, "requirements", 200, 1_000),
            ReadOptionalStringArray(node, "acceptanceCriteria", 300, 2_000),
            ReadOptionalStringArray(node, "importantConstraints", 100, 1_000),
            ReadOptionalStringArray(node, "assumptions", 100, 1_000),
            ReadOptionalStringArray(node, "decisions", 100, 1_000));
    }

    /// <summary>
    /// Lê os pedidos de seção integral de anexo. Limites apertados (5 pedidos × 10 seções) porque
    /// cada seção volta COMPLETA para o prompt — o teto protege a janela de contexto, não o modelo.
    /// </summary>
    private static List<ChiefContextRequest>? ReadContextRequests(JsonElement root)
    {
        if (!root.TryGetProperty("contextRequests", out var node) ||
            node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() > 5)
        {
            throw new AgentOutputValidationException(
                "Chief context requests must be an array with at most 5 items.");
        }

        var requests = new List<ChiefContextRequest>();
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new AgentOutputValidationException("Every context request must be an object.");
            }

            EnsureOnlyProperties(entry, ContextRequestProperties, "context request");
            var file = ReadRequiredText(entry, "file", 1, 200);
            if (!entry.TryGetProperty("sections", out var sectionsNode) ||
                sectionsNode.ValueKind != JsonValueKind.Array ||
                sectionsNode.GetArrayLength() is < 1 or > 10)
            {
                throw new AgentOutputValidationException(
                    "Context request sections must contain 1 to 10 items.");
            }

            var sections = sectionsNode.EnumerateArray()
                .Select(item => ReadText(item, "context request section", 1, 100))
                .ToArray();
            requests.Add(new ChiefContextRequest(file, sections));
        }

        return requests.Count == 0 ? null : requests;
    }

    /// <summary>
    /// Lê as ações sobre cards já existentes. Hoje só `replan`, e de propósito: reabrir um card
    /// escalado é o caminho de volta que a decisão do dono precisa ter. Qualquer outra
    /// manipulação de card continua sendo do laço do chefe, não de um turno de conversa.
    /// </summary>
    private static List<ChiefCardAction>? ReadCardActions(JsonElement root)
    {
        if (!root.TryGetProperty("cardActions", out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() > 10)
        {
            throw new AgentOutputValidationException(
                "Chief card actions must be an array with at most 10 items.");
        }

        var actions = new List<ChiefCardAction>();
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new AgentOutputValidationException("Every card action must be an object.");
            }

            EnsureOnlyProperties(entry, CardActionProperties, "card action");
            var action = ReadRequiredText(entry, "action", 1, 40);
            if (!string.Equals(action, "replan", StringComparison.Ordinal))
            {
                throw new AgentOutputValidationException("Card action is not part of the closed set.");
            }

            // O identificador é ULID e tem tamanho fixo: aceitar texto livre aqui deixaria o
            // modelo apontar para qualquer coisa.
            var cardId = ReadRequiredText(entry, "cardId", 26, 26);

            // A instrução é o que o dono decidiu, e ela SUBSTITUI o enunciado anterior. Um texto
            // curto demais não redireciona trabalho nenhum.
            var instruction = ReadRequiredText(entry, "instruction", 20, 10_000);
            actions.Add(new ChiefCardAction(action, cardId, instruction));
        }

        return actions.Count == 0 ? null : actions;
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

    private static IReadOnlyList<string>? ReadOptionalStringArray(
        JsonElement parent,
        string property,
        int maxItems,
        int maxItemLength)
    {
        if (!parent.TryGetProperty(property, out var node) || node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() > maxItems)
        {
            throw new AgentOutputValidationException($"{property} must be an array with at most {maxItems} items.");
        }

        return [.. node.EnumerateArray()
            .Select(item => ReadText(item, property, 1, maxItemLength))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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

    /// <summary>
    /// O contrato do TURNO DA CHEFE: além de tudo o que <see cref="Parse"/> exige, a
    /// classificação de intenção é obrigatória.
    ///
    /// A ausência FALHA de propósito — e a falha é o que aciona a rodada de reparo do executor, na
    /// qual o modelo corrige a própria saída. Degradar em silêncio para `unmatched` parecia seguro
    /// e era pior: o turno perdia a permissão de agir, o dono recebia uma resposta simpática,
    /// nenhum trabalho era criado e NADA explicava. Foi exatamente o que aconteceu no primeiro
    /// piloto real — a chefe respondeu, propôs demandas, e o portão as descartou porque o modelo
    /// esqueceu um campo. Pedir de novo custa uma chamada; perder o trabalho custa o projeto.
    /// </summary>
    public static ChiefTurnOutput ParseChiefTurn(string json)
    {
        var output = Parse(json);
        if (output.Intent == ChiefTurnIntent.Unmatched && output.IntentConfidence == 0)
        {
            throw new AgentOutputValidationException(
                "Chief output must classify the turn in `intent` (one of the declared values) " +
                "and declare `intentConfidence` between 0 and 1.");
        }

        return output;
    }

    public static JsonElement V3JsonSchema { get; } = CreateV3Schema();

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
                "contextRequests": {
                  "type": ["array", "null"],
                  "maxItems": 5,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["file", "sections"],
                    "properties": {
                      "file": { "type": "string", "minLength": 1, "maxLength": 200 },
                      "sections": {
                        "type": "array",
                        "minItems": 1,
                        "maxItems": 10,
                        "items": { "type": "string", "minLength": 1, "maxLength": 100 }
                      }
                    }
                  }
                },
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
                "cardActions": {
                  "type": ["array", "null"],
                  "maxItems": 10,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["action", "cardId", "instruction"],
                    "properties": {
                      "action": { "type": "string", "enum": ["replan"] },
                      "cardId": { "type": "string", "minLength": 26, "maxLength": 26 },
                      "instruction": { "type": "string", "minLength": 20, "maxLength": 10000 },
                      "reason": { "type": ["string", "null"], "maxLength": 2000 }
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

    private static JsonElement CreateV3Schema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["response", "intent", "intentConfidence"],
              "properties": {
                "intent": {
                  "type": "string",
                  "enum": ["understand_project", "answer_question", "summarize_status",
                           "record_user_decision", "request_human_input",
                           "conversation_general"]
                },
                "intentConfidence": { "type": "number", "minimum": 0, "maximum": 1 },
                "response": { "type": "string", "minLength": 1, "maxLength": 100000 },
                "understandingUpdate": {
                  "type": ["object", "null"],
                  "additionalProperties": false,
                  "properties": {
                    "projectSummary": { "type": ["string", "null"], "maxLength": 3000 },
                    "productGoal": { "type": ["string", "null"], "maxLength": 2000 },
                    "primaryUsers": {
                      "type": ["array", "null"],
                      "maxItems": 50,
                      "items": { "type": "string", "minLength": 1, "maxLength": 500 }
                    },
                    "requirements": {
                      "type": ["array", "null"],
                      "maxItems": 200,
                      "items": { "type": "string", "minLength": 1, "maxLength": 1000 }
                    },
                    "acceptanceCriteria": {
                      "type": ["array", "null"],
                      "maxItems": 300,
                      "items": { "type": "string", "minLength": 1, "maxLength": 2000 }
                    },
                    "importantConstraints": {
                      "type": ["array", "null"],
                      "maxItems": 100,
                      "items": { "type": "string", "minLength": 1, "maxLength": 1000 }
                    },
                    "assumptions": {
                      "type": ["array", "null"],
                      "maxItems": 100,
                      "items": { "type": "string", "minLength": 1, "maxLength": 1000 }
                    },
                    "decisions": {
                      "type": ["array", "null"],
                      "maxItems": 100,
                      "items": { "type": "string", "minLength": 1, "maxLength": 1000 }
                    }
                  }
                },
                "contextRequests": {
                  "type": ["array", "null"],
                  "maxItems": 5,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["file", "sections"],
                    "properties": {
                      "file": { "type": "string", "minLength": 1, "maxLength": 200 },
                      "sections": {
                        "type": "array",
                        "minItems": 1,
                        "maxItems": 10,
                        "items": { "type": "string", "minLength": 1, "maxLength": 100 }
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
