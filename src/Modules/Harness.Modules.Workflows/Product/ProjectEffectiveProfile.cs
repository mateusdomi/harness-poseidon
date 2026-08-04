using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Modalidade do produto pedido. É a primeira decisão da esteira e a que determina o que "pronto"
/// significa: um "sistema de empréstimos" e "uma API de empréstimos" têm o mesmo domínio e
/// entregáveis mínimos completamente diferentes.
/// </summary>
public enum ProductModality
{
    /// <summary>Não resolvida ainda. Nunca é tratada como "não precisa de nada".</summary>
    Unspecified,
    Web,
    ApiOnly,
    Worker,
    Desktop,
    Mobile,
    Library,
}

/// <summary>
/// De onde uma decisão técnica veio. A ordem do enum É a precedência, do menor para o maior peso:
/// preferência do agente não aparece porque não é fonte válida em nenhum nível.
/// </summary>
public enum ProfileAuthority
{
    /// <summary>Default global do Poseidon quando ninguém se pronunciou.</summary>
    Baseline,

    /// <summary>Restrição da organização ou do tenant.</summary>
    OrganizationConstraint,

    /// <summary>Requisito técnico explícito do usuário ou do projeto.</summary>
    ProjectRequirement,

    /// <summary>Decisão arquitetural aprovada e registrada (ADR).</summary>
    ApprovedDecision,

    /// <summary>Restrição legal, regulatória, de segurança ou de compliance obrigatória.</summary>
    Regulatory,
}

/// <summary>Uma decisão sobre uma área do perfil, com origem e justificativa.</summary>
public sealed record ProfileDirective(
    ProfileAuthority Authority,
    string Area,
    string Value,
    string Reason,
    string? AdrId = null);

/// <summary>Um default que deixou de valer, com a razão e o ADR que o sustenta.</summary>
public sealed record ProfileOverride(
    string Area,
    string DefaultValue,
    string OverrideValue,
    ProfileAuthority Authority,
    string Reason,
    string? AdrId);

public sealed record BackendProfile(
    bool Required,
    string? Runtime,
    string? Framework,
    string? Language,
    string? ArchitectureStyle);

public sealed record FrontendProfile(
    bool Required,
    string? Framework,
    string? Language,
    string? BuildSystem,
    string? DesignSystem);

public sealed record DataProfile(
    bool Required,
    string? Database,
    string? MigrationStrategy,
    IReadOnlyList<string> ApprovedTechnologies);

public sealed record ApiProfile(
    bool Required,
    string? Protocol,
    bool OpenApiRequired,
    string? Versioning);

public sealed record SecurityProfile(
    string? Authentication,
    string? Authorization,
    IReadOnlyList<string> Constraints);

public sealed record OperationProfile(
    string? Hosting,
    string? Observability,
    IReadOnlyList<string> Requirements);

/// <summary>
/// A configuração técnica que EFETIVAMENTE vale para um projeto — o <i>constraint profile</i> que
/// o gate da Fase 3 já cobrava (<c>aderência ao constraint profile — desvio exige ADR</c>) e que
/// não existia em documento, schema ou código.
///
/// É deliberadamente machine-readable: a versão humana em Markdown é derivada dele, nunca a fonte.
/// Um perfil que só existisse em prosa não poderia ser lido por um gate.
/// </summary>
public sealed record ProjectEffectiveProfile(
    string ProjectId,
    string BaselineVersion,
    ProductModality Modality,
    BackendProfile Backend,
    FrontendProfile Frontend,
    DataProfile Data,
    ApiProfile Api,
    SecurityProfile Security,
    OperationProfile Operation,
    IReadOnlyList<ProfileOverride> Overrides,
    IReadOnlyList<string> ActiveAdrs,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> ResolutionSources)
{
    /// <summary>Versão do baseline global vigente quando este perfil foi resolvido.</summary>
    public const string CurrentBaselineVersion = "1.0.0";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Desserializa um perfil persistido. Devolve <see langword="null"/> em vez de lançar quando o
    /// conteúdo é inválido ou de uma versão que não sabemos ler: o chamador decide se isso bloqueia
    /// a execução (fail-closed) ou apenas resolve o perfil de novo. Perfil corrompido nunca vira
    /// perfil "vazio que passa em tudo".
    /// </summary>
    public static ProjectEffectiveProfile? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectEffectiveProfile>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Identidade estável do conteúdo do perfil. Permite ao recibo de governança dizer QUAL perfil
    /// a execução usou, e não apenas que havia um.
    /// </summary>
    public string Fingerprint() =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(ToJson())))[..16];
}
