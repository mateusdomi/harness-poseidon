using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Harness.Modules.Agents.Infrastructure.CodexCli;

namespace Harness.IntegrationTests.Agents;

public sealed class CodexCliAgentExecutorPocTests
{
    [Fact]
    public async Task ProcessIsHeartbeatedInterruptedResumedAndRehydratedFromGitCheckpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-4",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var repositoryPath = Path.Combine(artifactRoot, "fixture-repository");
        var stateDirectory = Path.Combine(artifactRoot, "codex-state");
        var reconstructedStateDirectory = Path.Combine(artifactRoot, "reconstructed-codex-state");
        var heartbeats = new ConcurrentQueue<CodexCliHeartbeat>();

        try
        {
            await CreateFixtureRepositoryAsync(repositoryPath, timeout.Token);
            var executable = CodexCliExecutableLocator.Find();
            var options = CreateOptions(executable, artifactRoot, repositoryPath, stateDirectory);

            string persistedThreadId;
            int interruptedProcessId;
            await using (var server = await CodexCliAppServer.StartAsync(
                options,
                (heartbeat, _) =>
                {
                    heartbeats.Enqueue(heartbeat);
                    return ValueTask.CompletedTask;
                },
                timeout.Token))
            {
                var thread = await server.StartThreadAsync(
                    ephemeral: false,
                    developerInstructions: "PoC-4 persisted session marker.",
                    timeout.Token);
                persistedThreadId = thread.ThreadId;
                Assert.False(thread.Ephemeral);
                await server.InjectSessionMarkerAsync(
                    persistedThreadId,
                    "Harness PoC-4 persisted checkpoint marker.",
                    timeout.Token);

                await WaitForHeartbeatsAsync(heartbeats, minimumCount: 2, timeout.Token);
                interruptedProcessId = server.ProcessId;
                await server.InterruptAsync(timeout.Token);

                Assert.False(server.IsRunning);
                Assert.False(IsProcessRunning(interruptedProcessId));
            }

            await using (var resumedServer = await CodexCliAppServer.StartAsync(options, cancellationToken: timeout.Token))
            {
                var resumed = await resumedServer.ResumeThreadAsync(persistedThreadId, timeout.Token);
                Assert.Equal(persistedThreadId, resumed.ThreadId);
                Assert.False(resumed.Ephemeral);
            }

            var checkpoint = await GitCheckpointContextBuilder.CaptureAsync(
                repositoryPath,
                cancellationToken: timeout.Token);
            var reconstructedOptions = CreateOptions(
                executable,
                artifactRoot,
                repositoryPath,
                reconstructedStateDirectory);

            await using (var reconstructedServer = await CodexCliAppServer.StartAsync(
                reconstructedOptions,
                cancellationToken: timeout.Token))
            {
                var reconstructed = await reconstructedServer.StartThreadAsync(
                    ephemeral: true,
                    developerInstructions: checkpoint.CreateDeveloperInstructions(),
                    timeout.Token);

                Assert.NotEqual(persistedThreadId, reconstructed.ThreadId);
                Assert.True(reconstructed.Ephemeral);
                Assert.True(checkpoint.WorkingTreeClean);
                Assert.Equal(40, checkpoint.CommitSha.Length);
                Assert.Contains(checkpoint.CommitSha, checkpoint.CreateDeveloperInstructions(), StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static CodexCliAppServerOptions CreateOptions(
        string executable,
        string artifactRoot,
        string repositoryPath,
        string stateDirectory) =>
        new(
            executable,
            artifactRoot,
            repositoryPath,
            stateDirectory,
            TimeSpan.FromMilliseconds(100));

    private static async Task CreateFixtureRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, ["init", "--initial-branch=main"], cancellationToken);
        await RunGitAsync(repositoryPath, ["config", "user.name", "Harness PoC"], cancellationToken);
        await RunGitAsync(repositoryPath, ["config", "user.email", "poc@harness.invalid"], cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "checkpoint.txt"),
            "durable checkpoint\n",
            cancellationToken);
        await RunGitAsync(repositoryPath, ["add", "checkpoint.txt"], cancellationToken);
        await RunGitAsync(repositoryPath, ["commit", "-m", "checkpoint"], cancellationToken);
    }

    private static async Task RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git fixture process did not start.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git fixture command failed with code {process.ExitCode}: {(await error).Trim()}");
        }
    }

    private static async Task WaitForHeartbeatsAsync(
        ConcurrentQueue<CodexCliHeartbeat> heartbeats,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        while (heartbeats.Count < minimumCount)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        var captured = heartbeats.ToArray();
        Assert.Equal(Enumerable.Range(1, captured.Length).Select(value => (long)value), captured.Select(item => item.Sequence));
        Assert.All(captured, heartbeat => Assert.NotEqual(default, heartbeat.OccurredAt));
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
