using System.Data.Common;
using Harness.Persistence.Abstractions.Foundation;

namespace Harness.IntegrationTests.Persistence;

internal static class FoundationTransactionBehavior
{
    public const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    public const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    public static async Task AssertAsync(
        IFoundationTransactionStore store,
        CancellationToken cancellationToken)
    {
        var command = Command();
        var concurrentResults = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => store.ProvisionProjectAsync(command, cancellationToken)));

        Assert.Single(concurrentResults, receipt => !receipt.Replay);
        Assert.Equal(9, concurrentResults.Count(receipt => receipt.Replay));
        Assert.All(concurrentResults, receipt =>
        {
            Assert.Equal(command.ProjectId, receipt.ProjectId);
            Assert.Equal(1, receipt.LedgerSequence);
            Assert.Equal(command.OutboxMessageId, receipt.OutboxMessageId);
            Assert.Equal(
                AuditLedgerHash.Compute(
                    AuditLedgerHash.Genesis,
                    command.TenantId,
                    1,
                    command.EventType,
                    command.PayloadJson,
                    command.OccurredAt),
                receipt.LedgerHash);
        });

        var expectedSnapshot = new FoundationStoreSnapshot(1, 1, 1, 1, 1, 1, 1);
        Assert.Equal(expectedSnapshot, await store.ReadSnapshotAsync(cancellationToken));

        var malformedPayload = command with
        {
            IdempotencyKey = "foundation:malformed-payload",
            PayloadJson = "{not-json}",
        };
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ProvisionProjectAsync(malformedPayload, cancellationToken));
        Assert.Equal(expectedSnapshot, await store.ReadSnapshotAsync(cancellationToken));

        var conflict = command with { MessageHash = new string('B', 64) };
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => store.ProvisionProjectAsync(conflict, cancellationToken));
        Assert.Equal(expectedSnapshot, await store.ReadSnapshotAsync(cancellationToken));

        var rollback = command with
        {
            TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FB1",
            TenantName = "Rollback Tenant",
            OrganizationId = "01ARZ3NDEKTSV4RRFFQ69G5FB2",
            OrganizationName = "Rollback Organization",
            ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FB3",
            ProjectName = "Rollback Project",
            UserId = "01ARZ3NDEKTSV4RRFFQ69G5FB4",
            UserDisplayName = "Rollback User",
            IdempotencyKey = "foundation:rollback",
            MessageHash = new string('C', 64),
            LedgerEventId = "01ARZ3NDEKTSV4RRFFQ69G5FB5",
        };
        await Assert.ThrowsAnyAsync<DbException>(
            () => store.ProvisionProjectAsync(rollback, cancellationToken));
        Assert.Equal(expectedSnapshot, await store.ReadSnapshotAsync(cancellationToken));
    }

    public static ProjectProvisionCommand Command() => new(
        TenantId,
        "Tenant",
        "01ARZ3NDEKTSV4RRFFQ69G5FAW",
        "Organization",
        ProjectId,
        "Project",
        "01ARZ3NDEKTSV4RRFFQ69G5FAY",
        "Local User",
        "foundation:provision-project",
        new string('A', 64),
        "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
        "01ARZ3NDEKTSV4RRFFQ69G5FB0",
        "project.created",
        "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAX\"}",
        new DateTimeOffset(2026, 7, 18, 13, 40, 0, TimeSpan.Zero));
}
