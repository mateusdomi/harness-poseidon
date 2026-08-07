using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Persistence.Abstractions.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Achado da recuperação de throughput da Fase 5 (2026-08): a rota que uma persona nova herda ao
/// nascer nunca olhava o papel — só pegava o primeiro agente executável do projeto, ordenado por
/// estado e depois por Id. Como os primeiros agentes de cada projeto (fases 1-4) rodam sob a conta
/// do Chief, toda persona de código nova herdava a mesma conta por acidente de ordenação, mesmo com
/// outras contas write-capable e com quota disponíveis para o papel certo — a fleet nominal de N
/// contas virava fleet efetiva de 1 por bug, não só por falta de cota real.
/// </summary>
public sealed class ChiefTeamRouteSelectionTests
{
    private static readonly AgentMetricsRecord Metrics = new(0, 0, 0, 0m, 0);

    private static AgentRecord Agent(string id, string accountId, string state = "idle") =>
        new("tenant", id, "definition", "project", "Agente", state, null, "claude-opus-4",
            null, Metrics, null, accountId, "medium", "medium");

    private static ProposedPersona Backend(string key = "backend-specialist-x") => new(
        key, "Especialista Backend", "Implementar regras de negócio no backend.",
        "Backend", ["Implementar endpoints"], ["Não altera governança"],
        ["backend.write"], ["medium"]);

    private static ProposedPersona Frontend(string key = "frontend-specialist-x") => new(
        key, "Especialista Frontend", "Implementar telas de frontend.",
        "Frontend", ["Implementar componentes"], ["Não altera governança"],
        ["frontend.write"], ["medium"]);

    // ---- InferAccountRole -------------------------------------------------------------

    [Theory]
    [InlineData("Especialista Frontend", "interfaces web")]
    [InlineData("Especialista Mobile", "aplicativos mobile")]
    [InlineData("Especialista em Acessibilidade", "navegação por teclado")]
    public void PersonasDeInterfaceInferemOPapelFrontend(string name, string purpose)
    {
        var persona = Frontend() with { Name = name, Purpose = purpose };

        Assert.Equal(AgentRoles.FrontendSpecialist, ChiefTeamManager.InferAccountRole(persona));
    }

    [Theory]
    [InlineData("Especialista DevOps", "infraestrutura e observabilidade")]
    [InlineData("Especialista em Banco de Dados", "modelagem Oracle")]
    [InlineData("Especialista Backend", "regras de negócio")]
    public void PersonasQueNaoSaoDeInterfaceCaemEmBackend(string name, string purpose)
    {
        var persona = Backend() with { Name = name, Purpose = purpose };

        Assert.Equal(AgentRoles.BackendSpecialist, ChiefTeamManager.InferAccountRole(persona));
    }

    // ---- SelectRouteCore ---------------------------------------------------------------

    /// <summary>
    /// O caso que a fleet efetiva = 1 produzia: o primeiro agente do projeto (conta do Chief,
    /// sem o papel `backend-specialist`) vencia por ordem de Id sobre uma conta que de fato
    /// aceita o papel. Agora a conta com o papel certo vence, mesmo entrando depois.
    /// </summary>
    [Fact]
    public void UmaPersonaDeBackendHerdaAContaQueAceitaOPapelBackendNaoAPrimeiraPorId()
    {
        var chiefAgent = Agent("01-chief-agent", "chief-claude-primary");
        var backendAgent = Agent("02-backend-agent", "worker-claude-secondary");
        var projectAgents = new[] { chiefAgent, backendAgent };

        var (route, matchedRole) = ChiefTeamManager.SelectRouteCore(
            projectAgents,
            AgentRoles.BackendSpecialist,
            agent => string.Equals(agent.AccountId, "worker-claude-secondary", StringComparison.Ordinal));

        Assert.True(matchedRole);
        Assert.Equal("worker-claude-secondary", route!.AccountId);
    }

    /// <summary>Papel frontend segue o mesmo caminho — não é especial, é o mesmo predicado.</summary>
    [Fact]
    public void UmaPersonaDeFrontendHerdaAContaQueAceitaOPapelFrontend()
    {
        var chiefAgent = Agent("01-chief-agent", "chief-claude-primary");
        var frontendAgent = Agent("02-frontend-agent", "worker-codex-frontend");
        var projectAgents = new[] { chiefAgent, frontendAgent };

        var (route, matchedRole) = ChiefTeamManager.SelectRouteCore(
            projectAgents,
            AgentRoles.FrontendSpecialist,
            agent => string.Equals(agent.AccountId, "worker-codex-frontend", StringComparison.Ordinal));

        Assert.True(matchedRole);
        Assert.Equal("worker-codex-frontend", route!.AccountId);
    }

    /// <summary>
    /// Nenhuma conta do projeto declara o papel certo: cair para a mais disponível é melhor que
    /// recusar a persona inteira — mas o chamador sabe que foi fallback (matchedRole=false) e
    /// pode registrar o motivo, em vez de mascarar a rota como ideal.
    /// </summary>
    [Fact]
    public void SemContaComOPapelCertoCaiParaAMaisDisponivelEAvisaQueFoiFallback()
    {
        var only = Agent("01-only-agent", "chief-claude-primary");

        var (route, matchedRole) = ChiefTeamManager.SelectRouteCore(
            [only], AgentRoles.BackendSpecialist, _ => false);

        Assert.False(matchedRole);
        Assert.Equal("chief-claude-primary", route!.AccountId);
    }

    /// <summary>Agente sem rota executável (sem conta/modelo/effort) nunca é escolhido.</summary>
    [Fact]
    public void AgenteSemRotaExecutavelNuncaEhEscolhido()
    {
        var incomplete = new AgentRecord(
            "tenant", "01-incomplete", "definition", "project", "Agente", "idle", null, null,
            null, Metrics, null, null, null, null);
        var complete = Agent("02-complete", "worker-claude-secondary");

        var (route, _) = ChiefTeamManager.SelectRouteCore(
            [incomplete, complete], AgentRoles.BackendSpecialist, _ => true);

        Assert.Equal("worker-claude-secondary", route!.AccountId);
    }

    /// <summary>
    /// Entre duas contas com o papel certo, o estado ativo/idle/waiting ainda desempata primeiro
    /// — o fix não jogou fora a preferência por disponibilidade, só a subordinou ao papel.
    /// </summary>
    [Fact]
    public void EntreContasComOPapelCertoAMaisDisponivelDesempata()
    {
        var retiredButEligible = Agent("01-retired-like", "worker-a", state: "waiting");
        var idleEligible = Agent("02-idle", "worker-b", state: "idle");

        var (route, matchedRole) = ChiefTeamManager.SelectRouteCore(
            [retiredButEligible, idleEligible], AgentRoles.BackendSpecialist, _ => true);

        Assert.True(matchedRole);
        // Ambos "active-like" (idle/waiting contam igual) — o Id decide, e "01" vem antes de "02".
        Assert.Equal("worker-a", route!.AccountId);
    }
}
