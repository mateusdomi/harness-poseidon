using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Host;
using Harness.Host.Observability;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Observability;

public sealed class DurableExecutionTelemetryTests
{
    [Fact]
    public async Task HostDecoratesDurableEngineAndEmitsSafeCorrelationSpan()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;
        var tenantId = UlidValue.New(now).ToString();
        var executionId = UlidValue.New(now.AddTicks(1)).ToString();
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

                var snapshot = await engine.GetAsync(tenantId, executionId, timeout.Token);

                Assert.Null(snapshot);
                var span = Assert.Single(
                    activities,
                    activity =>
                        activity.OperationName == "poseidon.durable.get" &&
                        Equals(activity.GetTagItem("tenant_id"), tenantId) &&
                        Equals(activity.GetTagItem("execution_id"), executionId));
                Assert.Equal(tenantId, span.GetTagItem("tenant_id"));
                Assert.Equal(executionId, span.GetTagItem("execution_id"));
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
