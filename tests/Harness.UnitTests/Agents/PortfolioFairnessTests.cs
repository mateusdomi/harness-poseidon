using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Dual Project Gate, Parte P — justiça de portfólio SEM scheduler novo: o eval determinístico
/// exercita o mecanismo REAL (ordem de projetos por menor atendimento em
/// <see cref="ChiefBacklogLoopService.OrderProjectsForDispatch"/> + plano por projeto em
/// <see cref="ChiefBacklogPolicy"/>) e prova que um backlog gigante num projeto NÃO deixa o
/// outro parado, preservando papel (actor ≠ critic ≠ chief) e concorrência por conta.
/// </summary>
public sealed class PortfolioFairnessTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private const string ProjectA = "01ARZ3NDEKTSV4RRFFQ69G5PA1";
    private const string ProjectB = "01ARZ3NDEKTSV4RRFFQ69G5PB1";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"harness-portfolio-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private AccountAvailabilityLedger Ledger() =>
        new(Path.Combine(_dir, "avail.json"));

    private static AgentAccountRegistry Registry()
    {
        var registry = new AgentAccountRegistry();
        foreach (var (alias, role, concurrency) in new[]
        {
            ("worker-glm-general", AgentRoles.BackendSpecialist, 2),
            ("worker-codex-backend", AgentRoles.BackendSpecialist, 2),
            ("critic-claude", AgentRoles.Critic, 2),
            ("chief-claude-primary", AgentRoles.ChiefOrchestrator, 1),
        })
        {
            registry.Register(new AgentAccountContract(
                alias, "provider", ExecutorCatalog.Glm, $"keychain://poseidon/{alias}",
                $"confighome://{alias}", [role], [.. AgentRoles.PathScopesFor(role)],
                AgentAccountState.Available, AgentAccountHealth.Healthy, concurrency, 0,
                null, null, null, null, null, 100));
        }

        return registry;
    }

    private static ProjectRecord Project(string id, DateTimeOffset lastActivity) =>
        new("t", id, "org", $"Projeto {id[^3..]}", id[^3..], "Projeto do eval de fairness",
            "active", "critical", null, "local", "main", [], new ProjectBrandRecord(null, null, null, null), [],
            1, "chief", "autonomous", Now.AddDays(-3), lastActivity, 1);

    private static ChiefCard Card(string project, int index) =>
        new($"{project[^3..]}-card-{index:D3}", project, AgentRoles.BackendSpecialist,
            "code", 50, []);

    /// <summary>
    /// O cenário da missão: A com 50 cards READY, B com 10, mesma prioridade, mesma criticidade.
    /// Janela limitada por rodada. Depois de poucas rodadas, AMBOS receberam trabalho — e B
    /// recebe já na SEGUNDA rodada, porque a ordem por menor atendimento inverte a fila assim
    /// que A é servido. Nenhum projeto monopoliza os slots indefinidamente.
    /// </summary>
    [Fact]
    public void CinquentaContraDezNenhumProjetoMonopolizaOsSlots()
    {
        var registry = Registry();
        var ledger = Ledger();
        var policy = new ChiefBacklogPolicy();
        var backlogA = Enumerable.Range(1, 50).Select(i => Card(ProjectA, i)).ToList();
        var backlogB = Enumerable.Range(1, 10).Select(i => Card(ProjectB, i)).ToList();
        var dispatchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var served = new Dictionary<string, int> { [ProjectA] = 0, [ProjectB] = 0 };
        var firstServedRound = new Dictionary<string, int>();

        for (var round = 1; round <= 6; round++)
        {
            var ordered = ChiefBacklogLoopService.OrderProjectsForDispatch(
                [Project(ProjectA, Now.AddMinutes(-1)), Project(ProjectB, Now.AddMinutes(-2))],
                dispatchCounts);
            foreach (var project in ordered)
            {
                var backlog = project.Id == ProjectA ? backlogA : backlogB;
                if (backlog.Count == 0)
                {
                    continue;
                }

                // Janela limitada por rodada — o mesmo teto do loop real (AutoDispatchMaxConcurrent).
                var plan = policy.Plan(
                    [.. backlog.Take(8)], registry, ledger, maxConcurrentDispatch: 4, Now);
                foreach (var dispatch in plan.Dispatch)
                {
                    backlog.RemoveAll(card => card.TaskId == dispatch.Card.TaskId);
                    dispatchCounts[project.Id] = dispatchCounts.GetValueOrDefault(project.Id) + 1;
                    served[project.Id]++;
                    firstServedRound.TryAdd(project.Id, round);

                    // Papel preservado: card de backend NUNCA consome a conta do critic nem a
                    // do Chefe — a elegibilidade do scheduler é por papel, não por vaga.
                    Assert.NotEqual("critic-claude", dispatch.AccountAlias);
                    Assert.NotEqual("chief-claude-primary", dispatch.AccountAlias);
                }
            }
        }

        Assert.True(served[ProjectA] > 0, "A não recebeu trabalho.");
        Assert.True(served[ProjectB] > 0, "B não recebeu trabalho — starvation.");
        Assert.True(
            firstServedRound[ProjectB] <= 2,
            $"B só foi servido na rodada {firstServedRound[ProjectB]} — a ordem por menor atendimento não valeu.");
        // A (5x mais backlog) recebe mais no total, mas B nunca fica a mais de uma rodada de
        // distância — nenhum monopólio.
        Assert.True(served[ProjectA] > served[ProjectB]);
    }

    /// <summary>
    /// A ordem por menor atendimento é determinística e comprovável isolada: quem menos
    /// despachou vai primeiro; empate resolve por atividade recente e id — nunca por acaso.
    /// </summary>
    [Fact]
    public void AOrdemDeDespachoComecaSemprePeloProjetoMenosAtendido()
    {
        var projects = new[]
        {
            Project(ProjectA, Now.AddMinutes(-1)),
            Project(ProjectB, Now.AddMinutes(-2)),
        };

        var fresh = ChiefBacklogLoopService.OrderProjectsForDispatch(
            projects, new Dictionary<string, int>());
        // Empate (0 a 0): atividade mais recente primeiro — A.
        Assert.Equal(ProjectA, fresh[0].Id);

        var afterServingA = ChiefBacklogLoopService.OrderProjectsForDispatch(
            projects, new Dictionary<string, int> { [ProjectA] = 7 });
        Assert.Equal(ProjectB, afterServingA[0].Id);
    }
}
