using System.Globalization;

namespace Harness.ConcurrencyTests.Leases;

public sealed class LeaseFencingPocTests
{
    [Fact]
    public async Task ExpiredOwnerCannotWriteOrRenewAfterNewFencingTokenIsIssued()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-3",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactDirectory, "fencing.db");
        var initialTime = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var leaseDuration = TimeSpan.FromSeconds(10);

        try
        {
            await using var store = await SyntheticLeaseStore.OpenAsync(databasePath, timeout.Token);
            await store.EnsureResourceAsync("project-chief", initialTime, timeout.Token);

            var tokenA = await store.AcquireAsync(
                "project-chief",
                "owner-a",
                initialTime,
                leaseDuration,
                timeout.Token);
            var ownerBBlocked = await store.AcquireAsync(
                "project-chief",
                "owner-b",
                initialTime.AddSeconds(5),
                leaseDuration,
                timeout.Token);
            var tokenB = await store.AcquireAsync(
                "project-chief",
                "owner-b",
                initialTime.AddSeconds(11),
                leaseDuration,
                timeout.Token);

            Assert.Equal(1, tokenA);
            Assert.Null(ownerBBlocked);
            Assert.Equal(2, tokenB);

            var staleWrite = await store.WriteAsync(
                "project-chief",
                "owner-a",
                tokenA!.Value,
                "stale-value",
                initialTime.AddSeconds(12),
                timeout.Token);
            var staleRenewal = await store.RenewAsync(
                "project-chief",
                "owner-a",
                tokenA.Value,
                initialTime.AddSeconds(12),
                leaseDuration,
                timeout.Token);
            var currentWrite = await store.WriteAsync(
                "project-chief",
                "owner-b",
                tokenB!.Value,
                "current-value",
                initialTime.AddSeconds(12),
                timeout.Token);
            var currentRenewal = await store.RenewAsync(
                "project-chief",
                "owner-b",
                tokenB.Value,
                initialTime.AddSeconds(13),
                leaseDuration,
                timeout.Token);

            var snapshot = await store.ReadAsync("project-chief", timeout.Token);

            Assert.False(staleWrite);
            Assert.False(staleRenewal);
            Assert.True(currentWrite);
            Assert.True(currentRenewal);
            Assert.Equal("owner-b", snapshot.Owner);
            Assert.Equal(2, snapshot.FencingToken);
            Assert.Equal("current-value", snapshot.Value);
            Assert.Equal(initialTime.AddSeconds(23), snapshot.ExpiresAt);
        }
        finally
        {
            if (Directory.Exists(artifactDirectory))
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }
}
