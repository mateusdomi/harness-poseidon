using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Workflows;

public interface IWorkflowStore
{
    Task<WorkflowDefinitionReceipt> CreatePublishedDefinitionAsync(
        WorkflowDefinitionCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowDefinitionStoreSnapshot?> ReadDefinitionAsync(
        string tenantId,
        string definitionId,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunCreateReceipt> CreateRunAsync(
        WorkflowRunCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunStoreSnapshot?> ReadRunAsync(
        string tenantId,
        string runId,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunMutationReceipt> TransitionRunAsync(
        WorkflowRunTransitionCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunMutationReceipt> AdvanceObjectiveAsync(
        WorkflowObjectiveAdvanceCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunMutationReceipt> EvaluateGateAsync(
        WorkflowGateEvaluateCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunMutationReceipt> CompletePhaseAsync(
        WorkflowPhaseCompleteCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunAggregateSnapshot?> ReadRunAggregateAsync(
        string tenantId,
        string runId,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowDefinitionCreateCommand(
    string TenantId,
    string DefinitionId,
    string Name,
    string DefinitionVersionId,
    int Version,
    string ContentHash,
    IReadOnlyList<WorkflowPhaseCreateInput> Phases,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string Description = "");

public sealed record WorkflowPhaseCreateInput(
    string PhaseDefinitionId,
    string Key,
    string Name,
    int Order,
    IReadOnlyList<WorkflowObjectiveCreateInput> Objectives,
    IReadOnlyList<WorkflowGateCreateInput> Gates);

public sealed record WorkflowObjectiveCreateInput(
    string ObjectiveDefinitionId,
    string Key,
    string Name,
    string Kind,
    decimal Weight);

public sealed record WorkflowGateCreateInput(
    string GateDefinitionId,
    string ObjectiveDefinitionId,
    string Key,
    string Name,
    string MinimumRequiredState,
    IReadOnlyList<string> RequiredObjectiveDefinitionIds);

public sealed record WorkflowDefinitionReceipt(
    string DefinitionId,
    string DefinitionVersionId,
    int Version,
    long LedgerSequence,
    string LedgerHash,
    string OutboxMessageId,
    bool Replay);

public sealed record WorkflowDefinitionStoreSnapshot(
    string TenantId,
    string DefinitionId,
    string Name,
    string DefinitionVersionId,
    int Version,
    string Status,
    string ContentHash,
    int PhaseCount,
    int ObjectiveCount,
    int GateCount,
    int RequirementCount);

public sealed record WorkflowRunCreateCommand(
    string TenantId,
    string ProjectId,
    string DefinitionVersionId,
    string RunId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string? WorkflowId = null);

public sealed record WorkflowRunCreateReceipt(
    string RunId,
    long RunVersion,
    long LedgerSequence,
    string LedgerHash,
    string OutboxMessageId,
    bool Replay);

public sealed record WorkflowRunStoreSnapshot(
    string TenantId,
    string ProjectId,
    string DefinitionVersionId,
    string RunId,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int PhaseCount,
    int ActivePhaseCount,
    int ObjectiveCount,
    int GateCount,
    decimal Executed,
    decimal Validated,
    decimal Approved);

public enum WorkflowRunTransition
{
    Start,
    Pause,
    Resume,
    Cancel,
}

public enum WorkflowRunMutationStatus
{
    Applied,
    IdempotentReplay,
    NotFound,
    VersionConflict,
    InvalidState,
    PhaseNotActive,
    ObjectiveNotFound,
    ObjectiveTransitionInvalid,
    GateEvaluationRequired,
    GateNotFound,
    GateRequirementsNotMet,
    PhaseCompletionBlocked,
}

public sealed record WorkflowRunTransitionCommand(
    string TenantId,
    string RunId,
    WorkflowRunTransition Transition,
    long ExpectedRunVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkflowRunMutationReceipt(
    WorkflowRunMutationStatus Status,
    string RunId,
    long? RunVersion,
    string? RunState,
    long? LedgerSequence = null,
    string? LedgerHash = null,
    string? OutboxMessageId = null);

public sealed record WorkflowObjectiveAdvanceCommand(
    string TenantId,
    string RunId,
    string PhaseKey,
    string ObjectiveKey,
    string TargetState,
    long ExpectedRunVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkflowGateEvaluateCommand(
    string TenantId,
    string RunId,
    string PhaseKey,
    string GateKey,
    bool Passed,
    long ExpectedRunVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkflowPhaseCompleteCommand(
    string TenantId,
    string RunId,
    string PhaseKey,
    long ExpectedRunVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkflowRunAggregateSnapshot(
    string TenantId,
    string ProjectId,
    string DefinitionVersionId,
    string RunId,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    decimal Executed,
    decimal Validated,
    decimal Approved,
    IReadOnlyList<WorkflowPhaseRunSnapshot> Phases);

public sealed record WorkflowPhaseRunSnapshot(
    string PhaseRunId,
    string PhaseDefinitionId,
    string Key,
    string Name,
    int Order,
    string State,
    long Version,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkflowObjectiveRunSnapshot> Objectives,
    IReadOnlyList<WorkflowGateRunSnapshot> Gates);

public sealed record WorkflowObjectiveRunSnapshot(
    string ObjectiveRunId,
    string ObjectiveDefinitionId,
    string Key,
    string Name,
    string Kind,
    decimal Weight,
    string State,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record WorkflowGateRunSnapshot(
    string GateRunId,
    string GateDefinitionId,
    string Key,
    string Name,
    string MinimumRequiredState,
    string State,
    long Version,
    DateTimeOffset? EvaluatedAt,
    IReadOnlyList<string> RequiredObjectiveKeys);

public static class WorkflowDefinitionCreateValidator
{
    private static readonly HashSet<string> Kinds =
        new(StringComparer.Ordinal) { "document", "task", "test", "gate", "approval", "evidence" };
    private static readonly HashSet<string> RequiredStates =
        new(StringComparer.Ordinal) { "executed", "validated", "approved" };

    public static void Validate(WorkflowDefinitionCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.TenantId, nameof(command));
        ValidateId(command.DefinitionId, nameof(command));
        ValidateId(command.DefinitionVersionId, nameof(command));
        ValidateText(command.Name, 200, nameof(command));
        if (command.Description.Length > 20_000)
        {
            throw new ArgumentException("Description exceeds 20000 characters.", nameof(command));
        }
        ValidateText(command.IdempotencyKey, 200, nameof(command));
        if (command.Version != 1 || !string.Equals(
            command.ContentHash,
            WorkflowDefinitionContentHash.Compute(command.Phases),
            StringComparison.Ordinal))
        {
            throw new ArgumentException("Initial definition version and hash are invalid.", nameof(command));
        }

        ArgumentNullException.ThrowIfNull(command.Phases);
        if (command.Phases.Count == 0)
        {
            throw new ArgumentException("At least one phase is required.", nameof(command));
        }

        var allIds = new HashSet<string>(StringComparer.Ordinal)
        {
            command.DefinitionId,
            command.DefinitionVersionId,
        };
        var phaseKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < command.Phases.Count; index++)
        {
            ValidatePhase(command.Phases[index], index + 1, allIds, phaseKeys, nameof(command));
        }
    }

    private static void ValidatePhase(
        WorkflowPhaseCreateInput phase,
        int expectedOrder,
        HashSet<string> allIds,
        HashSet<string> phaseKeys,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(phase);
        ValidateUniqueId(phase.PhaseDefinitionId, allIds, parameterName);
        ValidateText(phase.Key, 100, parameterName);
        ValidateText(phase.Name, 200, parameterName);
        if (phase.Order != expectedOrder || !phaseKeys.Add(phase.Key))
        {
            throw new ArgumentException("Phase order and keys must be sequential and unique.", parameterName);
        }

        ArgumentNullException.ThrowIfNull(phase.Objectives);
        ArgumentNullException.ThrowIfNull(phase.Gates);
        if (phase.Objectives.Count == 0)
        {
            throw new ArgumentException("Each phase requires objectives.", parameterName);
        }

        var objectiveIds = new HashSet<string>(StringComparer.Ordinal);
        var objectiveKeys = new HashSet<string>(StringComparer.Ordinal);
        var gateObjectiveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var objective in phase.Objectives)
        {
            ArgumentNullException.ThrowIfNull(objective);
            ValidateUniqueId(objective.ObjectiveDefinitionId, allIds, parameterName);
            objectiveIds.Add(objective.ObjectiveDefinitionId);
            ValidateText(objective.Key, 100, parameterName);
            ValidateText(objective.Name, 200, parameterName);
            if (!objectiveKeys.Add(objective.Key) || !Kinds.Contains(objective.Kind) || objective.Weight <= 0m)
            {
                throw new ArgumentException("Objective key, kind, and weight must be valid.", parameterName);
            }

            if (objective.Kind == "gate")
            {
                gateObjectiveIds.Add(objective.ObjectiveDefinitionId);
            }
        }

        var boundGateObjectives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in phase.Gates)
        {
            ArgumentNullException.ThrowIfNull(gate);
            ValidateUniqueId(gate.GateDefinitionId, allIds, parameterName);
            ValidateText(gate.Key, 100, parameterName);
            ValidateText(gate.Name, 200, parameterName);
            ArgumentNullException.ThrowIfNull(gate.RequiredObjectiveDefinitionIds);
            if (!gateObjectiveIds.Contains(gate.ObjectiveDefinitionId) ||
                !boundGateObjectives.Add(gate.ObjectiveDefinitionId) ||
                !RequiredStates.Contains(gate.MinimumRequiredState) ||
                gate.RequiredObjectiveDefinitionIds.Count == 0 ||
                gate.RequiredObjectiveDefinitionIds.Distinct(StringComparer.Ordinal).Count() !=
                    gate.RequiredObjectiveDefinitionIds.Count ||
                gate.RequiredObjectiveDefinitionIds.Any(id =>
                    id == gate.ObjectiveDefinitionId || !objectiveIds.Contains(id)))
            {
                throw new ArgumentException("Gate binding and requirements must be valid.", parameterName);
            }
        }

        if (!gateObjectiveIds.SetEquals(boundGateObjectives))
        {
            throw new ArgumentException("Every gate objective must be bound exactly once.", parameterName);
        }
    }

    private static void ValidateUniqueId(string value, HashSet<string> identifiers, string parameterName)
    {
        ValidateId(value, parameterName);
        if (!identifiers.Add(value))
        {
            throw new ArgumentException("Definition identifiers must be unique.", parameterName);
        }
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private static void ValidateText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }
    }
}

public static class WorkflowDefinitionCreateHash
{
    public static string Compute(WorkflowDefinitionCreateCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
}

public static class WorkflowDefinitionContentHash
{
    public static string Compute(IReadOnlyList<WorkflowPhaseCreateInput> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(phases)));
    }
}

public static class WorkflowRunCreateValidator
{
    public static void Validate(WorkflowRunCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.TenantId, nameof(command));
        ValidateId(command.ProjectId, nameof(command));
        ValidateId(command.DefinitionVersionId, nameof(command));
        ValidateId(command.RunId, nameof(command));
        if (command.WorkflowId is not null)
        {
            ValidateId(command.WorkflowId, nameof(command));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey, nameof(command));
        if (command.IdempotencyKey.Length > 200)
        {
            throw new ArgumentException("Idempotency key exceeds 200 characters.", nameof(command));
        }
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }
}

public static class WorkflowRunCreateHash
{
    public static string Compute(WorkflowRunCreateCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
}

public static class WorkflowRunMutationValidator
{
    public static void Validate(WorkflowRunTransitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.TenantId, nameof(command.TenantId));
        ValidateId(command.RunId, nameof(command.RunId));
        if (!Enum.IsDefined(command.Transition))
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Transition is invalid.");
        }

        if (command.ExpectedRunVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Expected version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey, nameof(command));
        if (command.IdempotencyKey.Length > 200)
        {
            throw new ArgumentException("Idempotency key exceeds 200 characters.", nameof(command));
        }
    }

    public static string Hash(WorkflowRunTransitionCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));

    public static void Validate(WorkflowObjectiveAdvanceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.RunId,
            command.ExpectedRunVersion,
            command.IdempotencyKey,
            command);
        ValidateText(command.PhaseKey, nameof(command.PhaseKey));
        ValidateText(command.ObjectiveKey, nameof(command.ObjectiveKey));
        if (command.TargetState is not ("executed" or "validated" or "approved"))
        {
            throw new ArgumentException("Target state is invalid.", nameof(command));
        }
    }

    public static void Validate(WorkflowGateEvaluateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.RunId,
            command.ExpectedRunVersion,
            command.IdempotencyKey,
            command);
        ValidateText(command.PhaseKey, nameof(command.PhaseKey));
        ValidateText(command.GateKey, nameof(command.GateKey));
    }

    public static void Validate(WorkflowPhaseCompleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.RunId,
            command.ExpectedRunVersion,
            command.IdempotencyKey,
            command);
        ValidateText(command.PhaseKey, nameof(command.PhaseKey));
    }

    public static string Hash(WorkflowObjectiveAdvanceCommand command) => HashCore(command);

    public static string Hash(WorkflowGateEvaluateCommand command) => HashCore(command);

    public static string Hash(WorkflowPhaseCompleteCommand command) => HashCore(command);

    private static string HashCore<T>(T command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));

    private static void ValidateCommon(
        string tenantId,
        string runId,
        long expectedVersion,
        string idempotencyKey,
        object command)
    {
        ValidateId(tenantId, nameof(command));
        ValidateId(runId, nameof(command));
        if (expectedVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Expected version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey, nameof(command));
        if (idempotencyKey.Length > 200)
        {
            throw new ArgumentException("Idempotency key exceeds 200 characters.", nameof(command));
        }
    }

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 100)
        {
            throw new ArgumentException("Value exceeds 100 characters.", parameterName);
        }
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }
}
