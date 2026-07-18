using System.Text.Json;
using Harness.Host.Realtime;

namespace Harness.ContractTests.Realtime;

public sealed class EventCatalogContractTests
{
    [Fact]
    public async Task PublishedEventArtifactMatchesTypedCatalog()
    {
        var repositoryRoot = FindRepositoryRoot();
        await using var stream = File.OpenRead(Path.Combine(repositoryRoot, "docs", "contracts", "events.json"));
        using var document = await JsonDocument.ParseAsync(stream);
        var publishedEvents = document.RootElement
            .GetProperty("events")
            .EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(EventTypeCatalog.All, publishedEvents);
        Assert.Equal("/hubs/events", document.RootElement.GetProperty("hub").GetString());
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
