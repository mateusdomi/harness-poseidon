namespace Harness.Persistence.Abstractions.Workflows;

public interface IWorkflowCatalogStore
{
    Task<IReadOnlyList<WorkflowTemplateCatalogRecord>> ListTemplatesAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<WorkflowTemplateCatalogRecord?> GetTemplateAsync(
        string tenantId, string templateId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? templateId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<WorkflowVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default);
    Task<WorkflowBindingCatalogRecord> CreateBindingAsync(
        WorkflowBindingCreateCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowBindingCatalogRecord>> ListBindingsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<WorkflowBindingCatalogRecord?> GetBindingAsync(
        string tenantId, string workflowId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowRunCatalogRecord>> ListRunsAsync(
        string tenantId, string? workflowId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<WorkflowRunCatalogRecord?> GetRunAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowPhaseCatalogRecord>> ListPhasesAsync(
        string tenantId, string? runId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<WorkflowPhaseCatalogRecord?> GetPhaseAsync(
        string tenantId, string phaseId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowGateCatalogRecord>> ListGatesAsync(
        string tenantId, string? runId, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task<WorkflowGateCatalogRecord?> GetGateAsync(
        string tenantId, string gateId, CancellationToken cancellationToken = default);
}

public sealed record WorkflowTemplateCatalogRecord(
    string TenantId, string Id, string Name, string Description, string? CurrentVersionId,
    DateTimeOffset CreatedAt);

public sealed record WorkflowVersionCatalogRecord(
    string TenantId, string Id, string TemplateId, int Version, IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase, string? Changelog,
    DateTimeOffset PublishedAt);

public sealed record WorkflowRiskAcceptanceCatalogRecord(
    string Mode, string AcceptedByProfileId, string Note, DateTimeOffset AcceptedAt);

public sealed record WorkflowBindingCatalogRecord(
    string TenantId, string Id, string ProjectId, string TemplateId, string ActiveVersionId,
    string OperationMode, IReadOnlyList<string> SemiautonomousPauseGates,
    IReadOnlyList<WorkflowRiskAcceptanceCatalogRecord> RiskAcceptances, DateTimeOffset CreatedAt);

public sealed record WorkflowRunCatalogRecord(
    string TenantId, string Id, string WorkflowId, string VersionId, string State,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long Version);

public sealed record WorkflowPhaseCatalogRecord(
    string TenantId, string Id, string RunId, string Name, int Order, string State,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record WorkflowGateCatalogRecord(
    string TenantId, string Id, string PhaseId, string RunId, string Name, string State,
    bool RequiresApproval, string? DecidedByProfileId, DateTimeOffset? DecidedAt, string? Note);

public sealed record WorkflowBindingCreateCommand(
    string TenantId, string Id, string ProjectId, string TemplateId, string ActiveVersionId,
    string OperationMode, IReadOnlyList<string> SemiautonomousPauseGates,
    string AcceptedByProfileId, string RiskAcceptanceId, string RiskAcceptanceNote,
    DateTimeOffset OccurredAt);

public sealed class WorkflowCatalogReferenceNotFoundException(string reference) : Exception(reference)
{
    public string Reference { get; } = reference;
}

public sealed class WorkflowBindingAlreadyExistsException() : Exception("workflow");
