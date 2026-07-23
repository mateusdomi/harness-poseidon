using Harness.Persistence.Abstractions.Agents;

namespace Harness.Host.Agents;

/// <summary>
/// CAT-02: semeia, de forma idempotente, o conteúdo canônico completo das definições built-in
/// (personas de sistema). Delega ao store, que só preenche linhas ainda não semeadas
/// (owner IS NULL), de modo que reexecutar não duplica nem sobrescreve.
/// </summary>
public sealed class BuiltInAgentDefinitionSeeder(IAgentCatalogStore store)
{
    private readonly IAgentCatalogStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public Task<int> EnsureSeededAsync(CancellationToken cancellationToken) =>
        _store.EnsureBuiltInDefinitionsAsync(CanonicalAgentDefinitions.All, cancellationToken);
}
