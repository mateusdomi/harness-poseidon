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
    DateTimeOffset OccurredAt);

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
