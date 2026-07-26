using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Host;
using Harness.Host.Observability;
using Harness.Persistence.Abstractions.DurableExecution;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Observability;

public sealed class DurableExecutionTelemetryTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ExecutionId = "01ARZ3NDEKTSV4RRFFQ69G5FB9";

    [Fact]
    public async Task HostDecoratesDurableEngineAndEmitsSafeCorrelationSpan()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"durable-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PoseidonTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            await using var app = HostApplication.Build(
                [
                    "--urls", "http://127.0.0.1:0",
                    "--Harness:DatabasePath", Path.Combine(root, "harness.db"),
                ]);
            await app.StartAsync(timeout.Token);
            try
            {
                var engine = app.Services.GetRequiredService<IDurableExecutionEngine>();
                Assert.IsType<InstrumentedDurableExecutionEngine>(engine);

                var snapshot = await engine.GetAsync(TenantId, ExecutionId, timeout.Token);

                Assert.Null(snapshot);
                var span = Assert.Single(
                    activities,
                    activity => activity.OperationName == "poseidon.durable.get");
                Assert.Equal(TenantId, span.GetTagItem("tenant_id"));
                Assert.Equal(ExecutionId, span.GetTagItem("execution_id"));
                Assert.Equal("not_found", span.GetTagItem("durable.result"));
                Assert.DoesNotContain(
                    span.TagObjects,
                    tag => tag.Key.Contains("payload", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
