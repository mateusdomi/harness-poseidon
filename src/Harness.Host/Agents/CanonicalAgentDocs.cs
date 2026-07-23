using Harness.Persistence.Abstractions.Agents;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Host.Agents;

/// <summary>
/// PLAT-06: gerador determinístico do espelho documental versionável das definições canônicas
/// (<see cref="CanonicalAgentDefinitions"/>) sob <c>docs/agents/&lt;key&gt;.yaml</c>. A ÚNICA fonte
/// da verdade continua sendo o código; estes documentos são um reflexo revisável (um por persona)
/// mantido em sincronia por um teste de deriva (drift) que regenera em memória e compara byte a byte.
///
/// A renderização é determinística (ordem de campos fixa, quebras de linha <c>\n</c>, reusa o
/// <c>YamlDotNet</c> já presente no Host, mesma convenção camelCase do CAT-06) e re-executável:
/// dado o mesmo código-fonte, produz saída idêntica.
/// </summary>
public static class CanonicalAgentDocs
{
    public const string Header =
        "# GENERATED from CanonicalAgentDefinitions — do not hand-edit; run the generator.\n" +
        "# Source: src/Harness.Host/Agents/CanonicalAgentDefinitions.cs\n" +
        "# Regenerate: tools/backend/generate-agent-docs.sh\n";

    /// <summary>Diretório (relativo à raiz do repositório) onde os espelhos são escritos.</summary>
    public const string RelativeDirectory = "docs/agents";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>O nome de ficheiro (sem diretório) do espelho de uma definição.</summary>
    public static string FileName(BuiltInAgentDefinitionSeed seed) => $"{seed.Content.Key}.yaml";

    /// <summary>Renderiza o documento YAML completo (cabeçalho + corpo) de uma definição canônica.</summary>
    public static string Render(BuiltInAgentDefinitionSeed seed)
    {
        var content = seed.Content;
        var document = new CanonicalAgentDocument
        {
            Key = content.Key,
            Name = content.Name,
            Role = content.Role,
            Specialty = content.Specialty,
            Description = content.Description,
            Persona = content.Persona,
            Mission = content.Mission,
            OperatingPrinciples = [.. content.OperatingPrinciples],
            Deliverables = [.. content.Deliverables],
            QualityCriteria = [.. content.QualityCriteria],
            CommunicationStyle = content.CommunicationStyle,
            Limitations = [.. content.Limitations],
            Stacks = [.. content.Stacks ?? []],
            DefaultEffort = content.DefaultEffort,
            Team = content.Team,
            ActorCritic = content.ActorCritic,
            Risk = content.Risk,
            SkillIds = [.. content.SkillIds],
            ToolIds = [.. content.ToolIds],
            Owner = seed.Owner,
        };

        var body = Serializer.Serialize(document).Replace("\r\n", "\n");
        return Header + body;
    }

    /// <summary>Renderiza todos os espelhos: nome de ficheiro -&gt; conteúdo, na ordem canônica.</summary>
    public static IReadOnlyList<(string FileName, string Content)> RenderAll() =>
        [.. CanonicalAgentDefinitions.All.Select(seed => (FileName(seed), Render(seed)))];

    /// <summary>
    /// Escreve (ou reescreve) todos os espelhos em <paramref name="repositoryRoot"/>/docs/agents,
    /// de forma determinística. Retorna os caminhos relativos escritos, em ordem.
    /// </summary>
    public static IReadOnlyList<string> Write(string repositoryRoot)
    {
        var directory = Path.Combine(repositoryRoot, "docs", "agents");
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var (fileName, content) in RenderAll())
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
            written.Add($"{RelativeDirectory}/{fileName}");
        }

        return written;
    }

    /// <summary>
    /// Modelo ordenado do espelho declarativo. A ordem de declaração dita a ordem no YAML;
    /// escalares nulos são omitidos (OmitNull), listas são sempre emitidas (ex.: <c>skillIds: []</c>).
    /// </summary>
    private sealed class CanonicalAgentDocument
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string? Specialty { get; init; }
        public string Description { get; init; } = string.Empty;
        public string? Persona { get; init; }
        public string? Mission { get; init; }
        public List<string> OperatingPrinciples { get; init; } = [];
        public List<string> Deliverables { get; init; } = [];
        public List<string> QualityCriteria { get; init; } = [];
        public string? CommunicationStyle { get; init; }
        public List<string> Limitations { get; init; } = [];
        public List<string> Stacks { get; init; } = [];
        public string? DefaultEffort { get; init; }
        public string? Team { get; init; }
        public string? ActorCritic { get; init; }
        public string? Risk { get; init; }
        public List<string> SkillIds { get; init; } = [];
        public List<string> ToolIds { get; init; } = [];
        public string Owner { get; init; } = string.Empty;
    }
}
