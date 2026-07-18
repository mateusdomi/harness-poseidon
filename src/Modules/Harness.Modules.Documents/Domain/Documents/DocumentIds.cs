using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Modules.Documents.Domain.Documents;

public sealed class DocumentTag;

public sealed class DocumentVersionTag;

public sealed class DocumentApprovalRequestTag;

internal static class DocumentIdFactory
{
    public static EntityId<TTag> New<TTag>(IClock clock)
        where TTag : notnull => EntityId<TTag>.New(clock);
}
