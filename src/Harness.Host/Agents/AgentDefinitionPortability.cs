using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Persistence.Abstractions.Agents;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Host.Agents;

/// <summary>
/// Import/export portável de definições de agente (CAT-06). O documento carrega apenas o
/// CONTEÚDO da definição (as ~22 dimensões da persona) — nunca identidade de servidor
/// (id, tenant, owner, version, enabled, archived), de modo que o documento é portável
/// entre tenants e não pode contrabandear privilégio. A serialização é oferecida em JSON
/// e YAML com a mesma convenção camelCase da API; a desserialização REJEITA campos
/// desconhecidos (STJ <c>Disallow</c>; YamlDotNet lança em propriedade não mapeada).
///
/// Round-trip idempotente: <c>export -&gt; import -&gt; export</c> produz um documento
/// equivalente, porque o conteúdo exportado é exatamente o conteúdo re-importável e a
/// colisão de chave é resolvida de forma determinística (update-by-key por padrão).
/// </summary>
public static class AgentDefinitionPortability
{
    public const string SchemaVersion = "harness.agent-definition/v1";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    // Sem IgnoreUnmatchedProperties: propriedade desconhecida no YAML lança YamlException,
    // que o endpoint traduz num 400 tipado — o documento não pode injetar campos inesperados.
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static string Serialize(AgentDefinitionExportDocument document, AgentDocumentFormat format) =>
        format == AgentDocumentFormat.Yaml
            ? YamlSerializer.Serialize(document)
            : JsonSerializer.Serialize(document, JsonOptions);

    /// <summary>Desserializa o documento; lança <see cref="AgentDocumentFormatException"/> quando inválido.</summary>
    public static AgentDefinitionExportDocument Deserialize(string payload, AgentDocumentFormat format)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new AgentDocumentFormatException("The definition document body is empty.");
        }

        try
        {
            var document = format == AgentDocumentFormat.Yaml
                ? YamlDeserializer.Deserialize<AgentDefinitionExportDocument>(payload)
                : JsonSerializer.Deserialize<AgentDefinitionExportDocument>(payload, JsonOptions);
            if (document is null)
            {
                throw new AgentDocumentFormatException("The definition document could not be parsed.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new AgentDocumentFormatException($"The definition document is not valid JSON: {exception.Message}");
        }
        catch (YamlException exception)
        {
            throw new AgentDocumentFormatException($"The definition document is not valid YAML: {exception.Message}");
        }
    }

    /// <summary>
    /// Resolve o formato a partir do query param <c>format</c> (json|yaml|yml) e, na sua
    /// ausência, do header (Accept para export, Content-Type para import). O padrão é JSON.
    /// </summary>
    public static bool TryResolveFormat(string? formatQuery, string? header, out AgentDocumentFormat format)
    {
        format = AgentDocumentFormat.Json;
        if (!string.IsNullOrWhiteSpace(formatQuery))
        {
            switch (formatQuery.Trim().ToLowerInvariant())
            {
                case "json": format = AgentDocumentFormat.Json; return true;
                case "yaml" or "yml": format = AgentDocumentFormat.Yaml; return true;
                default: return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(header) &&
            (header.Contains("yaml", StringComparison.OrdinalIgnoreCase) ||
             header.Contains("yml", StringComparison.OrdinalIgnoreCase)))
        {
            format = AgentDocumentFormat.Yaml;
        }

        return true;
    }

    public static string ContentType(AgentDocumentFormat format) =>
        format == AgentDocumentFormat.Yaml ? "application/yaml" : "application/json";

    public static AgentDefinitionDocument ToDocument(AgentDefinitionRecord value) => new()
    {
        Key = value.Key,
        Name = value.Name,
        Role = value.Role,
        Specialty = value.Specialty,
        Description = value.Description,
        DefaultModelId = value.DefaultModelId,
        SkillIds = [.. value.SkillIds],
        ToolIds = [.. value.ToolIds],
        Persona = value.Persona,
        Mission = value.Mission,
        OperatingPrinciples = [.. value.OperatingPrinciples ?? []],
        Deliverables = [.. value.Deliverables ?? []],
        QualityCriteria = [.. value.QualityCriteria ?? []],
        CommunicationStyle = value.CommunicationStyle,
        Limitations = [.. value.Limitations ?? []],
        Stacks = [.. value.Stacks ?? []],
        DefaultEffort = value.DefaultEffort,
        PreferredAccountId = value.PreferredAccountId,
        FallbackModelIds = [.. value.FallbackModelIds ?? []],
        Team = value.Team,
        ActorCritic = value.ActorCritic,
        Risk = value.Risk,
    };

    public static AgentDefinitionContent ToContent(AgentDefinitionDocument document) => new(
        document.Key?.Trim() ?? string.Empty, document.Name ?? string.Empty, document.Role ?? string.Empty,
        Blank(document.Specialty), document.Description ?? string.Empty, Blank(document.DefaultModelId),
        document.SkillIds ?? [], document.ToolIds ?? [], Blank(document.Persona), Blank(document.Mission),
        document.OperatingPrinciples ?? [], document.Deliverables ?? [], document.QualityCriteria ?? [],
        Blank(document.CommunicationStyle), document.Limitations ?? [], document.Stacks ?? [],
        Blank(document.DefaultEffort), Blank(document.PreferredAccountId), document.FallbackModelIds ?? [],
        Blank(document.Team), Blank(document.ActorCritic), Blank(document.Risk));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum AgentDocumentFormat
{
    Json,
    Yaml,
}

public sealed class AgentDocumentFormatException(string detail) : Exception(detail);

/// <summary>
/// Documento portável de definição(ões) de agente. É um envelope com versão de esquema e
/// uma lista de definições, uniforme para export único e em lote.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AgentDefinitionExportDocument
{
    public string SchemaVersion { get; set; } = AgentDefinitionPortability.SchemaVersion;
    public List<AgentDefinitionDocument> Definitions { get; set; } = [];
}

/// <summary>
/// Conteúdo portável de uma definição — as dimensões da persona, sem identidade de servidor.
/// Campos desconhecidos são rejeitados (JSON <c>Disallow</c> / YAML lança).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AgentDefinitionDocument
{
    public string? Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Specialty { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DefaultModelId { get; set; }
    public List<string> SkillIds { get; set; } = [];
    public List<string> ToolIds { get; set; } = [];
    public string? Persona { get; set; }
    public string? Mission { get; set; }
    public List<string> OperatingPrinciples { get; set; } = [];
    public List<string> Deliverables { get; set; } = [];
    public List<string> QualityCriteria { get; set; } = [];
    public string? CommunicationStyle { get; set; }
    public List<string> Limitations { get; set; } = [];
    public List<string> Stacks { get; set; } = [];
    public string? DefaultEffort { get; set; }
    public string? PreferredAccountId { get; set; }
    public List<string> FallbackModelIds { get; set; } = [];
    public string? Team { get; set; }
    public string? ActorCritic { get; set; }
    public string? Risk { get; set; }
}

public sealed record AgentImportResultContract(IReadOnlyList<AgentImportItemContract> Imported);
public sealed record AgentImportItemContract(string Id, string Key, string Name, string Outcome);
