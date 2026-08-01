using Harness.Modules.Agents.Application.Execution;

namespace Harness.UnitTests.Agents;

/// <summary>
/// B14 (Fase 2B) — o despacho por intenção é determinístico e a rota governa a ação.
///
/// O turno da chefe era o laço mais quente do produto e inteiramente livre: o modelo lia a
/// mensagem e decidia, no mesmo fôlego, o que responder E o que fazer. Duas mensagens equivalentes
/// podiam render rotas diferentes, porque nada além do juízo do modelo definia a rota.
/// </summary>
public sealed class ChiefIntentDispatchTests
{
    [Fact]
    public void TheSameIntentAlwaysYieldsTheSameRoute()
    {
        // O ponto do B14: a rota é código, e código repete. Se isto falhar, a taxonomia virou
        // decoração sobre um comportamento que continua imprevisível.
        foreach (var intent in ChiefIntentDispatchTable.All)
        {
            var first = ChiefIntentDispatchTable.For(intent);
            var second = ChiefIntentDispatchTable.For(intent);

            Assert.Equal(first, second);
            Assert.Equal(intent, first.Intent);
        }
    }

    [Fact]
    public void EveryIntentInTheTaxonomyHasARouteWithAResponseShape()
    {
        Assert.Equal(10, ChiefIntentDispatchTable.All.Count);

        foreach (var intent in ChiefIntentDispatchTable.All)
        {
            var route = ChiefIntentDispatchTable.For(intent);
            Assert.False(
                string.IsNullOrWhiteSpace(route.ResponseShape),
                $"A intenção '{ChiefIntentDispatchTable.Name(intent)}' não diz como a resposta deve ser moldada.");
        }
    }

