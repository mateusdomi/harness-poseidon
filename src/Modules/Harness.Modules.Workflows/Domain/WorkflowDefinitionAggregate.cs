using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Modules.Workflows.Contracts;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Results;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Workflows.Domain;

public sealed class WorkflowDefinitionAggregate
{
    private readonly IClock _clock;
    private readonly List<WorkflowDefinitionVersion> _versions = [];

    private WorkflowDefinitionAggregate(
        EntityId<WorkflowDefinitionTag> id,
        string tenantId,
        string name,
        IClock clock)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        _clock = clock;
        Versions = new ReadOnlyCollection<WorkflowDefinitionVersion>(_versions);
    }

    public EntityId<WorkflowDefinitionTag> Id { get; }

    public string TenantId { get; }

    public string Name { get; }

    public IReadOnlyList<WorkflowDefinitionVersion> Versions { get; }

    public static WorkflowDefinitionAggregate Create(
        string tenantId,
        string name,
        IClock clock)
    {
        WorkflowValidation.ValidateUlid(tenantId, nameof(tenantId));
        WorkflowValidation.ValidateText(name, nameof(name), 200);
        ArgumentNullException.ThrowIfNull(clock);
        return new WorkflowDefinitionAggregate(
            WorkflowIdFactory.New<WorkflowDefinitionTag>(clock),
            tenantId,
            name,
            clock);
    }

    public WorkflowDefinitionVersion CreateVersion(
        IReadOnlyList<WorkflowPhaseDefinitionInput> phases)
    {
        var immutablePhases = WorkflowValidation.CopyAndValidate(phases);
        var version = new WorkflowDefinitionVersion(
            WorkflowIdFactory.New<WorkflowDefinitionVersionTag>(_clock),
            _versions.Count + 1,
            immutablePhases,
            ComputeHash(immutablePhases),
            _clock.UtcNow);
        _versions.Add(version);
        return version;
    }

    public Result Publish(EntityId<WorkflowDefinitionVersionTag> versionId)
    {
        var version = _versions.SingleOrDefault(candidate => candidate.Id == versionId);
        if (version is null)
        {
            return Result.Failure(WorkflowErrors.VersionNotFound);
        }

        if (version.Status == WorkflowDefinitionVersionStatus.Published)
        {
            return Result.Success();
        }

        version.Publish(_clock.UtcNow);
        return Result.Success();
    }

    private static string ComputeHash(IReadOnlyList<WorkflowPhaseDefinition> phases) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(phases)));
}

public sealed class WorkflowDefinitionVersion
{
    internal WorkflowDefinitionVersion(
        EntityId<WorkflowDefinitionVersionTag> id,
        int version,
        IReadOnlyList<WorkflowPhaseDefinition> phases,
        string contentHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        Version = version;
        Phases = phases;
        ContentHash = contentHash;
        CreatedAt = createdAt;
        Status = WorkflowDefinitionVersionStatus.Draft;
    }

    public EntityId<WorkflowDefinitionVersionTag> Id { get; }

    public int Version { get; }

    public IReadOnlyList<WorkflowPhaseDefinition> Phases { get; }

    public string ContentHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public WorkflowDefinitionVersionStatus Status { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    internal void Publish(DateTimeOffset publishedAt)
    {
        Status = WorkflowDefinitionVersionStatus.Published;
        PublishedAt = publishedAt;
    }
}

public sealed record WorkflowPhaseDefinition(
    string Key,
    string Name,
    int Order,
    IReadOnlyList<ObjectiveItemDefinition> ObjectiveItems,
    IReadOnlyList<WorkflowGateDefinition> Gates);

public sealed record ObjectiveItemDefinition(
    string Key,
    string Name,
    ObjectiveItemKind Kind,
    decimal Weight);

public sealed record WorkflowGateDefinition(
    string Key,
    string Name,
    IReadOnlyList<string> RequiredObjectiveKeys,
    ObjectiveItemState MinimumRequiredState);

internal static class WorkflowValidation
{
    public static IReadOnlyList<WorkflowPhaseDefinition> CopyAndValidate(
        IReadOnlyList<WorkflowPhaseDefinitionInput> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Count == 0)
        {
            throw new ArgumentException("A workflow requires at least one phase.", nameof(phases));
        }

