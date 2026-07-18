using System.Globalization;
using Harness.RecoveryTests.Fixtures;

namespace Harness.RecoveryTests;

public static class RecoveryFixtureProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || !string.Equals(args[0], "durable-worker", StringComparison.Ordinal))
        {
            return 64;
        }

        var databasePath = args[1];
        var taskId = args[2];
        var owner = args[3];
        var pauseAfterStep = int.Parse(args[4], CultureInfo.InvariantCulture);
        var readySignalPath = args[5];

        await using var store = await SyntheticDurableTaskStore.OpenAsync(databasePath);
        await store.RunAsync(taskId, owner, pauseAfterStep, readySignalPath, CancellationToken.None);
        return 0;
    }
}
