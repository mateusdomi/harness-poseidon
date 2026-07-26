namespace Harness.ArchitectureTests;

public sealed class ObservabilityInfrastructureTests
{
    [Fact]
    public void CollectorUsesPinnedAuthorizedImageAndLoopbackPorts()
    {
        var compose = ReadRepositoryFile("infra/compose/observability.compose.yaml");

        Assert.Contains(
            "image: otel/opentelemetry-collector-contrib:0.157.0",
            compose,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\n      - 0.0.0.0:", compose, StringComparison.Ordinal);

        foreach (var port in new[] { 4317, 4318, 9464, 13133 })
        {
            Assert.Contains($"127.0.0.1:{port}:{port}", compose, StringComparison.Ordinal);
        }
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
