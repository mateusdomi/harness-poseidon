namespace Harness.Persistence.Abstractions.RunTargets;

public interface IRunTargetStore
{
    Task<IReadOnlyList<RunTargetRecord>> SynchronizeAsync(
        RunTargetSynchronizationCommand command,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunTargetRecord>> ListAsync(
        string tenantId,
        string? projectId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default);
    Task<RunTargetRecord?> GetAsync(
        string tenantId,
        string id,
        CancellationToken cancellationToken = default);
    Task<RunTargetLaunchRecord?> GetLaunchAsync(
        string tenantId,
        string id,
        CancellationToken cancellationToken = default);
    Task<RunTargetRecord> SetStateAsync(
        RunTargetStateCommand command,
        CancellationToken cancellationToken = default);
    Task AppendLogAsync(
        RunTargetLogCommand command,
        CancellationToken cancellationToken = default);
    Task<int> CleanupAsync(
        RunTargetCleanupCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record RunTargetRecord(
    string Id,
    string ProjectId,
    string Name,
    string Kind,
    string? Url,
    int? Port,
    string State,
    DateTimeOffset DetectedAt,
    DateTimeOffset? LastCheckAt);

public sealed record RunTargetLaunchRecord(
    RunTargetRecord Target,
    string WorkingDirectory,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);

public sealed record RunTargetDefinition(
    string Fingerprint,
    string Name,
    string Kind,
    string? Url,
    int? Port,
    string WorkingDirectory,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);

public sealed record RunTargetSynchronizationCommand(
    string TenantId,
    string ActorProfileId,
    string ProjectId,
    IReadOnlyList<RunTargetDefinition> Definitions,
    DateTimeOffset OccurredAt);

public sealed record RunTargetStateCommand(
    string TenantId,
    string ActorProfileId,
    string Id,
    string State,
    string LogLine,
    DateTimeOffset OccurredAt);

public sealed record RunTargetLogCommand(
    string TenantId,
    string ProjectId,
    string Line,
    DateTimeOffset OccurredAt);

public sealed record RunTargetCleanupCommand(
    string TenantId,
    string ActorProfileId,
    string ProjectId,
    DateTimeOffset OccurredAt);

public sealed class RunTargetValidationException(string detail) : Exception(detail);
public sealed class RunTargetNotFoundException(string resource) : Exception(resource)
{
    public string Resource { get; } = resource;
}
