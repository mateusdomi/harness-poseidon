using System.Net;
using System.Text.RegularExpressions;
using Harness.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Frontend;

public sealed partial class EmbeddedFrontendApiTests
{
    [Fact]
    public async Task HostServesHttpModeBundleAndSpaFallbackWithoutMaskingApi404()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"frontend-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "frontend.db"); Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(app.Services) };
                var index = await client.GetStringAsync("/", timeout.Token);
                Assert.Contains("<div id=\"root\"></div>", index, StringComparison.Ordinal);
                Assert.Equal(index, await client.GetStringAsync("/settings", timeout.Token));
                var assetPath = ScriptSource().Match(index).Groups[1].Value;
                Assert.StartsWith("/assets/", assetPath, StringComparison.Ordinal);
                using var assetResponse = await client.GetAsync(assetPath, timeout.Token);
                assetResponse.EnsureSuccessStatusCode();
                var javascript = await assetResponse.Content.ReadAsStringAsync(timeout.Token);
                Assert.DoesNotMatch(AbsoluteApiDefault(), javascript);
                using var favicon = await client.GetAsync("/favicon.ico", timeout.Token);
                favicon.EnsureSuccessStatusCode();
                Assert.Equal("image/png", favicon.Content.Headers.ContentType?.MediaType);
                var faviconBytes = await favicon.Content.ReadAsByteArrayAsync(timeout.Token);
                Assert.True(faviconBytes.Length > 8);
                Assert.Equal(
                    new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                    faviconBytes[..4]);
                using var missingApi = await client.GetAsync("/api/v1/does-not-exist", timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
                Assert.NotEqual("text/html", missingApi.Content.Headers.ContentType?.MediaType);
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    [GeneratedRegex("<script[^>]+src=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptSource();

    [GeneratedRegex("const [A-Za-z_$][A-Za-z0-9_$]*=\"http://localhost:5001\";return\\{api:", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteApiDefault();
}