        var phaseKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WorkflowPhaseDefinition>(phases.Count);
        for (var index = 0; index < phases.Count; index++)
        {
            var phase = phases[index] ?? throw new ArgumentException(
                "Workflow phases cannot contain null entries.",
                nameof(phases));
            ValidateText(phase.Key, nameof(phases), 100);
            ValidateText(phase.Name, nameof(phases), 200);
            if (!phaseKeys.Add(phase.Key))
            {
                throw new ArgumentException("Phase keys must be unique.", nameof(phases));
            }

            result.Add(CopyPhase(phase, index + 1, nameof(phases)));
        }

        return new ReadOnlyCollection<WorkflowPhaseDefinition>(result);
    }

    public static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    public static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }
    }

    private static WorkflowPhaseDefinition CopyPhase(
        WorkflowPhaseDefinitionInput phase,
        int order,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(phase.ObjectiveItems);
        ArgumentNullException.ThrowIfNull(phase.Gates);
        if (phase.ObjectiveItems.Count == 0)
        {
            throw new ArgumentException("A phase requires objective items.", parameterName);
        }

        var objectiveKeys = new HashSet<string>(StringComparer.Ordinal);
        var objectives = new List<ObjectiveItemDefinition>(phase.ObjectiveItems.Count);
        foreach (var input in phase.ObjectiveItems)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateText(input.Key, parameterName, 100);
            ValidateText(input.Name, parameterName, 200);
            if (!Enum.IsDefined(input.Kind) || input.Weight <= 0m)
            {
                throw new ArgumentException("Objective kind and weight must be valid.", parameterName);
            }

            if (!objectiveKeys.Add(input.Key))
            {
                throw new ArgumentException("Objective keys must be unique within a phase.", parameterName);
            }

            objectives.Add(new ObjectiveItemDefinition(
                input.Key, input.Name, input.Kind, input.Weight));
        }

        var gateKeys = new HashSet<string>(StringComparer.Ordinal);
        var gates = new List<WorkflowGateDefinition>(phase.Gates.Count);
        foreach (var input in phase.Gates)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateText(input.Key, parameterName, 100);
            ValidateText(input.Name, parameterName, 200);
            ArgumentNullException.ThrowIfNull(input.RequiredObjectiveKeys);
            if (!gateKeys.Add(input.Key))
            {
                throw new ArgumentException("Gate keys must be unique within a phase.", parameterName);
            }

            var gateObjective = objectives.SingleOrDefault(objective => objective.Key == input.Key);
            if (gateObjective?.Kind != ObjectiveItemKind.Gate)
            {
                throw new ArgumentException("Each gate requires an objective item of kind Gate with the same key.", parameterName);
            }

            if (input.MinimumRequiredState == ObjectiveItemState.Pending ||
                !Enum.IsDefined(input.MinimumRequiredState))
            {
                throw new ArgumentException("A gate requires a non-pending minimum state.", parameterName);
            }

            var requirements = input.RequiredObjectiveKeys.ToArray();
            if (requirements.Length == 0 ||
                requirements.Distinct(StringComparer.Ordinal).Count() != requirements.Length ||
                requirements.Any(key => key == input.Key || !objectiveKeys.Contains(key)))
            {
                throw new ArgumentException("Gate requirements must be unique objective keys from the same phase.", parameterName);
            }

            gates.Add(new WorkflowGateDefinition(
                input.Key,
                input.Name,
                Array.AsReadOnly(requirements),
                input.MinimumRequiredState));
        }

        var unboundGateObjective = objectives.Any(objective =>
            objective.Kind == ObjectiveItemKind.Gate && !gateKeys.Contains(objective.Key));
        if (unboundGateObjective)
        {
            throw new ArgumentException("Every Gate objective must have a gate definition.", parameterName);
        }

        return new WorkflowPhaseDefinition(
            phase.Key,
            phase.Name,
            order,
            new ReadOnlyCollection<ObjectiveItemDefinition>(objectives),
            new ReadOnlyCollection<WorkflowGateDefinition>(gates));
    }
}
