using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Workflows.Domain;

public sealed class WorkflowDefinitionTag;

public sealed class WorkflowDefinitionVersionTag;

public sealed class WorkflowRunTag;

internal static class WorkflowIdFactory
{
    public static EntityId<TTag> New<TTag>(IClock clock)
        where TTag : notnull => EntityId<TTag>.New(clock);
}
