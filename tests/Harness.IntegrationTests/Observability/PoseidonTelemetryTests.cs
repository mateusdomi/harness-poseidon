using System.Diagnostics;
using Harness.Host.Observability;
using Microsoft.Extensions.Configuration;

namespace Harness.IntegrationTests.Observability;

public sealed class PoseidonTelemetryTests
{
    [Fact]
    public void SensitiveProcessorRemovesPayloadIdentityAndSecretTags()
    {
        using var activity = new Activity("test").Start();
        activity.SetTag("url.full", "https://example.test/api?access_token=secret");
        activity.SetTag("user.email", "person@example.test");
        activity.SetTag("gen_ai.prompt", "private prompt");
        activity.SetTag("poseidon.execution_id", "execution-42");

        var processor = new SensitiveTelemetryProcessor();
        processor.OnEnd(activity);

        Assert.Null(activity.GetTagItem("url.full"));
        Assert.Null(activity.GetTagItem("user.email"));
        Assert.Null(activity.GetTagItem("gen_ai.prompt"));
        Assert.Equal("execution-42", activity.GetTagItem("poseidon.execution_id"));
    }

    [Fact]
    public void ChiefTurnSpanContainsOnlySafeCorrelationAttributes()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PoseidonTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = PoseidonTelemetry.StartChiefTurn(
            "tenant-1",
            "project-1",
            "conversation-1",
            "turn-1",
            "agent-1");

        Assert.NotNull(activity);
        Assert.Equal("tenant-1", activity.GetTagItem("tenant_id"));
        Assert.Equal("project-1", activity.GetTagItem("project_id"));
        Assert.Equal("conversation-1", activity.GetTagItem("conversation_id"));
        Assert.Equal("turn-1", activity.GetTagItem("chief_turn_id"));
        Assert.Equal("agent-1", activity.GetTagItem("agent_id"));
        Assert.DoesNotContain(
            activity.TagObjects,
            tag => tag.Key.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                   tag.Key.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
                   tag.Key.Contains("response", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317", true)]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "https://collector/v1/traces", true)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "ftp://collector", false)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://user:secret@collector", false)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "", false)]
    public void OtlpExportRequiresAnExplicitSafeHttpEndpoint(
        string key,
        string endpoint,
        bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = endpoint,
            })
            .Build();

        Assert.Equal(
            expected,
            PoseidonTelemetry.HasOtlpEndpoint(
                configuration,
                "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"));
    }
}
