using System.Text.Json;
using Harness.Host;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.ContractTests.Realtime;

public sealed class OpenApiContractTests
{
    [Fact]
    public async Task PublishedOpenApiMatchesRunningHost()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var app = HostApplication.Build(["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync(timeout.Token);

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("Kestrel address was not published.");
            var address = new Uri(addresses.Single(item =>
                item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
            using var client = new HttpClient { BaseAddress = address };
            var generatedJson = await client.GetStringAsync("/openapi/v1.json", timeout.Token);
            var publishedJson = await File.ReadAllTextAsync(
                Path.Combine(FindRepositoryRoot(), "docs", "contracts", "openapi.json"),
                timeout.Token);

            using var generated = JsonDocument.Parse(generatedJson);
            using var published = JsonDocument.Parse(publishedJson);
            Assert.Equal(
                JsonSerializer.Serialize(generated.RootElement),
                JsonSerializer.Serialize(published.RootElement));
        }
        finally
        {
            await app.StopAsync(timeout.Token);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
