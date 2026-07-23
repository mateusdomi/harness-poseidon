using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// CAT-02: as definições canônicas built-in devem ganhar, na semeadura, os ~14 campos de
/// persona totalmente preenchidos e o owner de sistema — de forma idempotente (reexecutar não
/// duplica nem sobrescreve) e sem perturbar o comportamento existente do catálogo.
/// </summary>
public sealed class BuiltInAgentDefinitionSeedTests
{
    private static readonly string[] CanonicalKeys =
    [
        "chief-orchestrator", "critic-qa", "product-requirements-analyst",
        "software-architect", "software-engineer", "technical-writer",
        // DEL-08: as personas de Delivery ("sob demanda").
        "delivery-tech-lead-copilot", "delivery-daily-intelligence",
        "delivery-risk-dependency-analyst", "delivery-forecast-analyst",
        "delivery-quality-release-auditor", "delivery-documentation-steward",
        "delivery-executive-reporting", "delivery-benefits-analyst",
        // ARC-09: as personas de Architecture ("sob demanda").
        "architecture-chief", "architecture-discovery",
        "architecture-solution-architect", "architecture-enterprise",
        "architecture-integration", "architecture-data",
        "architecture-security", "architecture-infrastructure",
        "architecture-rationalization-analyst", "architecture-critic",
        "architecture-adr-writer",
    ];

    // As personas que são crítico/auditor (ActorCritic == "critic"); as demais são "actor".
    private static readonly string[] CriticKeys =
    [
        "critic-qa", "delivery-quality-release-auditor", "architecture-critic",
    ];

    private static readonly string[] ValidEfforts = ["low", "medium", "high", "max"];
    private static readonly string[] ValidRisks = ["low", "medium", "high"];

    [Fact]
    public async Task SeedsEveryCanonicalPersonaWithCompleteContentAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"builtin-agent-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "harness.db");

        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var store = new SqliteAgentCatalogStore(dispatcher);
            var seeder = new BuiltInAgentDefinitionSeeder(store);

            // Antes do seed: as seis definições existem, mas o conteúdo de persona e o owner
            // vêm vazios (regressão que CAT-02 corrige).
            var before = await store.ListDefinitionsAsync(null, 50, timeout.Token);
            Assert.Equal(
                CanonicalKeys.Order(StringComparer.Ordinal),
                before.Select(definition => definition.Key).Order(StringComparer.Ordinal));
            Assert.All(before, definition =>
            {
                Assert.Null(definition.Owner);
                Assert.True(string.IsNullOrEmpty(definition.Persona));
                Assert.True(string.IsNullOrEmpty(definition.Mission));
            });

            // Primeira semeadura: preenche exatamente as seis personas.
            Assert.Equal(CanonicalKeys.Length, await seeder.EnsureSeededAsync(timeout.Token));

            var seeded = await store.ListDefinitionsAsync(null, 50, timeout.Token);
            Assert.Equal(
                CanonicalKeys.Order(StringComparer.Ordinal),
                seeded.Select(definition => definition.Key).Order(StringComparer.Ordinal));
            Assert.All(seeded, AssertFullyPopulated);

            // Os auditores/críticos têm ActorCritic "critic"; os demais são "actor" — prova das defaults por papel.
            Assert.All(
                seeded.Where(definition => CriticKeys.Contains(definition.Key)),
                definition => Assert.Equal("critic", definition.ActorCritic));
            Assert.All(
                seeded.Where(definition => !CriticKeys.Contains(definition.Key)),
                definition => Assert.Equal("actor", definition.ActorCritic));

            // Idempotência: reexecutar não semeia nada e não duplica linhas nem altera conteúdo.
            Assert.Equal(0, await seeder.EnsureSeededAsync(timeout.Token));
            var afterReseed = await store.ListDefinitionsAsync(null, 50, timeout.Token);
            Assert.Equal(CanonicalKeys.Length, afterReseed.Count);
            Assert.All(afterReseed, AssertFullyPopulated);
            var chiefBefore = seeded.Single(definition => definition.Key == "chief-orchestrator");
            var chiefAfter = afterReseed.Single(definition => definition.Key == "chief-orchestrator");
            Assert.Equal(chiefBefore.Persona, chiefAfter.Persona);
            Assert.Equal(chiefBefore.Mission, chiefAfter.Mission);
            Assert.Equal(chiefBefore.Version, chiefAfter.Version);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void AssertFullyPopulated(AgentDefinitionRecord definition)
    {
        Assert.Equal(CanonicalAgentDefinitions.SystemOwner, definition.Owner);
        Assert.False(string.IsNullOrWhiteSpace(definition.Persona));
        Assert.False(string.IsNullOrWhiteSpace(definition.Mission));
        Assert.False(string.IsNullOrWhiteSpace(definition.CommunicationStyle));
        Assert.False(string.IsNullOrWhiteSpace(definition.DefaultEffort));
        Assert.False(string.IsNullOrWhiteSpace(definition.Team));
        Assert.False(string.IsNullOrWhiteSpace(definition.ActorCritic));
        Assert.False(string.IsNullOrWhiteSpace(definition.Risk));
        Assert.NotEmpty(definition.OperatingPrinciples ?? []);
        Assert.NotEmpty(definition.Deliverables ?? []);
        Assert.NotEmpty(definition.QualityCriteria ?? []);
        Assert.NotEmpty(definition.Limitations ?? []);
        Assert.NotEmpty(definition.Stacks ?? []);
        Assert.Contains(definition.DefaultEffort, ValidEfforts);
        Assert.Contains(definition.Risk, ValidRisks);
    }
}
