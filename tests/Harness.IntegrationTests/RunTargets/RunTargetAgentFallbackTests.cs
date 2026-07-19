using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Harness.Host.RunTargets;
using Harness.Modules.Agents.Application.Execution;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.RunTargets;

public sealed class RunTargetAgentFallbackTests
{
    private static readonly string[] ValidArguments = ["python3", "server.py"];

    private const string PythonServer = """
        import http.server, os
        port = int(os.environ["PORT"])
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                body = b"agent-ok"
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def log_message(self, *args):
                pass
        http.server.HTTPServer(("127.0.0.1", port), Handler).serve_forever()
        """;

    [Fact]
    public async Task UsesStrictCachedAgentProposalOnlyAfterDeterministicLayersAndRunsIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"run-agent-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "server.py"), PythonServer, timeout.Token);
        var proposal = JsonSerializer.Serialize(new
        {
            version = 1,
            targets = new[]
            {
                new
                {
                    name = "Unknown Python service",
                    kind = "http",
                    workingDirectory = ".",
                    executable = "/usr/bin/env",
                    arguments = ValidArguments,
                    environment = new Dictionary<string, string> { ["PORT"] = "{port}" },
                },
            },
        });
        var executor = new StubAgentExecutor(proposal);
        var fallback = new RunTargetAgentFallback(
            executor,
            SystemClock.Instance,
            NullLogger<RunTargetAgentFallback>.Instance);
        var detector = new RunTargetDetector(fallback);
        var context = Context();
        Process? process = null;
        try
        {
            var first = await detector.DetectAsync(root, context, timeout.Token);
            var target = Assert.Single(first);
            Assert.Equal("http", target.Kind);
            Assert.EndsWith("(Agent)", target.Name, StringComparison.Ordinal);
            Assert.Equal("agent", target.Environment["HARNESS_RUN_DETECTION_SOURCE"]);
            Assert.DoesNotContain("{port}", target.Arguments);
            Assert.DoesNotContain("{port}", target.Environment["PORT"], StringComparison.Ordinal);
            Assert.Equal(1, executor.CallCount);

            var replay = await detector.DetectAsync(root, context, timeout.Token);
            Assert.Equal(target, Assert.Single(replay));
            Assert.Equal(1, executor.CallCount);

            var start = new ProcessStartInfo
            {
                FileName = target.Executable,
                WorkingDirectory = target.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in target.Arguments) start.ArgumentList.Add(argument);
            foreach (var item in target.Environment) start.Environment[item.Key] = item.Value;
            process = Process.Start(start) ?? throw new InvalidOperationException("Agent target did not start.");
            await WaitForBodyAsync(target.Url!, "agent-ok", timeout.Token);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            process?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotInvokeAgentWhenKnownLayerFindsTarget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"run-agent-known-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "main.py"), PythonServer, timeout.Token);
            var executor = new StubAgentExecutor("not-used");
            var detector = new RunTargetDetector(new RunTargetAgentFallback(
                executor,
                SystemClock.Instance,
                NullLogger<RunTargetAgentFallback>.Instance));
            var targets = await detector.DetectAsync(root, Context(), timeout.Token);
            Assert.Single(targets);
            Assert.Equal(0, executor.CallCount);
            Assert.False(targets[0].Environment.ContainsKey("HARNESS_RUN_DETECTION_SOURCE"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/usr/bin/env", "sh", "PORT", "{port}")]
    [InlineData("/usr/bin/env", "python3", "API_TOKEN", "not-a-real-secret")]
    [InlineData("../../outside", "ignored", "PORT", "{port}")]
    public async Task RejectsUnsafeAgentProposalWithoutBreakingDiscovery(
        string executable,
        string command,
        string environmentName,
        string environmentValue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"run-agent-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var proposal = JsonSerializer.Serialize(new
            {
                version = 1,
                targets = new[]
                {
                    new
                    {
                        name = "Unsafe",
                        kind = "http",
                        workingDirectory = ".",
                        executable,
                        arguments = new[] { command, "{port}" },
                        environment = new Dictionary<string, string> { [environmentName] = environmentValue },
                    },
                },
            });
            var detector = new RunTargetDetector(new RunTargetAgentFallback(
                new StubAgentExecutor(proposal),
                SystemClock.Instance,
                NullLogger<RunTargetAgentFallback>.Instance));
            Assert.Empty(await detector.DetectAsync(root, Context(), timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsExecutableReachedThroughProjectSymlink()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"run-agent-link-{Guid.NewGuid():N}");
        var outside = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"run-agent-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "start"), "not executed", timeout.Token);
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            var proposal = JsonSerializer.Serialize(new
            {
                version = 1,
                targets = new[]
                {
                    new
                    {
                        name = "Linked escape",
                        kind = "process",
                        workingDirectory = ".",
                        executable = "linked/start",
                        arguments = Array.Empty<string>(),
                        environment = new Dictionary<string, string>(),
                    },
                },
            });
            var detector = new RunTargetDetector(new RunTargetAgentFallback(
                new StubAgentExecutor(proposal),
                SystemClock.Instance,
                NullLogger<RunTargetAgentFallback>.Instance));
            Assert.Empty(await detector.DetectAsync(root, Context(), timeout.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static RunTargetDetectionContext Context()
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            UlidValue.New(now).ToString(),
            UlidValue.New(now.AddTicks(1)).ToString(),
            UlidValue.New(now.AddTicks(2)).ToString(),
            "chief");
    }

    private static async Task WaitForBodyAsync(
        string url,
        string expected,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if (await client.GetStringAsync(url, cancellationToken) == expected) return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(50, cancellationToken);
        }
        throw new TimeoutException($"Agent fallback service {url} did not become ready.");
    }

    private sealed class StubAgentExecutor(string response) : IAgentExecutor
    {
        public int CallCount { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("RUN_TARGET_DETECTION_V1", request.Instruction, StringComparison.Ordinal);
            CallCount++;
            var structuredOutput = JsonSerializer.Serialize(new
            {
                response,
                demands = Array.Empty<object>(),
            });
            return Task.FromResult(new AgentExecutionResult(
                "stub",
                $"stub:{request.ProjectId}",
                $"stub:{request.ConversationId}",
                structuredOutput,
                [],
                1));
        }
    }
}
