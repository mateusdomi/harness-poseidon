using Harness.SharedKernel.Results;

namespace Harness.Modules.Coordination.Domain.Work;

public static class WorkChainErrors
{
    public static ErrorDescriptor DemandNotFound { get; } =
        new("coordination.demand.notFound", "Coordination.WorkChain.DemandNotFound");

    public static ErrorDescriptor TaskNotFound { get; } =
        new("coordination.task.notFound", "Coordination.WorkChain.TaskNotFound");

    public static ErrorDescriptor InstructionNotFound { get; } =
        new("coordination.instruction.notFound", "Coordination.WorkChain.InstructionNotFound");

    public static ErrorDescriptor AttemptNotFound { get; } =
        new("coordination.attempt.notFound", "Coordination.WorkChain.AttemptNotFound");

    public static ErrorDescriptor ActiveAttemptExists { get; } =
        new("coordination.attempt.activeExists", "Coordination.WorkChain.ActiveAttemptExists");

    public static ErrorDescriptor InstructionIsNotLatest { get; } =
        new("coordination.instruction.notLatest", "Coordination.WorkChain.InstructionIsNotLatest");

    public static ErrorDescriptor CorrectionRequired { get; } =
        new("coordination.instruction.correctionRequired", "Coordination.WorkChain.CorrectionRequired");

    public static ErrorDescriptor ReplanningRequired { get; } =
        new("coordination.task.replanningRequired", "Coordination.WorkChain.ReplanningRequired");

    public static ErrorDescriptor TaskIsNotEscalated { get; } =
        new("coordination.task.notEscalated", "Coordination.WorkChain.TaskIsNotEscalated");

    public static ErrorDescriptor TaskIsNotApproved { get; } =
        new("coordination.task.notApproved", "Coordination.WorkChain.TaskIsNotApproved");

    public static ErrorDescriptor TaskIsNotMerged { get; } =
        new("coordination.task.notMerged", "Coordination.WorkChain.TaskIsNotMerged");

    public static ErrorDescriptor InvalidTaskState { get; } =
        new("coordination.task.invalidState", "Coordination.WorkChain.InvalidTaskState");

    public static ErrorDescriptor InvalidAttemptState { get; } =
        new("coordination.attempt.invalidState", "Coordination.WorkChain.InvalidAttemptState");

    public static ErrorDescriptor EvidenceRequired { get; } =
        new("coordination.attempt.evidenceRequired", "Coordination.WorkChain.EvidenceRequired");

    public static ErrorDescriptor ReviewAlreadyRecorded { get; } =
        new("coordination.review.alreadyRecorded", "Coordination.WorkChain.ReviewAlreadyRecorded");

    public static ErrorDescriptor IndependentReviewerRequired { get; } =
        new("coordination.review.independentRequired", "Coordination.WorkChain.IndependentReviewerRequired");
}
