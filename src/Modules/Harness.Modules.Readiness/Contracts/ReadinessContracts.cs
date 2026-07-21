using System.Text.Json.Serialization;

namespace Harness.Modules.Readiness.Contracts;

/// <summary>
/// Estado de configuração fechado de uma dependência do golden path (ADR-017).
/// Serializa como o nome do membro (PascalCase) para bater com o contrato publicado.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationState>))]
public enum ConfigurationState
{
    Unconfigured,
    Simulated,
    Configured,
    Ready,
    Degraded,
    Unavailable,
}

/// <summary>Etapas de prontidão na ordem canônica do golden path (ADR-017).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadinessStep>))]
public enum ReadinessStep
{
    ProfileReady,
    OrganizationReady,
    ProjectReady,
    ProviderAccountReady,
    ModelReady,
    WorkflowReady,
    ChiefDefinitionReady,
    AgentPoolReady,
    ExecutionReady,
}

/// <summary>Bloqueador tipado: código estável e IDs relacionados. Nunca texto livre.</summary>
public sealed record ReadinessBlocker(string Code, IReadOnlyList<string> RelatedIds);

/// <summary>Próxima ação recomendada: código estável, rota e recurso opcional.</summary>
public sealed record ReadinessNextAction(string Code, string Route, string? ResourceId);

/// <summary>Prontidão de uma etapa. `MessageCode` é código localizável, não texto de domínio.</summary>
public sealed record ReadinessStepContract(
    ReadinessStep Step,
    ConfigurationState State,
    string ExecutionMode,
    string Capability,
    string MessageCode,
    IReadOnlyList<string> RelatedIds,
    IReadOnlyList<ReadinessBlocker> Blockers,
    ReadinessNextAction? NextAction);

/// <summary>Snapshot de prontidão do projeto (read model, somente leitura).</summary>
public sealed record ProjectReadinessSnapshot(
    string? ProjectId,
    ConfigurationState OverallState,
    IReadOnlyList<ReadinessStepContract> Steps,
    IReadOnlyList<ReadinessNextAction> NextActions);
