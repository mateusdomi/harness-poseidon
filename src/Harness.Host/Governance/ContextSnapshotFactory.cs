using Harness.Modules.Governance.Context;
using Harness.Persistence.Abstractions.Governance;

namespace Harness.Host.Governance;

internal static class ContextSnapshotFactory
{
    public static ContextSnapshotCreateCommand Create(
        string tenantId,
        string snapshotId,
        string projectId,
        string workTaskId,
        string executionId,
        ContextBundle bundle,
        DateTimeOffset createdAt) => new(
            tenantId,
            snapshotId,
            projectId,
            workTaskId,
            executionId,
            bundle.ManifestVersion,
            bundle.Documents
                .Where(document => !document.Truncated)
                .Select(document => document.DocumentId)
                .ToArray(),
            bundle.Segments
                .Select(segment => new ContextSnapshotSourceRecord(
                    segment.SourceId,
                    segment.Kind.ToString(),
                    segment.CitationReference ?? segment.SourceId))
                .ToArray(),
            bundle.BundleChecksum,
            bundle.EstimatedTokens,
            createdAt);
}
