using System.Text.Json.Serialization;

namespace Harness.Modules.Workflows.Contracts;

public sealed record WorkflowTemplateContract(
    string Id, string Name, string Description, string? CurrentVersionId, DateTimeOffset CreatedAt);

public sealed record WorkflowPhaseConfigContract(
    IReadOnlyList<string> DocumentKinds, decimal ProgressWeight,
    IReadOnlyList<string> AllowedAgentDefinitionIds);

public sealed record WorkflowVersionContract(
    string Id, string TemplateId, int Version, IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase,
    IReadOnlyDictionary<string, WorkflowPhaseConfigContract> PhaseConfigs,
    string? DefaultOperationMode, IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions,
    string? Changelog, DateTimeOffset PublishedAt);

public sealed record WorkflowRiskAcceptanceContract(
    string Mode, string AcceptedByProfileId, string Note, DateTimeOffset AcceptedAt);

public sealed record WorkflowContract(
    string Id, string ProjectId, string TemplateId, string ActiveVersionId, string OperationMode,
    IReadOnlyList<string> SemiautonomousPauseGates,
    IReadOnlyList<WorkflowRiskAcceptanceContract> RiskAcceptances, DateTimeOffset CreatedAt);

public sealed record WorkflowRunContract(
    string Id, string WorkflowId, string VersionId, string State, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record PhaseContract(
    string Id, string RunId, string Name, int Order, string State, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record GateContract(
    string Id, string PhaseId, string RunId, string Name, string State, bool RequiresApproval,
    string? DecidedByProfileId, DateTimeOffset? DecidedAt, string? Note);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowTemplateRequest(
    string Name, string Description, IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase, string? Changelog = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowRequest(
    string ProjectId, string TemplateId, string? VersionId, string OperationMode,
    IReadOnlyList<string>? SemiautonomousPauseGates, string RiskAcceptanceNote);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowRunRequest(string WorkflowId);
