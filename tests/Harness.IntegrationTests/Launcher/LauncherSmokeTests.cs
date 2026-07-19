using System.Net.Http.Json;
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
            Assert.True(File.Exists(Path.Combine(root, "harness.db")));
            Assert.True(File.Exists(Path.Combine(root, DesktopLifecycleManager.ProcessLeaseFileName)));
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

    private sealed record HealthPayload(string Status);
}
