using System.Diagnostics;
using System.Globalization;
using Harness.RecoveryTests.Fixtures;

namespace Harness.RecoveryTests;

public sealed class DurableExecutionRecoveryPocTests
{
    [Fact]
    public async Task SigkillIsReconciledWithoutLostOrDuplicateCheckpoints()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var repositoryRoot = FindRepositoryRoot();
        var artifactDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-2",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactDirectory, "recovery.db");
        var readySignalPath = Path.Combine(artifactDirectory, "checkpoint.ready");
        var taskId = "synthetic-recovery-task";
        Process? worker = null;

        Directory.CreateDirectory(artifactDirectory);

        try
        {
            await using (var initialStore = await SyntheticDurableTaskStore.OpenAsync(databasePath, timeout.Token))
            {
                await initialStore.EnqueueAsync(taskId, totalSteps: 6, DateTimeOffset.UtcNow, timeout.Token);
            }

            worker = StartWorker(repositoryRoot, databasePath, taskId, readySignalPath);
            var standardOutput = worker.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = worker.StandardError.ReadToEndAsync(timeout.Token);

            await WaitForSignalAsync(worker, readySignalPath, standardOutput, standardError, timeout.Token);
            await SendSigkillAsync(worker, timeout.Token);

            Assert.True(worker.HasExited);
            Assert.NotEqual(0, worker.ExitCode);

            await using var restartedStore = await SyntheticDurableTaskStore.OpenAsync(databasePath, timeout.Token);
            var interrupted = await restartedStore.ReadAsync(taskId, timeout.Token);

            Assert.Equal(SyntheticTaskState.Running, interrupted.State);
            Assert.Equal(3, interrupted.CompletedSteps);
            Assert.Equal(3, interrupted.CheckpointCount);
            Assert.Equal(1, interrupted.AttemptCount);
            Assert.Equal("owner-before-kill", interrupted.Owner);

            var reconciled = await restartedStore.ReconcileInterruptedAsync(
                interrupted.LastHeartbeatAt.AddMilliseconds(1),
                DateTimeOffset.UtcNow,
                timeout.Token);
            Assert.Equal(1, reconciled);

            await restartedStore.RunAsync(
                taskId,
                "owner-after-restart",
                pauseAfterStep: null,
                readySignalPath: null,
                timeout.Token);

            var completed = await restartedStore.ReadAsync(taskId, timeout.Token);
            Assert.Equal(SyntheticTaskState.Completed, completed.State);
            Assert.Equal(6, completed.TotalSteps);
            Assert.Equal(6, completed.CompletedSteps);
            Assert.Equal(6, completed.CheckpointCount);
            Assert.Equal(2, completed.AttemptCount);
            Assert.Equal(1, completed.ReconciliationCount);
            Assert.Null(completed.Owner);
        }
        finally
        {
            if (worker is { HasExited: false })
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }

            worker?.Dispose();

            if (Directory.Exists(artifactDirectory))
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    private static Process StartWorker(
        string repositoryRoot,
        string databasePath,
        string taskId,
        string readySignalPath)
    {
        var dotnet = Path.Combine(repositoryRoot, "tools", "backend", ".tooling", "dotnet", "dotnet");
        var assembly = typeof(RecoveryFixtureProgram).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("durable-worker");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add(taskId);
        startInfo.ArgumentList.Add("owner-before-kill");
        startInfo.ArgumentList.Add("3");
        startInfo.ArgumentList.Add(readySignalPath);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start synthetic durable worker process.");
    }

    private static async Task WaitForSignalAsync(
        Process worker,
        string readySignalPath,
        Task<string> standardOutput,
        Task<string> standardError,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(readySignalPath))
        {
            if (worker.HasExited)
            {
                throw new InvalidOperationException(
                    $"Synthetic worker exited before checkpoint signal. " +
                    $"stdout: {await standardOutput} stderr: {await standardError}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static async Task SendSigkillAsync(Process worker, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/kill",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-9");
        startInfo.ArgumentList.Add(worker.Id.ToString(CultureInfo.InvariantCulture));

        using var kill = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start /bin/kill for the recovery proof.");
        var error = await kill.StandardError.ReadToEndAsync(cancellationToken);
        await kill.WaitForExitAsync(cancellationToken);

        Assert.True(kill.ExitCode == 0, $"kill -9 failed with exit {kill.ExitCode}: {error}");
        await worker.WaitForExitAsync(cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find repository root for recovery proof.");
    }
}
