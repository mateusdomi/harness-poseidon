using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Fase 1C — regressão permanente do veredito composto de revisão.
///
/// O ponto do bloco não é ter três camadas: é que camada SUPERIOR nunca compensa inferior, e que a
/// reprovação diga QUAL camada falhou. "Reprovado" sozinho não informa a quem corrige — build
/// quebrado, critério de aceite não atendido e objetivo da demanda não cumprido exigem ações
/// completamente diferentes.
/// </summary>
public sealed class LayeredReviewVerdictTests
{
    [Theory]
    [InlineData(VerificationLayer.Deterministic)]
    [InlineData(VerificationLayer.Behavioral)]
    [InlineData(VerificationLayer.Intent)]
    public void EachLayerFailsOnItsOwnAndNamesItself(VerificationLayer failing)
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Layer(VerificationLayer.Deterministic, failing),
            Layer(VerificationLayer.Behavioral, failing),
            Layer(VerificationLayer.Intent, failing),
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(failing, outcome.BlockedAt);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Severity));
    }

    [Fact]
    public void AHigherLayerNeverCompensatesALowerOne()
    {
        // Intenção impecável com build quebrado continua reprovado: um "a solução está ótima" não
        // pode aprovar trabalho que falha o teste objetivo.
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            new LayerResult(VerificationLayer.Deterministic, LayerVerdict.Fail, "build.broken"),
            new LayerResult(VerificationLayer.Behavioral, LayerVerdict.Pass, "ok"),
            new LayerResult(VerificationLayer.Intent, LayerVerdict.Pass, "ok"),
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Deterministic, outcome.BlockedAt);
    }

    [Fact]
    public void ALayerThatDidNotRunIsNeverCountedAsApproval()
    {
        // "Não executada" e "aprovada" são coisas opostas. Tratar ausência de prova como prova é
        // exatamente o que transforma um gate em carimbo.
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            new LayerResult(VerificationLayer.Deterministic, LayerVerdict.Pass, "ok"),
            new LayerResult(VerificationLayer.Behavioral, LayerVerdict.NotRun, "not_run"),
            new LayerResult(VerificationLayer.Intent, LayerVerdict.Pass, "ok"),
        ]);

        Assert.False(outcome.Approved);
    }

    [Fact]
    public void TheDeterministicLayerGuardsTheReviewerSlotBeforeItIsOccupied()
    {
        // Camada 1 roda ANTES de ocupar revisor: ocupar um agente crítico para dizer que o build
        // está quebrado é gastar o recurso mais caro para descobrir o mais barato.
        Assert.False(LayeredVerificationPolicy.MayOccupyReviewer(
            [new LayerResult(VerificationLayer.Deterministic, LayerVerdict.Fail, "build.broken")]));
        Assert.True(LayeredVerificationPolicy.MayOccupyReviewer(
            [new LayerResult(VerificationLayer.Deterministic, LayerVerdict.Pass, "ok")]));
    }

    private static LayerResult Layer(VerificationLayer layer, VerificationLayer failing) =>
        new(layer, layer == failing ? LayerVerdict.Fail : LayerVerdict.Pass,
            layer == failing ? $"{layer}.failed" : "ok");

    /// <summary>
    /// Quando o gate DOCUMENTAL reprova, o bloqueio precisa constar na camada determinística.
    ///
    /// Antes a camada era uma constante `Pass` com motivo `clean`, então a mesma entrega ficava
    /// registrada como "diagnóstico limpo" logo depois de ser reprovada por um gate
    /// determinístico. O desfecho saía certo — o crítico sintético reprovava —, mas o REGISTRO
    /// dizia o contrário do que aconteceu, e rastreabilidade que mente é pior que ausente,
    /// porque ninguém desconfia dela.
    /// </summary>
    [Fact]
    public void OBloqueioDeterministicoApareceNaCamadaQueOProduziu()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            new LayerResult(
                VerificationLayer.Deterministic, LayerVerdict.Fail, "document.template_invalid"),
            new LayerResult(VerificationLayer.Behavioral, LayerVerdict.Fail, "critic.fail"),
            new LayerResult(VerificationLayer.Intent, LayerVerdict.Fail, "critic.fail"),
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Deterministic, outcome.BlockedAt);
    }

    /// <summary>
    /// Camada superior não compensa inferior: determinística reprovada barra mesmo com o crítico
    /// achando o trabalho bom. É o caso que a composição existe para cobrir.
    /// </summary>
    [Fact]
    public void OCriticoSatisfeitoNaoSalvaUmaFalhaDeterministica()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            new LayerResult(
                VerificationLayer.Deterministic, LayerVerdict.Fail, "code.diagnostics_dirty"),
            new LayerResult(VerificationLayer.Behavioral, LayerVerdict.Pass, "critic.pass"),
            new LayerResult(VerificationLayer.Intent, LayerVerdict.Pass, "critic.pass"),
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Deterministic, outcome.BlockedAt);
    }
}
