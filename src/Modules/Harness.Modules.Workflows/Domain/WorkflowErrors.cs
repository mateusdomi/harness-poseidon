using Harness.SharedKernel.Results;

namespace Harness.Modules.Workflows.Domain;

public static class WorkflowErrors
{
    public static ErrorDescriptor VersionNotFound { get; } =
        new("workflows.version.notFound", "Workflows.Definition.VersionNotFound");

    public static ErrorDescriptor VersionNotPublished { get; } =
        new("workflows.version.notPublished", "Workflows.Definition.VersionNotPublished");

    public static ErrorDescriptor InvalidRunState { get; } =
        new("workflows.run.invalidState", "Workflows.Run.InvalidState");

    public static ErrorDescriptor PhaseNotActive { get; } =
        new("workflows.phase.notActive", "Workflows.Run.PhaseNotActive");

    public static ErrorDescriptor ObjectiveNotFound { get; } =
        new("workflows.objective.notFound", "Workflows.Run.ObjectiveNotFound");

    public static ErrorDescriptor ObjectiveTransitionInvalid { get; } =
        new("workflows.objective.transitionInvalid", "Workflows.Run.ObjectiveTransitionInvalid");

    public static ErrorDescriptor GateMustUseEvaluation { get; } =
        new("workflows.gate.evaluationRequired", "Workflows.Run.GateEvaluationRequired");

    public static ErrorDescriptor GateNotFound { get; } =
        new("workflows.gate.notFound", "Workflows.Run.GateNotFound");

    public static ErrorDescriptor GateRequirementsNotMet { get; } =
        new("workflows.gate.requirementsNotMet", "Workflows.Run.GateRequirementsNotMet");

    public static ErrorDescriptor PhaseCompletionBlocked { get; } =
        new("workflows.phase.completionBlocked", "Workflows.Run.PhaseCompletionBlocked");

    public static ErrorDescriptor NoPreviousPhaseToRollback { get; } =
        new("workflows.phase.noPreviousPhase", "Workflows.Run.NoPreviousPhaseToRollback");

    public static ErrorDescriptor PhaseTimeoutExceeded { get; } =
        new("workflows.phase.timeoutExceeded", "Workflows.Run.PhaseTimeoutExceeded");
}
