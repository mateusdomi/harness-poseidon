using Harness.Host.Leadership;

namespace Harness.UnitTests.Leadership;

public sealed class LeadershipProfileStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"poseidon-leadership-{Guid.NewGuid():N}");

    [Fact]
    public async Task UpdateIsPersistentVersionedAndOptimisticallyLocked()
    {
        using var firstStore = new LeadershipProfileStore(_directory);
        var initial = await firstStore.ReadAsync();
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var updated = await firstStore.UpdateAsync(new(
            "Bruna Magalhães",
            "Diretora de Engenharia e Operações de IA",
            "Resumo atualizado.",
            ["Engenharia", "Operações de IA"],
            "Histórico.",
            ["Português (Brasil)", "Inglês"],
            "Direta e transparente.",
            ["Café", "Leitura"],
            27,
            "Chame Mateus pelo nome e use um tom profissional e leve.",
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            initial.Version),
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            now);

        Assert.Equal(initial.Version + 1, updated.Version);
        Assert.Single(updated.History);
        Assert.Contains(updated.History[0].Changes, change =>
            change.StartsWith("Camada de comunicação", StringComparison.Ordinal));

        using var secondStore = new LeadershipProfileStore(_directory);
        var persisted = await secondStore.ReadAsync();
        Assert.Equivalent(updated, persisted, strict: true);

        await Assert.ThrowsAsync<LeadershipProfileConflictException>(() =>
            secondStore.UpdateAsync(new(
                updated.DisplayName, updated.Title, updated.Summary, updated.Specialties,
                updated.CareerSummary, updated.Languages, updated.Personality, updated.Hobbies,
                updated.Age, updated.CommunicationInstructions, updated.PreferredModelId,
                updated.PreferredAccountId, initial.Version),
                "01ARZ3NDEKTSV4RRFFQ69G5FAX",
                now.AddMinutes(1)));
    }

    [Fact]
    public async Task AgentPhotoIsManagedPerSafeAliasAndFormatReplacementIsIdempotent()
    {
        using var store = new LeadershipProfileStore(_directory);
        await using (var first = new MemoryStream([1, 2, 3]))
            await store.SaveAgentPhotoAsync("worker-kimi-ui", first, ".png");

        Assert.EndsWith(".png", store.ResolveAgentPhotoPath("worker-kimi-ui"));

        await using (var replacement = new MemoryStream([4, 5]))
            await store.SaveAgentPhotoAsync("worker-kimi-ui", replacement, ".jpg");

        var path = store.ResolveAgentPhotoPath("worker-kimi-ui");
        Assert.EndsWith(".jpg", path);
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(path!));
        Assert.Throws<LeadershipProfileValidationException>(() =>
            store.ResolveAgentPhotoPath("../fora"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
