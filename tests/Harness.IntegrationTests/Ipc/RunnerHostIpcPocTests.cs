using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Harness.Host;
using Harness.Host.Ipc;
using Harness.SharedKernel.RunnerIpc;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Ipc;

public sealed class RunnerHostIpcPocTests
{
    [Fact]
    public async Task RealRunnerUsesAuthenticatedIdempotentSequencedLoopbackIpc()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var repositoryRoot = FindRepositoryRoot();
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-9",
            $"ipc-{Guid.NewGuid():N}");
        var tokenPath = Path.Combine(artifactRoot, "runner-ipc-token");
        var tokenValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(artifactRoot);
        await File.WriteAllTextAsync(tokenPath, tokenValue, timeout.Token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await using var app = HostApplication.Build(
            ["--urls", "http://127.0.0.1:0"],
            new RunnerIpcToken(tokenValue));
        await app.StartAsync(timeout.Token);

        try
        {
            var baseAddress = GetBaseAddress(app.Services);
            var processor = app.Services.GetRequiredService<RunnerIpcMessageProcessor>();
            var firstRun = await RunRunnerAsync(
                repositoryRoot,
                baseAddress,
                tokenPath,
                "runner-poc9",
                "attempt-poc9",
                1,
                $"{RunnerMessageTypes.Heartbeat},{RunnerMessageTypes.Checkpoint},{RunnerMessageTypes.Completion}",
                "stable",
                timeout.Token);

            Assert.Equal(0, firstRun.ExitCode);
            Assert.DoesNotContain(tokenValue, firstRun.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(tokenValue, firstRun.StandardError, StringComparison.Ordinal);
            var afterFirstRun = processor.ReadAttempt("attempt-poc9");
            Assert.NotNull(afterFirstRun);
            Assert.Equal(3, afterFirstRun.LastSequence);
            Assert.Equal(1, afterFirstRun.HeartbeatCount);
            Assert.Equal(["checkpoint-2"], afterFirstRun.CheckpointIds);
            Assert.True(afterFirstRun.Completed);
            Assert.Equal(3, afterFirstRun.InboxCount);

            var replayRun = await RunRunnerAsync(
                repositoryRoot,
                baseAddress,
                tokenPath,
                "runner-poc9",
                "attempt-poc9",
                1,
                $"{RunnerMessageTypes.Heartbeat},{RunnerMessageTypes.Checkpoint},{RunnerMessageTypes.Completion}",
                "stable",
                timeout.Token);

            Assert.Equal(0, replayRun.ExitCode);
            Assert.Contains("\"replays\":3", replayRun.StandardOutput, StringComparison.Ordinal);
            var afterReplay = processor.ReadAttempt("attempt-poc9");
            Assert.NotNull(afterReplay);
            Assert.Equal(afterFirstRun.LastSequence, afterReplay.LastSequence);
            Assert.Equal(afterFirstRun.HeartbeatCount, afterReplay.HeartbeatCount);
            Assert.Equal(afterFirstRun.CheckpointIds, afterReplay.CheckpointIds);
            Assert.Equal(afterFirstRun.Completed, afterReplay.Completed);
            Assert.Equal(afterFirstRun.InboxCount, afterReplay.InboxCount);

            var gapRun = await RunRunnerAsync(
                repositoryRoot,
                baseAddress,
                tokenPath,
                "runner-poc9",
                "attempt-gap",
                2,
                RunnerMessageTypes.Heartbeat,
                "gap",
                timeout.Token);

            Assert.Equal(2, gapRun.ExitCode);
            Assert.Contains("runner_sequence_gap", gapRun.StandardOutput, StringComparison.Ordinal);
            Assert.Null(processor.ReadAttempt("attempt-gap"));

            var wrongTokenPath = Path.Combine(artifactRoot, "wrong-token");
            await File.WriteAllTextAsync(wrongTokenPath, new string('A', 64), timeout.Token);
            var unauthorizedRun = await RunRunnerAsync(
                repositoryRoot,
                baseAddress,
                wrongTokenPath,
                "runner-unauthorized",
                "attempt-unauthorized",
                1,
                RunnerMessageTypes.Heartbeat,
                "unauthorized",
                timeout.Token);

            Assert.Equal(2, unauthorizedRun.ExitCode);
            Assert.Contains("runner_ipc_unauthorized", unauthorizedRun.StandardOutput, StringComparison.Ordinal);
            Assert.Null(processor.ReadAttempt("attempt-unauthorized"));
        }
        finally
        {
            await app.StopAsync(timeout.Token);
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private static async Task<RunnerProcessResult> RunRunnerAsync(
        string repositoryRoot,
        Uri baseAddress,
        string tokenPath,
        string runnerId,
        string attemptId,
        long startSequence,
        string messages,
        string idempotencyPrefix,
        CancellationToken cancellationToken)
    {
        var dotnetRoot = Path.Combine(repositoryRoot, "tools", "backend", ".tooling", "dotnet");
        var runnerAssembly = Path.Combine(
            repositoryRoot,
            "src",
            "Harness.Runner",
            "bin",
            GetBuildConfiguration(),
            "net10.0",
            "Harness.Runner.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(dotnetRoot, "dotnet"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        foreach (var argument in new[]
        {
            runnerAssembly,
            "--host-url", baseAddress.AbsoluteUri,
            "--token-file", tokenPath,
            "--runner-id", runnerId,
            "--attempt-id", attemptId,
            "--start-sequence", startSequence.ToString(CultureInfo.InvariantCulture),
            "--messages", messages,
            "--idempotency-prefix", idempotencyPrefix,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Harness.Runner did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new RunnerProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static Uri GetBaseAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish a server address.");
        return new Uri(
            addresses.Single(address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)),
            UriKind.Absolute);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string GetBuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new DirectoryNotFoundException("Test build configuration was not found.");

    private sealed record RunnerProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
