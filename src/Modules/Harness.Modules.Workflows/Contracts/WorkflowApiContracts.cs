using System.Text.Json.Serialization;

namespace Harness.Modules.Workflows.Contracts;

public sealed record WorkflowTemplateContract(
    string Id, string Name, string Description, string? CurrentVersionId, string State,
    DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt);

public sealed record WorkflowPhaseConfigContract(
    IReadOnlyList<string> DocumentKinds, decimal ProgressWeight,
    IReadOnlyList<string> AllowedAgentDefinitionIds,
    string? Objective = null,
    string? Context = null,
    IReadOnlyList<string>? AcceptanceCriteria = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? EntryConditions = null,
    IReadOnlyList<string>? ExitConditions = null,
    IReadOnlyList<string>? AllowedSkillIds = null,
    IReadOnlyList<string>? AllowedToolIds = null);

public sealed record WorkflowVersionContract(
    string Id, string TemplateId, int Version, IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase,
    IReadOnlyDictionary<string, WorkflowPhaseConfigContract> PhaseConfigs,
    string? DefaultOperationMode, IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions,
    string? Changelog, string State, DateTimeOffset? PublishedAt, DateTimeOffset? ArchivedAt);

public sealed record WorkflowRiskAcceptanceContract(
    string Mode, string AcceptedByProfileId, string Note, DateTimeOffset AcceptedAt);

public sealed record WorkflowContract(
    string Id, string ProjectId, string TemplateId, string ActiveVersionId, string OperationMode,
    IReadOnlyList<string> SemiautonomousPauseGates,
    IReadOnlyList<WorkflowRiskAcceptanceContract> RiskAcceptances, DateTimeOffset CreatedAt);

public sealed record WorkflowRunContract(
    string Id, string WorkflowId, string VersionId, string State, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record PhaseProgressBreakdownContract(int Completed, int Total);

public sealed record PhaseProgressContract(
    int Completed, int Total, decimal Percent, string Source, DateTimeOffset? UpdatedAt,
    PhaseProgressBreakdownContract Tasks, PhaseProgressBreakdownContract Documents,
    PhaseProgressBreakdownContract Gates);

public sealed record PhaseDeliverableContract(string Name, string Status);

public sealed record PhaseContract(
    string Id, string RunId, string Name, int Order, string State, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, PhaseProgressContract Progress,
    IReadOnlyList<PhaseDeliverableContract> Deliverables);

public sealed record GateContract(
    string Id, string PhaseId, string RunId, string Name, string State, bool RequiresApproval,
    string? DecidedByProfileId, DateTimeOffset? DecidedAt, string? Note);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowTemplateRequest(
    string Name, string? Description = null, IReadOnlyList<string>? Phases = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GatesByPhase = null,
    string? Changelog = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowRequest(
    string ProjectId, string TemplateId, string? VersionId, string OperationMode,
    IReadOnlyList<string>? SemiautonomousPauseGates, string RiskAcceptanceNote);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LinkWorkflowTemplateRequest(string TemplateId, string? VersionId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkflowRunRequest(string WorkflowId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishWorkflowVersionRequest(
    IReadOnlyList<string> Phases, IReadOnlyDictionary<string, IReadOnlyList<string>> GatesByPhase,
    IReadOnlyDictionary<string, WorkflowPhaseConfigContract>? PhaseConfigs = null,
    string? DefaultOperationMode = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Transitions = null,
    string? Changelog = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowDraftRequest(
    IReadOnlyList<string>? Phases = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GatesByPhase = null,
    IReadOnlyDictionary<string, WorkflowPhaseConfigContract>? PhaseConfigs = null,
    string? DefaultOperationMode = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Transitions = null,
    string? Changelog = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishWorkflowDraftRequest(string? Changelog = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetWorkflowOperationModeRequest(
    string Mode, IReadOnlyList<string>? SemiautonomousPauseGates, string RiskAcceptanceNote);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionWorkflowRunRequest(string Transition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvanceWorkflowObjectiveRequest(
    string PhaseKey, string ObjectiveKey, string TargetState);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EvaluateWorkflowGateRequest(
    string PhaseKey, string GateKey, bool Passed, string? Note = null);
