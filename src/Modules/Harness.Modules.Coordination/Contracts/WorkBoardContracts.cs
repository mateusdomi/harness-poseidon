using System.Text.Json.Serialization;

namespace Harness.Modules.Coordination.Contracts;

public sealed record SolicitationContract(
    string Id, string ProjectId, string AuthorProfileId, string Kind, string Title, string Body,
    string State, string? SupersedesId, DateTimeOffset CreatedAt);

public sealed record DemandContract(
    string Id, string ProjectId, string? SolicitationId, string Title, string Description,
    string State, string Priority, DateTimeOffset CreatedAt, string? PhaseName = null);

public sealed record WorkProgressContract(decimal Executed, decimal Validated, decimal Approved);

public sealed record BoardTaskContract(
    string Id, string ProjectId, string? DemandId, string Title, string State, string Priority,
    string? AssigneeAgentId, string? BlockedReason, int InstructionVersion,
    WorkProgressContract Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? DueAt, DateTimeOffset? ArchivedAt, string? PhaseName = null,
    string CardType = "agent_task");

public sealed record TaskInstructionContract(
    string Id, string TaskId, int Version, string Body, string AuthorKind, string? AuthorId,
    DateTimeOffset CreatedAt);

public sealed record AttemptContract(
    string Id, string TaskId, int Number, string State, string AgentId,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, decimal CostUsd,
    long TokensInput, long TokensOutput, IReadOnlyList<string> CommitRefs, string? Summary,
    string? FailureReason);

public sealed record AttemptEventContract(
    string Id, string AttemptId, string Kind, string Content, DateTimeOffset OccurredAt,
    string Severity = "info");

public sealed record SolicitationAnalysisItemContract(string Id, string Text);

public sealed record SolicitationAnalysisContract(
    string SolicitationId,
    IReadOnlyList<SolicitationAnalysisItemContract> Requirements,
    IReadOnlyList<SolicitationAnalysisItemContract> Ambiguities,
    IReadOnlyList<SolicitationAnalysisItemContract> Contradictions,
    IReadOnlyList<SolicitationAnalysisItemContract> Questions,
    IReadOnlyList<SolicitationAnalysisItemContract> AcceptanceCriteria);

public sealed record SolicitationAnalysisDraft(
    SolicitationContract Solicitation,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> Ambiguities,
    IReadOnlyList<string> Contradictions,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> AcceptanceCriteria);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSolicitationRequest(
    string ProjectId, string Kind, string Title, string Body, string? SupersedesId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDemandRequest(
    string ProjectId, string Title, string Description, string? SolicitationId = null,
    string? Priority = null, string? PhaseName = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskRequest(
    string ProjectId, string Title, string Instruction, string? DemandId = null,
    string? Priority = null, string? AssigneeAgentId = null, DateTimeOffset? DueAt = null,
    string? PhaseName = null, string? CardType = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MoveTaskRequest(string ToState, string? Note = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetTaskPriorityRequest(string Priority);

// Operação em lote do quadro: aplica uma única ação a muitas tasks numa requisição.
// 'operation' escolhe a ação; 'toState'/'note' alimentam 'move'; 'priority' alimenta 'priority'.
// A resposta traz um resultado POR item (nunca aborta o lote inteiro por causa de um id).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BatchTaskOperationRequest(
    string Operation, IReadOnlyList<string> TaskIds, string? ToState = null,
    string? Note = null, string? Priority = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppendTaskInstructionRequest(string Body);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskInstructionRequest(string TaskId, string Body);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionSolicitationRequest(string State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalyzeSolicitationRequest(
    string ProjectId, string Text, IReadOnlyList<string>? AttachmentNames = null);
