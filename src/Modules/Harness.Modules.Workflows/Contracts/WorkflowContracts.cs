namespace Harness.Modules.Workflows.Contracts;

public enum WorkflowDefinitionVersionStatus
{
    Draft,
    Published,
}

public enum WorkflowRunState
{
    Pending,
    Running,
    Paused,
    Completed,
    Cancelled,
}

public enum WorkflowPhaseRunState
{
    Pending,
    Active,
    Completed,
}

public enum WorkflowGateState
{
    Pending,
    Failed,
    Passed,
}

public enum ObjectiveItemKind
{
    Document,
    Task,
    Test,
    Gate,
    Approval,
    Evidence,
}

public enum ObjectiveItemState
{
    Pending,
    Executed,
    Validated,
    Approved,
}

public sealed record ObjectiveItemDefinitionInput(
    string Key,
    string Name,
    ObjectiveItemKind Kind,
    decimal Weight);

public sealed record WorkflowGateDefinitionInput(
    string Key,
    string Name,
    IReadOnlyList<string> RequiredObjectiveKeys,
    ObjectiveItemState MinimumRequiredState);

public sealed record WorkflowPhaseDefinitionInput(
    string Key,
    string Name,
    IReadOnlyList<ObjectiveItemDefinitionInput> ObjectiveItems,
    IReadOnlyList<WorkflowGateDefinitionInput> Gates);

public sealed record WorkflowProgress(
    decimal Executed,
    decimal Validated,
    decimal Approved);
