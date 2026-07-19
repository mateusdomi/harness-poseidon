using System.Diagnostics;
using Harness.Host.RunTargets;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.RunTargets;

public sealed class RunTargetDockerLifecycleTests
{
    private const string Dockerfile = """
        FROM alpine:3.24
        EXPOSE 8080
        CMD ["sh", "-c", "while true; do printf 'HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\ndocker-ok' | nc -l -p 8080; done"]
        """;

    private const string Compose = """
        services:
          web:
            build:
              context: .
              dockerfile_inline: |
                FROM alpine:3.24
                EXPOSE 8080
            expose:
              - "8080"
            command:
              - sh
              - -c
              - >-
                while true; do printf 'HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\ncompose-ok' | nc -l -p 8080; done
        """;

    [Fact]
    public async Task DetectsStartsStopsAndCleansDockerfileAndComposeTargets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"docker-run-targets-{Guid.NewGuid():N}");
        var dockerDirectory = Path.Combine(root, "docker-service");
        var composeDirectory = Path.Combine(root, "compose-service");
        Directory.CreateDirectory(dockerDirectory);
        Directory.CreateDirectory(composeDirectory);
        await File.WriteAllTextAsync(Path.Combine(dockerDirectory, "Dockerfile"), Dockerfile, timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(composeDirectory, "compose.yaml"), Compose, timeout.Token);

        var lifecycle = new DockerRunTargetLifecycle();
        var targetIds = new List<string>();
        try
        {
            var definitions = await new RunTargetDetector().DetectAsync(root, timeout.Token);
            var docker = Assert.Single(definitions, target => target.Environment["HARNESS_RUN_DOCKER_MODE"] == "dockerfile");
            var compose = Assert.Single(definitions, target => target.Environment["HARNESS_RUN_DOCKER_MODE"] == "compose");
            Assert.All(new[] { docker, compose }, target =>
            {
                Assert.Equal("http", target.Kind);
                Assert.StartsWith("http://127.0.0.1:", target.Url, StringComparison.Ordinal);
                Assert.True(target.Port is > 0);
                Assert.True(target.Environment.ContainsKey("HARNESS_RUN_DOCKER_MODE"));
            });

            foreach (var definition in new[] { docker, compose })
            {
                var targetId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                targetIds.Add(targetId);
                var record = new RunTargetRecord(
                    targetId,
                    UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                    definition.Name,
                    definition.Kind,
                    definition.Url,
                    definition.Port,
                    "stopped",
                    DateTimeOffset.UtcNow,
                    null);
                var launch = new RunTargetLaunchRecord(
                    record,
                    definition.WorkingDirectory,
                    definition.Executable,
                    definition.Arguments,
                    definition.Environment);
                var logs = new List<string>();
                var start = await lifecycle.PrepareAsync(
                    launch,
                    line =>
                    {
                        logs.Add(line);
                        return Task.CompletedTask;
                    },
                    timeout.Token);
                using var process = new Process { StartInfo = start };
                Assert.True(process.Start());
                var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
                await WaitForBodyAsync(
                    definition.Url!,
                    definition.Environment["HARNESS_RUN_DOCKER_MODE"] == "dockerfile" ? "docker-ok" : "compose-ok",
                    timeout.Token);
                await lifecycle.CleanupAsync(targetId, _ => Task.CompletedTask, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                _ = await standardOutput;
                _ = await standardError;
                Assert.Empty(await ManagedResourcesAsync(targetId, timeout.Token));
            }
        }
        finally
        {
            foreach (var targetId in targetIds)
            {
                await lifecycle.CleanupAsync(targetId, _ => Task.CompletedTask, CancellationToken.None);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefusesComposeWithPrePublishedHostPort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"docker-run-target-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "compose.yaml"),
                Compose.Replace("expose:\n      - \"8080\"", "ports:\n      - \"18080:8080\"", StringComparison.Ordinal),
                timeout.Token);
            var definitions = await new RunTargetDetector().DetectAsync(root, timeout.Token);
            Assert.DoesNotContain(
                definitions,
                target => target.Environment.TryGetValue("HARNESS_RUN_DOCKER_MODE", out var mode) && mode == "compose");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForBodyAsync(string url, string expected, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        for (var attempt = 0; attempt < 240; attempt++)
        {
            try
            {
                if (await client.GetStringAsync(url, cancellationToken) == expected) return;
            }
            catch (HttpRequestException)
            {
                // Container startup is asynchronous; retry until the bounded test timeout.
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"Managed Docker service {url} did not become ready.");
    }

    private static async Task<IReadOnlyList<string>> ManagedResourcesAsync(
        string targetId,
        CancellationToken cancellationToken)
    {
        var resources = new List<string>();
        foreach (var kind in new[] { "container", "network", "volume", "image" })
        {
            var start = new ProcessStartInfo
            {
                FileName = "/usr/local/bin/docker",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(kind);
            start.ArgumentList.Add("ls");
            if (kind == "container") start.ArgumentList.Add("--all");
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add($"label=com.harness.run-target={targetId}");
            start.ArgumentList.Add("--format");
            start.ArgumentList.Add(kind == "image" ? "{{.Repository}}:{{.Tag}}" : kind is "network" or "volume" ? "{{.Name}}" : "{{.Names}}");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker CLI did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            resources.AddRange((await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return resources;
    }
}
