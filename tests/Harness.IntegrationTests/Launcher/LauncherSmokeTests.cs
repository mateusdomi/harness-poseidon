using System.Net.Http.Json;
using System.Text.Json;
using Harness.Launcher;

namespace Harness.IntegrationTests.Launcher;

public sealed class LauncherSmokeTests
{
    [Fact]
    public async Task LauncherStartsHostOnDynamicPortWithUserDataDirectoryAndServesSpa()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var options = LauncherOptions.Parse(["--data-dir", root, "--no-browser"]);
            Assert.False(options.OpenBrowser);
            await using var handle = await LauncherApplication.StartAsync(options, timeout.Token);
            Assert.Equal("127.0.0.1", handle.Address.Host);
            Assert.NotEqual(0, handle.Address.Port);
            Assert.Equal(Path.GetFullPath(root), handle.DataDirectory);
            Assert.Equal("onboarding", handle.Readiness.Mode);
            Assert.Contains(
                "GET /api/v1/event-streams/snapshot=200 JSON",
                handle.Readiness.VerifiedEndpoints);
            Assert.Contains(
                "GET /_runner/ipc/status=200 ready",
                handle.Readiness.VerifiedEndpoints);
            Assert.True(File.Exists(Path.Combine(root, "harness.db")));
            Assert.True(File.Exists(Path.Combine(root, DesktopLifecycleManager.ProcessLeaseFileName)));
            var runtimePath = Path.Combine(root, "runtime", "poseidon.json");
            Assert.True(File.Exists(runtimePath));
            using (var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(runtimePath, timeout.Token)))
            {
                var runnerPid = runtime.RootElement.GetProperty("runnerPid").GetInt32();
                Assert.False(System.Diagnostics.Process.GetProcessById(runnerPid).HasExited);
                Assert.Equal(2, runtime.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("onboarding", runtime.RootElement.GetProperty("readinessMode").GetString());
                Assert.True(runtime.RootElement.GetProperty("verifiedEndpoints").GetArrayLength() >= 7);
            }
            Assert.True(File.Exists(Path.Combine(root, "runtime", "runner.token")));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await LauncherApplication.StartAsync(options, timeout.Token));

            using var client = new HttpClient { BaseAddress = handle.Address };
            var health = await client.GetFromJsonAsync<HealthPayload>("/health", timeout.Token);
            Assert.Equal("healthy", health!.Status);
            using var spa = await client.GetAsync(new Uri("/", UriKind.Relative), timeout.Token);
            spa.EnsureSuccessStatusCode();
            var body = await spa.Content.ReadAsStringAsync(timeout.Token);
            Assert.Contains("<div id=\"root\">", body, StringComparison.Ordinal);

            Assert.Throws<ArgumentException>(() => LauncherOptions.Parse(["--porta", "x"]));
            Assert.Throws<ArgumentException>(() => LauncherOptions.Parse(["--port", "0"]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task LauncherVerifiesEssentialApisWithPersistedPersonalSessionBeforeBecomingReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"launcher-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var options = LauncherOptions.Parse(["--data-dir", root, "--no-browser"]);
            await using (var firstRun = await LauncherApplication.StartAsync(options, timeout.Token))
            {
                using var client = new HttpClient { BaseAddress = firstRun.Address };
                using var created = await client.PostAsJsonAsync(
                    "/api/v1/profiles",
                    new
                    {
                        displayName = "Readiness",
                        email = (string?)null,
                        avatarUrl = (string?)null,
                        locale = "pt-BR",
                    },
                    timeout.Token);
                Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
            }

            await using var restarted = await LauncherApplication.StartAsync(options, timeout.Token);
            Assert.Equal("personal-session", restarted.Readiness.Mode);
            Assert.Contains(
                "GET /api/v1/projects=200 JSON",
                restarted.Readiness.VerifiedEndpoints);
            Assert.Contains(
                "GET /api/v1/audit-events=200 JSON",
                restarted.Readiness.VerifiedEndpoints);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed record HealthPayload(string Status);
}
