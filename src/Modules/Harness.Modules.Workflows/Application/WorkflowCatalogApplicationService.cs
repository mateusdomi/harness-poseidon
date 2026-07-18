using Harness.Modules.Workflows.Contracts;
using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Workflows.Application;

public static class WorkflowCatalogApplicationService
{
    private static readonly HashSet<string> Modes =
        new(["manual", "semiautonomous", "autonomous"], StringComparer.Ordinal);

    public static WorkflowTemplateCreation CreateTemplate(
        string templateId, string versionId, CreateWorkflowTemplateRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Text(request.Name, 200); var description = Text(request.Description, 20_000);
        if (request.Phases is null || request.Phases.Count == 0)
            throw new ArgumentException("At least one phase is required.", nameof(request));
        var phaseNames = request.Phases.Select(x => Text(x, 200)).ToArray();
        if (phaseNames.Distinct(StringComparer.Ordinal).Count() != phaseNames.Length)
            throw new ArgumentException("Phase names must be unique.", nameof(request));
        if (request.GatesByPhase.Keys.Any(x => !phaseNames.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException("Gate phases must exist in the phase list.", nameof(request));

        var tick = 0L; string NextId() => UlidValue.New(now.AddTicks(++tick)).ToString();
        var phases = new List<WorkflowApiPhaseCreation>();
        for (var index = 0; index < phaseNames.Length; index++)
        {
            var phaseName = phaseNames[index]; var phaseId = NextId();
            var workId = NextId(); var objectives = new List<WorkflowApiObjectiveCreation>
            {
                new(workId, $"work-{index + 1}", phaseName, "task", 1m),
            };
            var gates = new List<WorkflowApiGateCreation>();
            var gateNames = request.GatesByPhase.TryGetValue(phaseName, out var configured)
                ? configured.Select(x => Text(x, 200)).ToArray() : [];
            if (gateNames.Distinct(StringComparer.Ordinal).Count() != gateNames.Length)
                throw new ArgumentException("Gate names must be unique inside a phase.", nameof(request));
            for (var gateIndex = 0; gateIndex < gateNames.Length; gateIndex++)
            {
                var objectiveId = NextId(); objectives.Add(new(objectiveId,
                    $"gate-objective-{gateIndex + 1}", gateNames[gateIndex], "gate", 1m));
                gates.Add(new(NextId(), objectiveId, $"gate-{gateIndex + 1}", gateNames[gateIndex],
                    "validated", [workId]));
            }
            phases.Add(new(phaseId, $"phase-{index + 1}", phaseName, index + 1, objectives, gates));
        }
        return new(Id(templateId), Id(versionId), name, description, phases,
            string.IsNullOrWhiteSpace(request.Changelog) ? null : Text(request.Changelog, 10_000), Utc(now));
    }

    public static WorkflowBindingInput CreateBinding(CreateWorkflowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = Choice(request.OperationMode, Modes);
        var gates = (request.SemiautonomousPauseGates ?? []).Select(x => Text(x, 200)).Distinct(StringComparer.Ordinal).ToArray();
        if (mode != "semiautonomous" && gates.Length > 0)
            throw new ArgumentException("Pause gates are only valid in semiautonomous mode.", nameof(request));
        return new(Id(request.ProjectId), Id(request.TemplateId),
            request.VersionId is null ? null : Id(request.VersionId), mode, gates,
            Text(request.RiskAcceptanceNote, 10_000));
    }

    public static string WorkflowId(CreateWorkflowRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); return Id(request.WorkflowId);
    }

    private static string Id(string value) => UlidValue.TryParse(value, out var id)
        ? id.ToString() : throw new ArgumentException("Value must be a canonical ULID.", nameof(value));
    private static string Text(string value, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); var text = value.Trim();
        return text.Length <= max ? text : throw new ArgumentException($"Value exceeds {max} characters.", nameof(value));
    }
    private static string Choice(string value, HashSet<string> choices)
    { var text = Text(value, 100); return choices.Contains(text) ? text : throw new ArgumentException("Value is not supported.", nameof(value)); }
    private static DateTimeOffset Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero
        ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed record WorkflowTemplateCreation(
    string TemplateId, string VersionId, string Name, string Description,
    IReadOnlyList<WorkflowApiPhaseCreation> Phases, string? Changelog, DateTimeOffset OccurredAt);

public sealed record WorkflowApiPhaseCreation(
    string Id, string Key, string Name, int Order, IReadOnlyList<WorkflowApiObjectiveCreation> Objectives,
    IReadOnlyList<WorkflowApiGateCreation> Gates);

public sealed record WorkflowApiObjectiveCreation(
    string Id, string Key, string Name, string Kind, decimal Weight);

public sealed record WorkflowApiGateCreation(
    string Id, string ObjectiveId, string Key, string Name, string MinimumRequiredState,
    IReadOnlyList<string> RequiredObjectiveIds);

public sealed record WorkflowBindingInput(
    string ProjectId, string TemplateId, string? VersionId, string OperationMode,
    IReadOnlyList<string> PauseGates, string RiskAcceptanceNote);
