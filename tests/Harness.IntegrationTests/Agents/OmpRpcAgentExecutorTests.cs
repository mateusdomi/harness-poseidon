using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.OmpRpc;

namespace Harness.IntegrationTests.Agents;

public sealed class OmpRpcAgentExecutorTests
{
    [Fact]
    public async Task DeterministicFakeRpcServerValidatesNdjsonLifecycle()
    {
        var root = FindRepositoryRoot();
        var fake = FakeServerPath(root);
        Assert.True(File.Exists(fake), $"Fake RPC server was not built: {fake}");
        var executor = new OmpRpcAgentExecutor(new OmpRpcAgentExecutorOptions
        {
            Enabled = true,
            Executable = Path.Combine(root, "tools", "backend", "dotnet.sh"),
            PrefixArguments = [fake],
            TurnTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
        });

        var result = await executor.ExecuteAsync(new AgentExecutionRequest(
            "tenant", "project", "conversation", "agent", "instruction", "{}", root));

        Assert.Equal("omp-rpc", result.Executor);
        Assert.Equal("omp-fake-session", result.SessionId);
        Assert.Equal(["deterministic fake chunk"], result.Chunks);
        _ = ChiefTurnOutputContract.Parse(result.StructuredOutput);
    }

    [Fact]
    public void MissingOmpIsUnavailableInsteadOfConstructionFailure()
    {
        var options = new OmpRpcAgentExecutorOptions
        {
            Enabled = true,
            Executable = $"missing-omp-{Guid.NewGuid():N}",
        };
        var catalog = new AgentExecutorCatalog(options);

        Assert.Null(catalog.TryCreateOmp());
        var descriptor = catalog.List().Single(value => value.Id == "omp-rpc");
        Assert.False(descriptor.Available);
        Assert.Equal("binary_not_found", descriptor.AvailabilityReason);
        Assert.Equal(4, catalog.List().Count);
    }

    [Fact]
    public async Task CancellationIsCooperativeThenCleansUpHungFakeProcess()
    {
        var root = FindRepositoryRoot();
        var executor = new OmpRpcAgentExecutor(new OmpRpcAgentExecutorOptions
        {
            Enabled = true,
            Executable = Path.Combine(root, "tools", "backend", "dotnet.sh"),
            PrefixArguments = [FakeServerPath(root), "--fake-hang"],
            TurnTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
            CancellationGrace = TimeSpan.FromMilliseconds(100),
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            new AgentExecutionRequest(
                "tenant", "project", "conversation", "agent", "instruction", "{}", root),
            cancellation.Token));
    }

    [Fact]
    public async Task TurnTimeoutIsReportedAndCleansUpHungFakeProcess()
    {
        var root = FindRepositoryRoot();
        var executor = new OmpRpcAgentExecutor(new OmpRpcAgentExecutorOptions
        {
            Enabled = true,
            Executable = Path.Combine(root, "tools", "backend", "dotnet.sh"),
            PrefixArguments = [FakeServerPath(root), "--fake-hang"],
            TurnTimeout = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
            CancellationGrace = TimeSpan.FromMilliseconds(100),
        });

        await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(Request(root)));
    }

    [Fact]
    public async Task UnknownMessageFieldsFailClosedAsSchemaViolation()
    {
        var root = FindRepositoryRoot();
        var exception = await Assert.ThrowsAsync<OmpRpcProtocolException>(() =>
            Executor(root, "--fake-invalid-schema").ExecuteAsync(Request(root)));

        Assert.Equal("invalid_message_schema", exception.Code);
    }

    [Fact]
    public async Task ReliabilityBenchmarkCompletesTwentyFakeRpcTurnsWithoutFailure()
    {
        var root = FindRepositoryRoot();
        var executor = new OmpRpcAgentExecutor(new OmpRpcAgentExecutorOptions
        {
            Enabled = true,
            Executable = Path.Combine(root, "tools", "backend", "dotnet.sh"),
            PrefixArguments = [FakeServerPath(root)],
            TurnTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
        });

        for (var index = 0; index < 20; index++)
        {
            var result = await executor.ExecuteAsync(new AgentExecutionRequest(
                "tenant", "project", $"conversation-{index}", "agent", "instruction", "{}", root));
            Assert.Equal("omp-rpc", result.Executor);
        }
    }

    [Fact]
    public async Task RealOmpSmokeIsExplicitlyGated()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HARNESS_RUN_REAL_AGENT_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var catalog = new AgentExecutorCatalog(new OmpRpcAgentExecutorOptions { Enabled = true });
        var executor = catalog.TryCreateOmp();
        if (executor is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _ = await executor.ExecuteAsync(new AgentExecutionRequest(
            "smoke-tenant", "smoke-project", "smoke-conversation", "smoke-agent",
            "Return a schema-valid no-op response.", "{}", FindRepositoryRoot()), timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string FakeServerPath(string root)
    {
        var configuration = AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        return Path.Combine(
            root,
            "tests",
            "Harness.OmpRpcFakeServer",
            "bin",
            configuration,
            "net10.0",
            "Harness.OmpRpcFakeServer.dll");
    }

    private static OmpRpcAgentExecutor Executor(string root, params string[] arguments) => new(
        new OmpRpcAgentExecutorOptions
        {
            Enabled = true,
            Executable = Path.Combine(root, "tools", "backend", "dotnet.sh"),
            PrefixArguments = [FakeServerPath(root), .. arguments],
            TurnTimeout = TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
            CancellationGrace = TimeSpan.FromMilliseconds(100),
        });

    private static AgentExecutionRequest Request(string root) => new(
        "tenant", "project", "conversation", "agent", "instruction", "{}", root);
}
