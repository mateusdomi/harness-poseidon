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

        // C3: os eventos do golden path publicam payload, não só o nome. Cada schema é um
        // objeto com campos obrigatórios declarados, para o frontend tipar sem inventar campos.
        var payloads = document.RootElement.GetProperty("payloads");
        string[] goldenPathEvents =
        [
            "readiness.changed", "message.received", "turn.registered", "execution.enqueued",
            "execution.blocked", "provider.invoked", "model.responded", "chief.turnStateChanged",
        ];
        foreach (var eventType in goldenPathEvents)
        {
            Assert.Contains(eventType, publishedEvents);
            var schema = payloads.GetProperty(eventType);
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.NotEmpty(schema.GetProperty("required").EnumerateArray());
            Assert.NotEmpty(schema.GetProperty("properties").EnumerateObject());
        }

        // Todo payload publicado precisa existir no catálogo tipado.
        Assert.All(
            payloads.EnumerateObject().Select(property => property.Name),
            name => Assert.Contains(name, publishedEvents));
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
