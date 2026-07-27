using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Quem responde a uma pergunta de agente. A regra de produto: escolher padrão técnico, dividir
/// classe, criar migration, trocar de conta ou refazer teste é trabalho da chefe — levar isso ao
/// dono o transforma em gargalo de uma fábrica que existe para não depender dele. Sobe só o que o
/// sistema genuinamente não pode resolver.
/// </summary>
public sealed class AgentRequestPolicyTests
{
    [Theory]
    [InlineData("needs_decision", AgentRequestKind.NeedsDecision)]
    [InlineData("needs_clarification", AgentRequestKind.NeedsClarification)]
    [InlineData("blocked_by_dependency", AgentRequestKind.BlockedByDependency)]
    [InlineData("scope_expansion", AgentRequestKind.ScopeExpansion)]
    [InlineData("external_resource", AgentRequestKind.ExternalResource)]
    [InlineData("canonical_conflict", AgentRequestKind.CanonicalConflict)]
    public void TheKindIsAClosedSetAndRoundTrips(string wire, AgentRequestKind expected)
    {
        Assert.Equal(expected, AgentRequestPolicy.ParseKind(wire));
        Assert.Equal(wire, AgentRequestPolicy.Serialize(expected));
    }

    [Fact]
    public void AnUnknownKindStaysWithTheChief()
    {
        // Escalar por dúvida treina o dono a ignorar o canal.
        Assert.Equal(AgentRequestKind.NeedsDecision, AgentRequestPolicy.ParseKind("inventado"));
        Assert.Equal(
            AgentRequestRouting.ChiefResolves,
            AgentRequestPolicy.Route(AgentRequestPolicy.ParseKind("inventado"), "qualquer coisa"));
    }

    [Theory]
    [InlineData("Qual padrão de repositório devo seguir?")]
    [InlineData("Divido a classe em duas ou mantenho uma?")]
    [InlineData("Crio a migration agora ou no card seguinte?")]
    [InlineData("O teste ficou vermelho por timeout; refaço?")]
    public void OperationalDecisionsNeverReachTheOwner(string question)
    {
        Assert.Equal(
            AgentRequestRouting.ChiefResolves,
            AgentRequestPolicy.Route(AgentRequestKind.NeedsDecision, question));
    }

    [Theory]
    [InlineData("Preciso de uma assinatura paga do serviço de e-mail.")]
    [InlineData("Falta a credencial do gateway; não tenho como obter.")]
    [InlineData("Isso tem implicação jurídica sobre retenção de dados.")]
    [InlineData("A mudança gera custo adicional mensal.")]
    public void GenuineExternalDependenciesDoReachTheOwner(string question)
    {
        Assert.Equal(
            AgentRequestRouting.EscalateToHuman,
            AgentRequestPolicy.Route(AgentRequestKind.NeedsDecision, question));
    }

    [Fact]
    public void TheTypeAloneSettlesExternalResourceAndCanonicalConflict()
    {
        // Esses dois tipos declaram a natureza no próprio tipo: são exatamente o que a chefe não
        // tem como resolver sozinha.
        Assert.Equal(
            AgentRequestRouting.EscalateToHuman,
            AgentRequestPolicy.Route(AgentRequestKind.ExternalResource, "qualquer texto"));
        Assert.Equal(
            AgentRequestRouting.EscalateToHuman,
            AgentRequestPolicy.Route(AgentRequestKind.CanonicalConflict, "qualquer texto"));
    }

    [Fact]
    public void ScopeExpansionIsAlwaysGovernanceOfWorkNeverBusiness()
    {
        Assert.Equal(
            AgentRequestRouting.ChiefResolves,
            AgentRequestPolicy.Route(
                AgentRequestKind.ScopeExpansion, "preciso tocar o módulo vizinho, tem custo?"));
    }

    [Fact]
    public void ADependencyBlockedByAnotherCardIsOperational()
    {
        // Bloqueada por credencial é externa; bloqueada por outro card, não.
        Assert.Equal(
            AgentRequestRouting.ChiefResolves,
            AgentRequestPolicy.Route(
                AgentRequestKind.BlockedByDependency, "O card AUT-01 ainda não entregou o contrato."));
        Assert.Equal(
            AgentRequestRouting.EscalateToHuman,
            AgentRequestPolicy.Route(
                AgentRequestKind.BlockedByDependency, "Falta a credencial do provedor externo."));
    }
}
