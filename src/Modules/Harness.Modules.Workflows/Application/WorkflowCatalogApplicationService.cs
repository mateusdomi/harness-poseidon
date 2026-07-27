using Harness.Modules.Workflows.Contracts;
using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Workflows.Application;

public static class WorkflowCatalogApplicationService
{
    private static readonly HashSet<string> Modes =
        new(["manual", "semiautonomous", "autonomous"], StringComparer.Ordinal);
    private static readonly HashSet<string> TerminalTransitionTargets =
        new(["Arquivada", "Roteada-para-Sustentação"], StringComparer.Ordinal);

    public static WorkflowDraftTemplateCreation CreateDraftTemplate(
        string templateId, CreateWorkflowTemplateRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Text(request.Name, 200);
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? string.Empty
            : Text(request.Description, 20_000);
        return new(Id(templateId), name, description, Utc(now));
    }

    public static WorkflowTemplateCreation CreateTemplate(
        string templateId, string versionId, CreateWorkflowTemplateRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Text(request.Name, 200); var description = Text(request.Description ?? string.Empty, 20_000);
        if (request.Phases is null || request.Phases.Count == 0)
            throw new ArgumentException("At least one phase is required.", nameof(request));
        var phaseNames = request.Phases.Select(x => Text(x, 200)).ToArray();
        if (phaseNames.Distinct(StringComparer.Ordinal).Count() != phaseNames.Length)
            throw new ArgumentException("Phase names must be unique.", nameof(request));
        var gatesByPhase = request.GatesByPhase ?? new Dictionary<string, IReadOnlyList<string>>();
        if (gatesByPhase.Keys.Any(x => !phaseNames.Contains(x, StringComparer.Ordinal)))
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
            var gateNames = gatesByPhase.TryGetValue(phaseName, out var configured)
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

    public static WorkflowVersionCreation CreateVersion(
        string templateId, string versionId, PublishWorkflowVersionRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hierarchy = CreateTemplate(templateId, versionId,
            new CreateWorkflowTemplateRequest("Published version", "Published version",
                request.Phases, request.GatesByPhase, request.Changelog), now);
        var phaseSet = request.Phases.ToHashSet(StringComparer.Ordinal);
        var configs = request.PhaseConfigs ?? new Dictionary<string, WorkflowPhaseConfigContract>();
        if (configs.Keys.Any(key => !phaseSet.Contains(key)) ||
            configs.Values.Any(value => value.DocumentKinds is null ||
                value.AllowedAgentDefinitionIds is null ||
                value.ProgressWeight is < 0m or > 100m ||
                value.AllowedAgentDefinitionIds.Any(id => !UlidValue.TryParse(id, out _)) ||
                (value.AllowedSkillIds ?? []).Any(id => !UlidValue.TryParse(id, out _)) ||
                (value.AllowedToolIds ?? []).Any(id => !UlidValue.TryParse(id, out _)) ||
                (value.DependsOn ?? []).Any(phase => !phaseSet.Contains(phase))))
            throw new ArgumentException("Phase configuration is invalid.", nameof(request));
        foreach (var (phase, config) in configs)
        {
            var dependencies = config.DependsOn ?? [];
            if (dependencies.Contains(phase, StringComparer.Ordinal) || dependencies.Any(dependency =>
                    configs.TryGetValue(dependency, out var other) &&
                    (other.DependsOn ?? []).Contains(phase, StringComparer.Ordinal)))
                throw new ArgumentException("Workflow phase dependencies contain a cycle.", nameof(request));
        }
        var transitions = request.Transitions ?? new Dictionary<string, IReadOnlyList<string>>();
        if (transitions.Any(rule => !phaseSet.Contains(rule.Key) ||
            rule.Value is null || rule.Value.Any(next =>
                !phaseSet.Contains(next) &&
                !(rule.Key == "1-Triagem" && TerminalTransitionTargets.Contains(next)))))
            throw new ArgumentException("Workflow transitions reference an unknown phase.", nameof(request));
        var mode = request.DefaultOperationMode is null ? null : Choice(request.DefaultOperationMode, Modes);
        return new(hierarchy, configs, mode, transitions);
    }

    public static WorkflowVersionCreation CreateDraftVersion(
        string templateId, string versionId, WorkflowDraftRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var phases = request.Phases ?? [];
        WorkflowTemplateCreation hierarchy;
        if (phases.Count == 0)
        {
            hierarchy = new(Id(templateId), Id(versionId), "Draft", string.Empty, [],
                string.IsNullOrWhiteSpace(request.Changelog) ? null : Text(request.Changelog, 10_000),
                Utc(now));
        }
        else
        {
            hierarchy = CreateTemplate(templateId, versionId,
                new CreateWorkflowTemplateRequest("Draft", "Draft", phases,
                    request.GatesByPhase ?? new Dictionary<string, IReadOnlyList<string>>(),
                    request.Changelog), now);
        }

        var mode = request.DefaultOperationMode is null
            ? null
            : Choice(request.DefaultOperationMode, Modes);
        return new(hierarchy,
            request.PhaseConfigs ?? new Dictionary<string, WorkflowPhaseConfigContract>(),
            mode,
            request.Transitions ?? new Dictionary<string, IReadOnlyList<string>>());
    }

    public static WorkflowBindingInput SetOperationMode(
        string workflowId, string projectId, string templateId, string versionId,
        SetWorkflowOperationModeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = Id(workflowId);
        return CreateBinding(new CreateWorkflowRequest(projectId, templateId, versionId,
            request.Mode, request.SemiautonomousPauseGates, request.RiskAcceptanceNote));
    }

    public static string RunTransition(TransitionWorkflowRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Transition switch
        {
            "pause" => "pause",
            "resume" => "resume",
            "cancel" => "cancel",
            _ => throw new ArgumentException("Run transition is invalid.", nameof(request)),
        };
    }

    public static (string PhaseKey, string ObjectiveKey, string TargetState) AdvanceObjective(
        AdvanceWorkflowObjectiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.TargetState is "executed" or "validated" or "approved"
            ? request.TargetState : throw new ArgumentException("Objective target state is invalid.", nameof(request));
        return (Text(request.PhaseKey, 100), Text(request.ObjectiveKey, 100), target);
    }

    public static (string PhaseKey, string GateKey, bool Passed, string? Note) EvaluateGate(
        EvaluateWorkflowGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : Text(request.Note, 10_000);
        if (!request.Passed && note is null)
            throw new ArgumentException("A failed gate requires a note.", nameof(request));
        return (Text(request.PhaseKey, 100), Text(request.GateKey, 100), request.Passed, note);
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

public sealed record WorkflowDraftTemplateCreation(
    string TemplateId, string Name, string Description, DateTimeOffset OccurredAt);

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

public sealed record WorkflowVersionCreation(
    WorkflowTemplateCreation Hierarchy,
    IReadOnlyDictionary<string, WorkflowPhaseConfigContract> PhaseConfigs,
    string? DefaultOperationMode,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions);
