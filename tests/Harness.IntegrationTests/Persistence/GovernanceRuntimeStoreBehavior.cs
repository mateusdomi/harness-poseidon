using Harness.Persistence.Abstractions.Governance;

namespace Harness.IntegrationTests.Persistence;

public static class GovernanceRuntimeStoreBehavior
{
    public static async Task AssertAsync(
        IGovernanceRuntimeStore store,
        string tenantId,
        CancellationToken token)
    {
        const string projectId = "01ARZ3NDEKTSV4RRFFQ69G5FQ1";
        const string turnId = "01ARZ3NDEKTSV4RRFFQ69G5FQ2";
        var at = DateTimeOffset.Parse("2026-07-20T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var create = new GovernanceTurnReceiptCreateCommand(
            tenantId, projectId, turnId, turnId, turnId, "chief", "1.0.0",
            [new GovernanceReceiptDocumentRecord("governance-core", "sha256:" + new string('a', 64), "always", "Always", 900)],
            900, [], [], 0, "fake", "fake-model", at, new string('b', 64));
        var receipt = await store.CreateReceiptAsync(create, token);
        Assert.Equal(GovernanceReceiptState.Selected, receipt.State);
        Assert.Equal(1, receipt.Version);
        var replay = await store.CreateReceiptAsync(create, token);
        Assert.Equal(receipt.BundleChecksum, replay.BundleChecksum);
        Assert.Equal(receipt.Version, replay.Version);
        receipt = await store.CompleteReceiptAsync(
            new GovernanceTurnReceiptCompleteCommand(
                tenantId, turnId, 1, 1100, GovernanceReceiptState.Completed, "pass", at.AddSeconds(1)), token);
        Assert.Equal(2, receipt.Version);
        Assert.Equal(1100, receipt.ActualPromptTokens);
        await Assert.ThrowsAsync<GovernanceRuntimeConflictException>(() => store.CompleteReceiptAsync(
            new GovernanceTurnReceiptCompleteCommand(
                tenantId, turnId, 2, null, GovernanceReceiptState.Delivered, null, at.AddSeconds(2)), token));
        await Assert.ThrowsAsync<GovernanceRuntimeConflictException>(() => store.CompleteReceiptAsync(
            new GovernanceTurnReceiptCompleteCommand(
                tenantId, turnId, 1, null, GovernanceReceiptState.Failed, "fail", at.AddSeconds(2)), token));
        await store.AppendMetricAsync(new GovernanceMetricAppendCommand(
            tenantId, projectId, turnId, "01ARZ3NDEKTSV4RRFFQ69G5FQ3",
            GovernanceMetricKind.Selected, "governance-core", null, null, 900, at), token);
        await store.AppendMetricAsync(new GovernanceMetricAppendCommand(
            tenantId, projectId, turnId, "01ARZ3NDEKTSV4RRFFQ69G5FQ4",
            GovernanceMetricKind.GateResult, null, null, "pass", null, at.AddSeconds(1)), token);
        var metrics = await store.ListMetricsAsync(tenantId, turnId, token);
        Assert.Equal(2, metrics.Count);
        Assert.Single(await store.ListReceiptsAsync(tenantId, projectId, null, 10, token));

        const string snapshotId = "01ARZ3NDEKTSV4RRFFQ69G5FQ5";
        var snapshotCommand = new ContextSnapshotCreateCommand(
            tenantId,
            snapshotId,
            projectId,
            turnId,
            turnId,
            "1.0.0",
            ["governance-core"],
            [
                new ContextSnapshotSourceRecord(
                    "memory:attachment-1",
                    "Memory",
                    "solicitation_attachment:attachment-1"),
            ],
            new string('c', 64),
            123,
            at);
        var snapshot = await store.CreateContextSnapshotAsync(snapshotCommand, token);
        Assert.Equal(snapshotCommand.AssembledContextHash, snapshot.AssembledContextHash);
        Assert.Equal(snapshotCommand.BundleManifestIds, snapshot.BundleManifestIds);
        Assert.Equal(snapshotCommand.Sources, snapshot.Sources);
        var replayedSnapshot = await store.CreateContextSnapshotAsync(snapshotCommand, token);
        Assert.Equal(snapshot.AssembledContextHash, replayedSnapshot.AssembledContextHash);
        await Assert.ThrowsAsync<GovernanceRuntimeConflictException>(() =>
            store.CreateContextSnapshotAsync(
                snapshotCommand with { AssembledContextHash = new string('d', 64) },
                token));
        var loadedSnapshot = await store.GetContextSnapshotAsync(tenantId, snapshotId, token);
        Assert.NotNull(loadedSnapshot);
        Assert.Equal(snapshot.SnapshotId, loadedSnapshot.SnapshotId);
        Assert.Equal(snapshot.AssembledContextHash, loadedSnapshot.AssembledContextHash);
        Assert.Equal(snapshot.BundleManifestIds, loadedSnapshot.BundleManifestIds);
        Assert.Equal(snapshot.Sources, loadedSnapshot.Sources);
    }
}
