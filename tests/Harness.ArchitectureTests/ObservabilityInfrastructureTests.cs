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
        Assert.Contains("set(log.body, \"[REDACTED]\")", configuration, StringComparison.Ordinal);
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
        Assert.Contains(
            "poseidon:agent_execution_success_ratio:rate5m",
            dashboard,
            StringComparison.Ordinal);
        Assert.Contains(
            "poseidon:chief_turn_success_ratio:rate5m",
            dashboard,
            StringComparison.Ordinal);
        Assert.Contains(
            "poseidon:outbox_dispatch_failure_ratio:rate5m",
            dashboard,
            StringComparison.Ordinal);
        Assert.Contains(
            "poseidon_outbox_recovered_claim_count_total",
            dashboard,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PrometheusDefinesOperationalSlosAndAlerts()
    {
        var compose = ReadRepositoryFile("infra/compose/observability.compose.yaml");
        var prometheus = ReadRepositoryFile("infra/compose/prometheus.yaml");
        var rules = ReadRepositoryFile("infra/compose/prometheus-rules.yaml");

        Assert.Contains(
            "./prometheus-rules.yaml:/etc/prometheus/rules/poseidon.yaml:ro",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "/etc/prometheus/rules/poseidon.yaml",
            prometheus,
            StringComparison.Ordinal);

        foreach (var rule in new[]
                 {
                     "poseidon:agent_execution_success_ratio:rate5m",
                     "poseidon:chief_turn_success_ratio:rate5m",
                     "poseidon:outbox_dispatch_failure_ratio:rate5m",
                     "PoseidonAgentExecutionErrorBudgetBurn",
                     "PoseidonChiefTurnErrorBudgetBurn",
                     "PoseidonOutboxDispatchFailures",
                 })
        {
            Assert.Contains(rule, rules, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LangfuseIsSelfHostedAndReceivesOnlyCollectorTraces()
    {
        var compose = ReadRepositoryFile("infra/compose/langfuse.compose.yaml");
        var configuration = ReadRepositoryFile(
            "infra/compose/otel-collector-langfuse.yaml");

        Assert.Contains(
            "image: langfuse/langfuse:2@sha256:85c278dcab96c15db94191a5c1664f85aba2d7fb6771a00e99681c902c4b7015",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("image: postgres:16", compose, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:3001:3000", compose, StringComparison.Ordinal);
        Assert.Contains(
            "langfuse-postgres-data:/var/lib/postgresql/data",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("condition: service_healthy", compose, StringComparison.Ordinal);
        Assert.Contains(
            "http://127.0.0.1:3000/api/public/health",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "POSEIDON_LANGFUSE_OTLP_ENDPOINT: http://langfuse:3000/api/public/otel",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "POSEIDON_LANGFUSE_POSTGRES_PASSWORD:?",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "POSEIDON_LANGFUSE_AUTH:?",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("POSEIDON_LANGFUSE_NEXTAUTH_SECRET:?", compose, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_LANGFUSE_SALT:?", compose, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_LANGFUSE_ENCRYPTION_KEY:?", compose, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_LANGFUSE_PUBLIC_KEY:?", compose, StringComparison.Ordinal);
        Assert.Contains("POSEIDON_LANGFUSE_SECRET_KEY:?", compose, StringComparison.Ordinal);
        Assert.Contains("TELEMETRY_ENABLED: \"false\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0:", compose, StringComparison.Ordinal);
        Assert.Contains("otlp_http/langfuse:", configuration, StringComparison.Ordinal);
        Assert.Contains(
            "Authorization: Basic ${env:POSEIDON_LANGFUSE_AUTH}",
            configuration,
            StringComparison.Ordinal);
        Assert.Contains(
            "x-langfuse-ingestion-version: \"4\"",
            configuration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("metrics:", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("logs:", configuration, StringComparison.Ordinal);
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
