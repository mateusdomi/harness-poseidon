using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Governance.Coordination;

namespace Harness.UnitTests.Governance;

/// <summary>
/// Medido ao vivo: quatro cards de backend independentes do mesmo projeto reivindicavam `src/**`
/// cada um, três eram recusados por conflito de escopo a cada ciclo, e o paralelismo real do
/// produto era de UM card por papel por projeto. A política já aceitava claims estreitos; faltava
/// alguém capaz de decidir quais.
/// </summary>
public sealed class CardPathScopePlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-surface-{Guid.NewGuid():N}");

    public CardPathScopePlannerTests()
    {
        // Estrutura real do repositório, criada em disco: o mapa é LIDO, não adivinhado.
        Directory.CreateDirectory(Path.Combine(_root, "src", "Modules", "Harness.Modules.Agents"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Modules", "Harness.Modules.Workflows"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Modules", "Harness.Modules.Providers"));
        Directory.CreateDirectory(Path.Combine(_root, "tests", "Harness.UnitTests", "Agents"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Harness.Persistence.Sqlite", "Migrations"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Harness.Persistence.Postgres", "Migrations"));
        Directory.CreateDirectory(Path.Combine(_root, "frontend", "src", "features", "approvals"));
        Directory.CreateDirectory(Path.Combine(_root, "frontend", "src", "services", "approvals"));
    }

    private RepositorySurfaceMap Map() => RepositorySurfaceMap.Build(_root);

    private static IReadOnlyList<string> Backend => AgentRoles.PathScopesFor(AgentRoles.BackendSpecialist);
    private static IReadOnlyList<string> Frontend => AgentRoles.PathScopesFor(AgentRoles.FrontendSpecialist);

    [Fact]
    public void ACardThatNamesItsModuleClaimsOnlyThatModule()
    {
        var plan = CardPathScopePlanner.Plan(
            Backend, Map(), "Backend: fila de despacho",
            "Ajustar o scheduler em Harness.Modules.Agents.");

        Assert.True(plan.Narrowed);
        Assert.Contains("src/Modules/Harness.Modules.Agents/**", plan.Claims);
        Assert.DoesNotContain("src/**", plan.Claims);
    }

    [Fact]
    public void TheModuleTestProjectTravelsWithTheModule()
    {
        // Alterar um módulo sem poder tocar no teste dele produziria card incapaz de provar o
        // próprio trabalho.
        var plan = CardPathScopePlanner.Plan(
            Backend, Map(), "Backend", "Corrigir Harness.Modules.Agents e cobrir com teste.");

        Assert.Contains("tests/Harness.UnitTests/Agents/**", plan.Claims);
    }

    [Fact]
    public void FourCardsInDifferentModulesDoNotOverlap()
    {
        // O cenário que o defeito impedia: paralelismo real entre cards independentes.
        var agents = CardPathScopePlanner.Plan(
            Backend, Map(), "A", "Mexer em Harness.Modules.Agents.").Claims;
        var workflows = CardPathScopePlanner.Plan(
            Backend, Map(), "B", "Mexer em Harness.Modules.Workflows.").Claims;
        var providers = CardPathScopePlanner.Plan(
            Backend, Map(), "C", "Mexer em Harness.Modules.Providers.").Claims;
        var migration = CardPathScopePlanner.Plan(
            Backend, Map(), "D", "Criar a migration dual.").Claims;

        foreach (var (left, right) in new[]
        {
            (agents, workflows), (agents, providers), (agents, migration),
            (workflows, providers), (workflows, migration), (providers, migration),
        })
        {
            Assert.DoesNotContain(
                left.SelectMany(l => right.Select(r => (l, r))),
                pair => AttemptWorkspaceScopeOverlap(pair.l, pair.r));
        }
    }

    [Fact]
    public void TwoCardsInTheSameModuleStillCollide()
    {
        // Estreitar não pode virar desculpa para deixar passar conflito real.
        var first = CardPathScopePlanner.Plan(
            Backend, Map(), "A", "Mexer em Harness.Modules.Agents.").Claims;
        var second = CardPathScopePlanner.Plan(
            Backend, Map(), "B", "Também mexer em Harness.Modules.Agents.").Claims;

        Assert.Contains(
            first.SelectMany(l => second.Select(r => (l, r))),
            pair => AttemptWorkspaceScopeOverlap(pair.l, pair.r));
    }

    [Fact]
    public void AMigrationCardClaimsBothProvidersBecauseParityIsMandatory()
    {
        var plan = CardPathScopePlanner.Plan(
            Backend, Map(), "Persistência", "Adicionar a migration de obrigações.");

        Assert.True(plan.Narrowed);
        Assert.Contains("src/Harness.Persistence.Sqlite/Migrations/**", plan.Claims);
        Assert.Contains("src/Harness.Persistence.Postgres/Migrations/**", plan.Claims);
    }

    [Fact]
    public void AFrontendCardClaimsTheFeatureAndItsService()
    {
        var plan = CardPathScopePlanner.Plan(
            Frontend, Map(), "Tela", "Ajustar a central de approvals.");

        Assert.True(plan.Narrowed);
        Assert.Contains("frontend/src/features/approvals/**", plan.Claims);
        Assert.Contains("frontend/src/services/approvals/**", plan.Claims);
    }

    [Fact]
    public void WithoutARecognizedSurfaceTheRoleScopeIsKept()
    {
        // Conservador por construção: claim estreito demais trava o agente no meio do trabalho,
        // o que é pior do que um claim amplo que apenas serializa.
        var plan = CardPathScopePlanner.Plan(
            Backend, Map(), "Trabalho genérico", "Melhorar o sistema.");

        Assert.False(plan.Narrowed);
        Assert.Equal(Backend, plan.Claims);
        Assert.Equal("scope.no_surface_matched", plan.ReasonCode);
    }

    [Fact]
    public void ACardThatTouchesTooManySurfacesIsNotNarrowed()
    {
        var plan = CardPathScopePlanner.Plan(
            Backend, Map(),
            "Refatoração ampla",
            "Mexer em Harness.Modules.Agents, Harness.Modules.Workflows, Harness.Modules.Providers e na migration.");

        Assert.False(plan.Narrowed);
        Assert.Equal("scope.too_many_surfaces", plan.ReasonCode);
    }

    [Fact]
    public void NarrowingNeverReachesOutsideTheRole()
    {
        // O estreitamento reduz o alcance; nunca pode servir de porta para o que o papel negava.
        var plan = CardPathScopePlanner.Plan(
            Frontend, Map(), "Tela", "Ajustar Harness.Modules.Agents.");

        Assert.False(plan.Narrowed);
        Assert.Equal(Frontend, plan.Claims);
    }

    [Fact]
    public void EveryNarrowedClaimStillPassesTheScopePolicy()
    {
        // O planejador propõe; a política decide. Nenhum claim planejado pode ser recusado por ela.
        foreach (var text in new[]
        {
            "Mexer em Harness.Modules.Agents.", "Criar a migration dual.", "Ajustar o infra.",
        })
        {
            var plan = CardPathScopePlanner.Plan(Backend, Map(), "card", text);
            var decision = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Backend, plan.Claims);
            Assert.True(decision.Allowed, $"'{text}' produziu claim recusado: {string.Join(",", decision.RejectedClaims)}");
        }
    }

    [Fact]
    public void AnEmptySurfaceMapNeverNarrows()
    {
        var plan = CardPathScopePlanner.Plan(
            Backend, RepositorySurfaceMap.Empty, "A", "Mexer em Harness.Modules.Agents.");

        Assert.False(plan.Narrowed);
        Assert.Equal(Backend, plan.Claims);
    }

    /// <summary>Sobreposição de padrões no mesmo critério usado pelo store de claims.</summary>
    private static bool AttemptWorkspaceScopeOverlap(string left, string right)
    {
        var l = left.TrimEnd('*').TrimEnd('/');
        var r = right.TrimEnd('*').TrimEnd('/');
        return l.Equals(r, StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith($"{r}/", StringComparison.OrdinalIgnoreCase) ||
            r.StartsWith($"{l}/", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
