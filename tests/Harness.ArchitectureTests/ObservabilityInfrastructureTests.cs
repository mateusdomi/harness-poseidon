namespace Harness.ArchitectureTests;

public sealed class ObservabilityInfrastructureTests
{
    [Fact]
    public void CollectorUsesPinnedAuthorizedImageAndLoopbackPorts()
    {
        var compose = ReadRepositoryFile("infra/compose/observability.compose.yaml");

        foreach (var image in new[]
                 {
                     "otel/opentelemetry-collector-contrib:0.157.0",
                     "grafana/grafana:13.1.0",
                     "grafana/loki:3.7.2",
                     "grafana/tempo:2.10.5",
                     "prom/prometheus:v3.12.0",
                 })
        {
            Assert.Contains($"image: {image}", compose, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\n      - 0.0.0.0:", compose, StringComparison.Ordinal);

        foreach (var port in new[] { 3000, 3100, 3200, 4317, 4318, 9090, 9464, 13133 })
        {
            Assert.Contains($"127.0.0.1:{port}:{port}", compose, StringComparison.Ordinal);
        }

        Assert.Contains(
            "POSEIDON_GRAFANA_ADMIN_PASSWORD:?",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CollectorRedactsTelemetryBeforeBatching()
    {
        var configuration = ReadRepositoryFile("infra/compose/otel-collector.yaml");
        var transformIndex = configuration.IndexOf(
            "        - transform/remove_sensitive",
            StringComparison.Ordinal);
        var redactionIndex = configuration.IndexOf(
            "        - redaction",
            StringComparison.Ordinal);
        var batchIndex = configuration.IndexOf(
            "        - batch",
            StringComparison.Ordinal);

        Assert.True(transformIndex >= 0);
        Assert.True(redactionIndex > transformIndex);
        Assert.True(batchIndex > redactionIndex);
        Assert.Contains("set(body, \"[REDACTED]\")", configuration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("traces:")]
    [InlineData("metrics:")]
    [InlineData("logs:")]
    public void CollectorDefinesRequiredSignalPipeline(string pipeline)
    {
        var configuration = ReadRepositoryFile("infra/compose/otel-collector.yaml");

        Assert.Contains(pipeline, configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void GrafanaProvisioningCorrelatesMetricsLogsAndTraces()
    {
        var datasources = ReadRepositoryFile(
            "infra/compose/grafana/provisioning/datasources/datasources.yaml");
        var dashboard = ReadRepositoryFile(
            "infra/compose/grafana/dashboards/poseidon-operations.json");

        foreach (var uid in new[] { "uid: loki", "uid: prometheus", "uid: tempo" })
        {
            Assert.Contains(uid, datasources, StringComparison.Ordinal);
        }

        Assert.Contains("tracesToLogsV2:", datasources, StringComparison.Ordinal);
        Assert.Contains("poseidon_agent_execution_count_total", dashboard, StringComparison.Ordinal);
        Assert.Contains("poseidon_durable_operation_count_total", dashboard, StringComparison.Ordinal);
        Assert.Contains("poseidon_channel_operation_count_total", dashboard, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Não foi possível localizar a raiz contendo Harness.sln.");

        return File.ReadAllText(Path.Combine(root, relativePath));
    }
}
