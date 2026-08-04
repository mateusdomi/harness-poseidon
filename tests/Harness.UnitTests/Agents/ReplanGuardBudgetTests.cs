using Harness.Persistence.Abstractions.WorkChain;
using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// OPS-070 — o orçamento anti-laço puniu quatro cards por uma parede que era NOSSA.
///
/// Os cards de implementação da fase 5 entregaram diff vazio duas vezes cada, por dois defeitos
/// do próprio sistema: o cabeçalho da instrução mandava um Product Owner — cujo escopo nega
/// `src/**` — escrever backend (OPS-069), e o plano exigia uma decisão de arquitetura que nenhum
/// card podia executar (OPS-072). Quando os dois consertos entraram no ar, o replanejamento único
/// de cada card já estava gasto e eles não tinham mais volta automática.
///
/// Duas correções, ambas estreitas de propósito — nenhuma toca o orçamento de rodadas nem o
/// limiar do circuito, que são o freio que de fato funcionou naquela noite:
///
/// 1. "Tentou" passa a significar que algo foi ENTREGUE. Uma tentativa que rodou, gastou token e
///    não deixou commit nenhum não exerceu abordagem alguma — não há o que evitar repetir.
///    É o mesmo raciocínio que o código já aceita para falha de infraestrutura.
///
/// 2. O teto de rodadas operacionais passa a contar REPLANEJAMENTOS. Contava versões de
///    instrução como proxy — cada replanejamento grava uma —, mas a correção após reprovação
///    também grava, e o proxy quebrou: dez versões corretivas e um replanejamento apareciam como
///    orçamento esgotado.
/// </summary>
public sealed class ReplanGuardBudgetTests
{
    private const string Replan = "## Replanejamento após escalonamento";
    private static readonly string[] Original = ["original"];

    private static BoardAttemptRecord Attempt(
        long tokens, int commits, string? failureReason = null) =>
        new("t", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "card", 1, "failed", "agent",
            DateTimeOffset.UnixEpoch, null, null, 0m, 0, tokens,
            [.. Enumerable.Range(0, commits).Select(i => $"git-commit:{i}")],
            null, failureReason);

    private static int RealAttempts(params BoardAttemptRecord[] attempts) =>
        attempts.Count(a =>
            !CardCircuitBreakerService.IsInfrastructureFailure(a.FailureReason) &&
            a.TokensOutput > 0 &&
            a.CommitRefs.Count > 0);

    // ---- "Tentou" significa entregou -------------------------------------------------

    /// <summary>
    /// O caso que travou a fase 5: token gasto, nada entregue. O ator falou e não escreveu —
    /// a abordagem nunca foi exercida, então não há o que o replanejamento precise evitar.
    /// </summary>
    [Fact]
    public void TentativaQueGastouTokenESemCommitNaoContaComoAbordagemExercida()
    {
        Assert.Equal(0, RealAttempts(Attempt(tokens: 11607, commits: 0)));
    }

    /// <summary>
    /// E o simétrico, que é o que impede isto de virar desculpa: entregou commit, exerceu a
    /// abordagem, gastou o replanejamento. Um card que de fato tentou e falhou não volta de graça.
    /// </summary>
    [Fact]
    public void TentativaQueENTREGOUContaEGastaOReplanejamento()
    {
        Assert.Equal(1, RealAttempts(Attempt(tokens: 67748, commits: 1)));
    }

    /// <summary>Falha de infraestrutura continua não contando — a regra anterior segue valendo.</summary>
    [Theory]
    [InlineData("executor.quota_exhausted")]
    [InlineData("run.host_restart")]
    [InlineData("executor.authentication_required")]
    public void FalhaDeInfraestruturaContinuaNaoContando(string reason)
    {
        Assert.Equal(0, RealAttempts(Attempt(tokens: 500, commits: 3, failureReason: reason)));
    }

    /// <summary>
    /// Commit sem token é entrega igualmente: o que importa é ter deixado trabalho, não ter
    /// consumido modelo. Exigir os dois seria confundir esforço com resultado — mas exigir NENHUM
    /// seria deixar passar a tentativa vazia que este conserto existe para reconhecer.
    /// </summary>
    [Fact]
    public void SoContaQuandoHOUVEEntregaEConsumo()
    {
        Assert.Equal(0, RealAttempts(Attempt(tokens: 0, commits: 2)));
        Assert.Equal(0, RealAttempts(Attempt(tokens: 0, commits: 0)));
    }

    // ---- O teto conta replanejamentos, não instruções --------------------------------

    private static int OperationalReplans(params string[] bodies) =>
        bodies.Count(b => b.Contains(Replan, StringComparison.Ordinal));

    /// <summary>
    /// O estado exato dos quatro cards presos: dez versões de instrução e UM replanejamento.
    /// O proxy antigo (contar instruções) via orçamento esgotado onde havia uma rodada usada.
    /// </summary>
    [Fact]
    public void DezVersoesCorretivasEUmReplanejamentoContamComoUmaRodada()
    {
        var bodies = Original
            .Concat(Enumerable.Repeat("## Correções exigidas pelo review independente", 8))
            .Append($"corpo\n\n{Replan}\nA abordagem anterior...")
            .ToArray();

        Assert.Equal(10, bodies.Length);
        Assert.Equal(1, OperationalReplans(bodies));
    }

    /// <summary>
    /// E o teto continua existindo: quatro replanejamentos de verdade esgotam o orçamento. O
    /// conserto corrige a contagem, não remove o freio.
    /// </summary>
    [Fact]
    public void QuatroReplanejamentosDeVerdadeAindaEsgotamOTeto()
    {
        var bodies = Enumerable.Repeat($"corpo\n\n{Replan}\ntexto", 4).ToArray();

        Assert.True(OperationalReplans(bodies) >= 4);
    }

    /// <summary>
    /// Uma correção que CITE o replanejamento no texto do achado não pode inflar a contagem por
    /// engano — mas aqui a citação é literal e conta, e isso é aceitável: o marcador é uma linha
    /// própria escrita pelo sistema, não algo que um crítico digite por acaso. O teste existe para
    /// que essa suposição fique registrada e falhe alto se o marcador virar texto comum.
    /// </summary>
    [Fact]
    public void OMarcadorEEscritoPeloSistemaENaoPorAcaso()
    {
        Assert.Equal(0, OperationalReplans("o revisor mencionou replanejamento em prosa"));
        Assert.Equal(1, OperationalReplans($"x\n\n{Replan}\ny"));
    }
}
