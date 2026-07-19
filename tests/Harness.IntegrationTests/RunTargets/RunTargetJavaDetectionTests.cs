using Harness.Host.RunTargets;

namespace Harness.IntegrationTests.RunTargets;

public sealed class RunTargetJavaDetectionTests
{
    [Fact]
    public async Task DetectsSpringBootMavenAndGradleAndSkipsPlainJava()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"java-detect-{Guid.NewGuid():N}");
        var maven = Path.Combine(root, "maven-svc");
        var gradle = Path.Combine(root, "gradle-svc");
        var plain = Path.Combine(root, "plain-maven");
        Directory.CreateDirectory(maven);
        Directory.CreateDirectory(gradle);
        Directory.CreateDirectory(plain);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(maven, "pom.xml"),
                "<project><build><plugins><plugin>" +
                "<groupId>org.springframework.boot</groupId>" +
                "<artifactId>spring-boot-maven-plugin</artifactId>" +
                "</plugin></plugins></build></project>",
                timeout.Token);
            await File.WriteAllTextAsync(
                Path.Combine(gradle, "build.gradle"),
                "plugins { id 'org.springframework.boot' version '3.3.0' }",
                timeout.Token);
            // Maven sem Spring Boot: não deve virar alvo executável.
            await File.WriteAllTextAsync(
                Path.Combine(plain, "pom.xml"),
                "<project><groupId>demo</groupId><artifactId>lib</artifactId></project>",
                timeout.Token);

            var detector = new RunTargetDetector();
            var targets = await detector.DetectAsync(root, timeout.Token);

            var mavenTarget = Assert.Single(targets, t => t.Name.EndsWith("(Maven)", StringComparison.Ordinal));
            Assert.Equal("http", mavenTarget.Kind);
            Assert.NotNull(mavenTarget.Port);
            Assert.Contains("spring-boot:run", mavenTarget.Arguments);
            Assert.Contains(mavenTarget.Arguments, a => a.Contains($"--server.port={mavenTarget.Port}", StringComparison.Ordinal));
            Assert.Equal(
                mavenTarget.Port!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                mavenTarget.Environment["SERVER_PORT"]);
            Assert.StartsWith($"http://127.0.0.1:{mavenTarget.Port}", mavenTarget.Url!, StringComparison.Ordinal);

            var gradleTarget = Assert.Single(targets, t => t.Name.EndsWith("(Gradle)", StringComparison.Ordinal));
            Assert.Contains("bootRun", gradleTarget.Arguments);
            Assert.Contains(gradleTarget.Arguments, a => a.Contains($"--server.port={gradleTarget.Port}", StringComparison.Ordinal));

            // Projeto Maven sem Spring Boot não gera alvo (evita alvo que não sobe).
            Assert.DoesNotContain(targets, t => t.WorkingDirectory == Path.GetFullPath(plain));
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
