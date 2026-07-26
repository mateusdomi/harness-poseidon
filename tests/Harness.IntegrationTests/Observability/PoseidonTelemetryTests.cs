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
