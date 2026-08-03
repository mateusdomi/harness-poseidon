using Harness.Host.Workflows;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// A regra que impede a fase de Desenvolvimento de fechar com papelada e nenhuma implementação.
/// </summary>
public sealed class ExecutivePhaseGuardTests
{
    private static DemandMaterializationView Demand(
        string id, string? status, bool isInternal = false) =>
        new(id, isInternal, status);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AntesDaLiberacaoDeConstrucaoOAdiamentoNaoEPendencia(int phaseOrder)
    {
        // Em Triagem ou Planejamento o compromisso adiado é o comportamento correto. Cobrá-lo aqui
        // pararia a esteira exatamente no trecho em que ela deve andar.
        var pending = ExecutivePhaseGuard.PendingImplementation(
            phaseOrder, [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", PlanMaterializationStatus.Pending)]);

        Assert.Empty(pending);
    }

    [Fact]
    public void FaseExecutivaComCompromissoPendenteSeguraOPortao()
    {
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder,
            [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", PlanMaterializationStatus.Pending)]);

        Assert.Equal(["01AAAAAAAAAAAAAAAAAAAAAAAA"], pending);
    }

    [Theory]
    [InlineData(PlanMaterializationStatus.Pending)]
    [InlineData(PlanMaterializationStatus.Processing)]
    [InlineData(PlanMaterializationStatus.Failed)]
    public void QualquerEstadoQueNaoSejaConcluidoContinuaSendoPromessaEmAberto(string status)
    {
        // `failed` é o caso que mais engana: o compromisso parou, mas parar não entrega card algum.
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder,
            [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", status)]);

        Assert.Single(pending);
    }

    [Fact]
    public void CompromissoConcluidoLiberaAFase()
    {
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder,
            [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", PlanMaterializationStatus.Completed)]);

        Assert.Empty(pending);
    }

    [Fact]
    public void DemandaInternaDaEsteiraNaoSeguraFaseExecutiva()
    {
        // A esteira cria uma demanda por objetivo-documento. Se ela contasse como construção
        // prometida, a fase 5 jamais fecharia — a guarda viraria um travamento permanente.
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder,
            [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", PlanMaterializationStatus.Pending, isInternal: true)]);

        Assert.Empty(pending);
    }

    [Fact]
    public void DemandaSemCompromissoNaoInventaPendencia()
    {
        // Nunca houve promessa de materializar: não há o que cobrar, e cobrar assim mesmo
        // bloquearia projetos cujo trabalho entrou por outro caminho.
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder, [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", null)]);

        Assert.Empty(pending);
    }

    [Fact]
    public void OrdemEstavelParaQueOMotivoNaoOscileEntreCiclos()
    {
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder,
            [
                Demand("01CCCCCCCCCCCCCCCCCCCCCCCC", PlanMaterializationStatus.Pending),
                Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", PlanMaterializationStatus.Processing),
                Demand("01BBBBBBBBBBBBBBBBBBBBBBBB", PlanMaterializationStatus.Completed),
            ]);

        Assert.Equal(["01AAAAAAAAAAAAAAAAAAAAAAAA", "01CCCCCCCCCCCCCCCCCCCCCCCC"], pending);
    }

    [Fact]
    public void FasesPosterioresAoDesenvolvimentoTambemSaoExecutivas()
    {
        // Testes, Homologação e Release vêm depois: se a implementação nunca nasceu, elas não têm
        // sobre o que se pronunciar.
        var pending = ExecutivePhaseGuard.PendingImplementation(
            ActivePhaseResolver.DevelopmentPhaseOrder + 1,
            [Demand("01AAAAAAAAAAAAAAAAAAAAAAAA", PlanMaterializationStatus.Pending)]);

        Assert.Single(pending);
    }
}
