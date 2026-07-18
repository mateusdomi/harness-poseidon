using Harness.Persistence.Abstractions.Projects;

namespace Harness.Persistence.Abstractions.Agents;

public interface IChiefOrchestratorStore
{
    Task<ProjectRecord> PauseAsync(
        ChiefProjectCommand command,
        CancellationToken cancellationToken = default);

    Task<ProjectRecord> ResumeAsync(
        ChiefProjectCommand command,
        CancellationToken cancellationToken = default);

    Task<AgentRecord> HandoffAsync(
        ChiefHandoffCommand command,
        CancellationToken cancellationToken = default);

    Task<int> DrainAsync(
        ChiefDrainCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record ChiefProjectCommand(
    string TenantId,
    string ProjectId,
    string ActorProfileId,
    DateTimeOffset OccurredAt);

public sealed record ChiefHandoffCommand(
    string TenantId,
    string ProjectId,
    string NewAgentId,
    string ActorProfileId,
    string? TargetDefinitionId,
    string? TargetModelId,
    string Note,
    DateTimeOffset OccurredAt);

public sealed record ChiefDrainCommand(
    string TenantId,
    string ProjectId,
    string ActorProfileId,
    string? Note,
    DateTimeOffset OccurredAt);

public sealed class ChiefResourceNotFoundException(string resource) : Exception(resource)
{
    public string Resource { get; } = resource;
}

public sealed class ChiefStateConflictException(string detail) : Exception(detail);