    [Fact]
    public void TheNameOfEveryIntentRoundTrips()
    {
        // O nome vai para o ledger e para o painel: se `Parse(Name(x)) != x` a série temporal
        // fica com duas etiquetas para a mesma coisa e a medição perde sentido.
        foreach (var intent in ChiefIntentDispatchTable.All)
        {
            Assert.Equal(intent, ChiefIntentDispatchTable.Parse(ChiefIntentDispatchTable.Name(intent)));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("planejar")]          // quase certo
    [InlineData("PLANEJAR_DEMANDAS")] // plural
    [InlineData("outra_coisa")]
    public void AnythingOutsideTheTaxonomyIsUnmatchedIncludingNearMisses(string? value)
    {
        // "Quase" não é uma rota. Aceitar aproximação daria permissão de agir a uma classificação
        // que o sistema não entendeu.
        Assert.Equal(ChiefTurnIntent.Unmatched, ChiefIntentDispatchTable.Parse(value));
    }

    [Theory]
    [InlineData(0.59)]
    [InlineData(0.0)]
    public void LowConfidenceFallsBackToUnmatched(double confidence)
    {
        Assert.Equal(
            ChiefTurnIntent.Unmatched,
            ChiefIntentDispatchTable.Resolve(ChiefTurnIntent.PlanejarDemanda, confidence));
    }

    [Fact]
    public void ConfidenceAtTheThresholdIsAccepted()
    {
        Assert.Equal(
            ChiefTurnIntent.PlanejarDemanda,
            ChiefIntentDispatchTable.Resolve(ChiefTurnIntent.PlanejarDemanda, 0.6));
    }

    [Fact]
    public void OnlyPlanningAndEscalationMayCreateWork()
    {
        // A fronteira que importa: criar demanda é a ação que enche o board do dono.
        var mayCreate = ChiefIntentDispatchTable.All
            .Where(intent => ChiefIntentDispatchTable.For(intent).AllowedActions
                .HasFlag(ChiefTurnAction.CreateDemands))
            .ToArray();

        Assert.Equal(
            [ChiefTurnIntent.PlanejarDemanda, ChiefTurnIntent.DecidirEscalacao],
            mayCreate.Order().ToArray());
    }

    [Fact]
    public void OnlyPlanningMayChangeTheTeam()
    {
        var mayManage = ChiefIntentDispatchTable.All
            .Where(intent => ChiefIntentDispatchTable.For(intent).AllowedActions
                .HasFlag(ChiefTurnAction.ManageTeam))
            .ToArray();

        Assert.Equal([ChiefTurnIntent.PlanejarDemanda], mayManage);
    }

    [Fact]
    public void CheapIntentsDoNotPayForContextTheyDoNotUse()
    {
        // O B14 existe para cortar custo do laço mais quente. Carregar o board inteiro para
        // responder "bom dia" é exatamente o desperdício que ele veio eliminar.
        var chat = ChiefIntentDispatchTable.For(ChiefTurnIntent.ConversaGeral);
        Assert.False(chat.RequiresBoardSnapshot);
        Assert.False(chat.RequiresPhaseState);

        var planning = ChiefIntentDispatchTable.For(ChiefTurnIntent.PlanejarDemanda);
        Assert.True(planning.RequiresBoardSnapshot);
        Assert.True(planning.RequiresPhaseState);
    }

    // ---- O portão: prova ADVERSARIAL de que a rota governa a ação ----

    [Fact]
    public void AChatTurnThatEmitsWorkHasThatWorkDiscarded()
    {
        // O teste adversarial do gate 2B: o modelo classificou `conversa_geral` e mesmo assim
        // emitiu demandas e mudanças de equipe. Sem o portão, isso entraria no board do dono como
        // trabalho que ninguém pediu.
        var output = new ChiefTurnOutput(
            "Bom dia! Tudo certo por aqui.",
            [Demand("Refazer o cadastro"), Demand("Migrar o banco")],
            [new ChiefTeamAction("create_persona", "porque sim")],
            ChiefTurnIntent.ConversaGeral,
            0.95);

        var result = ChiefIntentGate.Apply(output);

        Assert.Empty(result.Output.Demands);
        Assert.Null(result.Output.TeamActions);
        Assert.Equal(2, result.DemandsDropped);
        Assert.Equal(1, result.TeamActionsDropped);
        Assert.True(result.AnythingDropped);

        // O corte é AUDITÁVEL: um descarte silencioso pareceria, de fora, o modelo nunca ter
        // proposto nada.
        Assert.Contains("conversa_geral", result.DropReason, StringComparison.Ordinal);
        Assert.Contains("demandsDropped=2", result.DropReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResponseToTheUserIsNeverCut()
    {
        // Cortar texto deixaria o dono com meia frase e nenhuma explicação. O portão remove
        // AÇÕES, não fala.
        var output = new ChiefTurnOutput(
            "Resposta inteira ao usuário.", [Demand("x")], null, ChiefTurnIntent.ConversaGeral, 0.9);

        Assert.Equal("Resposta inteira ao usuário.", ChiefIntentGate.Apply(output).Output.Response);
    }

    [Fact]
    public void APlanningTurnKeepsItsWork()
    {
        var output = new ChiefTurnOutput(
            "Vou organizar isso.",
            [Demand("Construir a tela de login")],
            [new ChiefTeamAction("create_persona", "falta especialista de frontend")],
            ChiefTurnIntent.PlanejarDemanda,
            0.9);

        var result = ChiefIntentGate.Apply(output);

        Assert.Single(result.Output.Demands);
        Assert.NotNull(result.Output.TeamActions);
        Assert.False(result.AnythingDropped);
        Assert.Equal(string.Empty, result.DropReason);
    }

    [Fact]
    public void AnUnmatchedTurnLosesThePermissionToAct()
    {
        // Classificação incerta vira turno livre SEM ação. É o que impede um palpite do modelo de
        // virar trabalho — e o que torna honesto expandir a taxonomia com dado real depois.
        var output = new ChiefTurnOutput(
            "Não tenho certeza do que você quis dizer.",
            [Demand("chute")],
            [new ChiefTeamAction("create_persona", "chute")],
            ChiefTurnIntent.Unmatched,
            0.2);

        var result = ChiefIntentGate.Apply(output);

        Assert.Empty(result.Output.Demands);
        Assert.Null(result.Output.TeamActions);
        Assert.Equal(ChiefTurnIntent.Unmatched, result.Route.Intent);
    }

    [Fact]
    public void EscalationMayOpenWorkButMayNotReshapeTheTeam()
    {
        // Escalar pode abrir o card que destrava; mexer na equipe no meio de uma escalada seria
        // resolver um problema mudando quem trabalha, sem o dono saber.
        var output = new ChiefTurnOutput(
            "Travou no acesso ao provedor.",
            [Demand("Solicitar credencial")],
            [new ChiefTeamAction("create_persona", "tentativa")],
            ChiefTurnIntent.DecidirEscalacao,
            0.9);

        var result = ChiefIntentGate.Apply(output);

        Assert.Single(result.Output.Demands);
        Assert.Null(result.Output.TeamActions);
        Assert.Equal(1, result.TeamActionsDropped);
    }

    [Fact]
    public void TheChiefTurnContractRefusesAnUnclassifiedTurnSoTheRepairRoundHappens()
    {
        // Foi assim que o primeiro piloto real falhou: o modelo esqueceu `intent`, o parse
        // degradou em silêncio para `unmatched`, o portão descartou as demandas — e o dono
        // recebeu uma resposta simpática sem nenhum trabalho criado, sem nada explicando.
        //
        // Agora a ausência FALHA, e a falha aciona a rodada de reparo em que o modelo corrige a
        // própria saída. Pedir de novo custa uma chamada; perder o trabalho custa o projeto.
        var semClassificacao = """{"response":"Vou cuidar disso.","demands":[]}""";

        Assert.Throws<AgentOutputValidationException>(
            () => ChiefTurnOutputContract.ParseChiefTurn(semClassificacao));

        // O contrato GERAL continua tolerante: ele é compartilhado com a detecção de serviços, os
        // executores de CLI e o simulado, que não tomam decisão de rota nenhuma.
        var geral = ChiefTurnOutputContract.Parse(semClassificacao);
        Assert.Equal(ChiefTurnIntent.Unmatched, geral.Intent);
    }

    [Fact]
    public void AClassifiedTurnPassesTheChiefContract()
    {
        var output = ChiefTurnOutputContract.ParseChiefTurn(
            """{"intent":"planejar_demanda","intentConfidence":0.93,"response":"Vou organizar.","demands":[]}""");

        Assert.Equal(ChiefTurnIntent.PlanejarDemanda, output.Intent);
        Assert.Equal(0.93, output.IntentConfidence);
    }

    private static ChiefDemandProposal Demand(string title) =>
        new(title, "descrição da demanda", "medium", ["critério"], null, null);
}
