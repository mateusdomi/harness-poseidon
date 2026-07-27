using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Sqlite;

namespace Harness.RecoveryTests;

public sealed class GovernanceReceiptRecoveryTests
{
    [Fact]
    public async Task ReceiptSurvivesStoreRestartAndCompletesWithOcc()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "recovery-artifacts",
            $"governance-receipt-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "governance.db");
        Directory.CreateDirectory(artifactRoot);
        const string tenantId = "01ARZ3NDEKTSV4RRFFQ69G5FQ0";
        const string projectId = "01ARZ3NDEKTSV4RRFFQ69G5FQ1";
        const string turnId = "01ARZ3NDEKTSV4RRFFQ69G5FQ2";
        var occurredAt = DateTimeOffset.Parse(
            "2026-07-20T18:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            await using (var firstDispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token))
            {
                Assert.Equal(73, await SqliteMigrationRunner.ApplyAsync(firstDispatcher, timeout.Token));
                var firstStore = new SqliteGovernanceRuntimeStore(firstDispatcher);
                var created = await firstStore.CreateReceiptAsync(
                    new GovernanceTurnReceiptCreateCommand(
                        tenantId,
                        projectId,
                        turnId,
                        turnId,
                        turnId,
                        "chief",
                        "1.1.0",
                        [new GovernanceReceiptDocumentRecord(
                            "governance-core",
                            "sha256:" + new string('a', 64),
                            "always",
                            "Always",
                            900)],
                        900,
                        [],
                        [],
                        0,
                        "fake",
                        "fake-model",
                        occurredAt,
                        new string('b', 64)),
                    timeout.Token);
                Assert.Equal(GovernanceReceiptState.Selected, created.State);
                Assert.Equal(1, created.Version);
            }

            await using var restartedDispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(restartedDispatcher, timeout.Token));
            var restartedStore = new SqliteGovernanceRuntimeStore(restartedDispatcher);
            var recovered = await restartedStore.GetReceiptAsync(tenantId, turnId, timeout.Token);
            Assert.NotNull(recovered);
            Assert.Equal(GovernanceReceiptState.Selected, recovered.State);
            var completed = await restartedStore.CompleteReceiptAsync(
                new GovernanceTurnReceiptCompleteCommand(
                    tenantId,
                    turnId,
                    recovered.Version,
                    925,
                    GovernanceReceiptState.Completed,
                    "pass",
                    occurredAt.AddMinutes(1)),
                timeout.Token);
            Assert.Equal(GovernanceReceiptState.Completed, completed.State);
            Assert.Equal(2, completed.Version);
            Assert.Equal(925, completed.ActualPromptTokens);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }
}
