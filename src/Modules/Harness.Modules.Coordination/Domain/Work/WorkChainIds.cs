using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Coordination.Domain.Work;

public sealed class SolicitationTag;

public sealed class DemandTag;

public sealed class WorkTaskTag;

public sealed class InstructionVersionTag;

public sealed class WorkAttemptTag;

public sealed class WorkReviewTag;

internal static class WorkChainIdFactory
{
    public static EntityId<TTag> New<TTag>(IClock clock)
        where TTag : notnull => EntityId<TTag>.New(clock);
}
