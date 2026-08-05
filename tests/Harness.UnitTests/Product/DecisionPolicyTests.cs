using Harness.Modules.Workflows.Product;
using static Harness.Modules.Workflows.Product.DecisionPolicy;

namespace Harness.UnitTests.Product;

/// <summary>
/// A política de dúvidas, contra os dois erros observados no run real: perguntar o que já foi
/// respondido (o usuário reenviou a mesma decisão nove vezes) e parar o que não depende da
/// resposta (um projeto inteiro esperando um documento de observabilidade).
/// </summary>
public sealed class DecisionPolicyTests
{
    [Fact]
    public void FonteComAutoridadeJaRespondeuEhClosedSemBloquearNada()
    {
        var routing = Route(new DecisionFacts(
            AnsweredByAuthoritativeSource: true,
            IsBusinessOrScopeOrCompliance: false,
            IsReversible: true,
            HasSafeDefault: true,
            BlocksDependentWork: true));

        Assert.Equal(DecisionRoute.Closed, routing.Route);
        Assert.Empty(routing.BlockedChain);
    }

    /// <summary>Fonte fechada vence até questão de negócio: reaberta só por conflito, não por rota.</summary>
    [Fact]
    public void RespostaFechadaVenceMesmoQuandoOTemaEDeNegocio()
    {
        var routing = Route(new DecisionFacts(true, true, false, false, true));

        Assert.Equal(DecisionRoute.Closed, routing.Route);
    }

    [Fact]
    public void EscolhaTecnicaReversivelComDefaultSeguroEhInfer()
    {
        var routing = Route(new DecisionFacts(false, false, true, true, false));

        Assert.Equal(DecisionRoute.Infer, routing.Route);
        Assert.Contains("registrar a premissa", routing.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// ASK bloqueia SÓ a cadeia dependente — nunca o projeto. É a regressão do defeito que parou
    /// empréstimos inteiro por causa de um documento.
    /// </summary>
    [Fact]
    public void PerguntaDeNegocioBloqueiaSomenteACadeiaDependente()
    {
        var routing = Route(new DecisionFacts(
            false, IsBusinessOrScopeOrCompliance: true, true, false,
            BlocksDependentWork: true,
            DependentChain: ["card-observabilidade"]));

        Assert.Equal(DecisionRoute.Ask, routing.Route);
        Assert.Equal(["card-observabilidade"], routing.BlockedChain);
    }

    [Fact]
    public void PerguntaSemDependentesNaoBloqueiaNada()
    {
        var routing = Route(new DecisionFacts(false, true, true, false, false));

        Assert.Equal(DecisionRoute.Ask, routing.Route);
        Assert.Empty(routing.BlockedChain);
    }

    /// <summary>Irreversível sem default seguro é ASK mesmo sendo tecnicamente "só engenharia".</summary>
    [Fact]
    public void IrreversivelSemDefaultSeguroEhAsk()
    {
        var routing = Route(new DecisionFacts(false, false, IsReversible: false, HasSafeDefault: false, false));

        Assert.Equal(DecisionRoute.Ask, routing.Route);
    }

    [Fact]
    public void SemRespostaSemDefaultESemNadaParadoEhDefer()
    {
        var routing = Route(new DecisionFacts(false, false, true, false, false));

        Assert.Equal(DecisionRoute.Defer, routing.Route);
    }
}
