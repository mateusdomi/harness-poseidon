using Harness.Modules.Operations;

namespace Harness.UnitTests.Operations;

/// <summary>
/// §31/§32 — a operação precisa medir custo, tempo e desperdício. O que estes testes protegem
/// não é a aritmética: é a recusa em inventar número. Campo sem base de cálculo volta `null`,
/// porque um zero afirma "medimos e deu zero" — coisa diferente de "não há o que medir".
/// </summary>
public sealed class OperationMetricsCalculatorTests
{
    private static InvocationSample Invocation(
        string attempt, string outcome, decimal cost, long durationMs = 1000, long input = 10, long output = 5) =>
        new(attempt, "card-1", outcome, cost, durationMs, input, output);

    private static AttemptSample Attempt(string id, int number, string state, long? durationMs = 1000) =>
        new(id, "card-1", number, state, durationMs);

    [Fact]
    public void SemAmostraNenhumaNadaEInventado()
    {
        var report = OperationMetricsCalculator.Calculate([], [], []);

        Assert.Null(report.CostPerAcceptedArtifact);
        Assert.Null(report.TransientFailureWasteRate);
        Assert.Null(report.FirstPassAcceptanceRate);
        Assert.Null(report.MeanRunDuration);
        Assert.Null(report.ModelUtilization);
        Assert.Equal(0, report.InvocationCount);
    }

    /// <summary>
    /// O desfecho real carrega sufixo e prefixo (`permanent|usage_unknown`, `review:completed`).
    /// Comparar por igualdade exata descartaria a maioria das amostras de produção.
    /// </summary>
    [Fact]
    public void ODesperdicioTransitorioEReconhecidoMesmoComSufixoNoDesfecho()
    {
        var report = OperationMetricsCalculator.Calculate(
            [
                Invocation("a1", "completed", 6m),
                Invocation("a2", "transient|usage_unknown", 2m),
                Invocation("a3", "transient", 2m),
            ],
            [Attempt("a1", 1, "approved")],
            []);

        // 4 de 10 dólares foram para tentativas que não entregaram nada.
        Assert.Equal(0.4, report.TransientFailureWasteRate!.Value, 3);
    }

    [Fact]
    public void RetrabalhoEOQueSeGastouDepoisDaPrimeiraTentativa()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 3m), Invocation("a2", "completed", 7m)],
            [Attempt("a1", 1, "rejected"), Attempt("a2", 2, "approved")],
            []);

        Assert.Equal(7m, report.CostOfRework);
        // O custo por artefato aceito conta o TOTAL, não só o da tentativa boa: as duas rodadas
        // foram necessárias para chegar a uma entrega.
        Assert.Equal(10m, report.CostPerAcceptedArtifact);
    }

    [Fact]
    public void AceiteDePrimeiraOlhaOCardENaoATentativa()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 1m)],
            [
                new AttemptSample("a1", "card-1", 1, "approved", 1000),
                new AttemptSample("b1", "card-2", 1, "rejected", 1000),
                new AttemptSample("b2", "card-2", 2, "approved", 1000),
            ],
            []);

        // Dois cards resolvidos, um aceito de primeira.
        Assert.Equal(0.5, report.FirstPassAcceptanceRate!.Value, 3);
    }

    /// <summary>Card ainda em voo não conta como sucesso nem como fracasso.</summary>
    [Fact]
    public void CardAindaEmExecucaoNaoEntraNaTaxaDeAceite()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 1m)],
            [
                new AttemptSample("a1", "card-1", 1, "approved", 1000),
                new AttemptSample("c1", "card-3", 1, "running", null),
            ],
            []);

        Assert.Equal(1.0, report.FirstPassAcceptanceRate!.Value, 3);
    }

    [Fact]
    public void UtilizacaoDeModeloEOTempoEmChamadaSobreOTempoDeParede()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 1m, durationMs: 30_000)],
            [Attempt("a1", 1, "approved")],
            [new PhaseSample(1, TimeSpan.FromMinutes(1)), new PhaseSample(2, TimeSpan.FromMinutes(1))]);

        // 30s de chamada em 2 minutos de parede.
        Assert.Equal(0.25, report.ModelUtilization!.Value, 3);
        Assert.Equal(2, report.PhaseWallTime.Count);
    }

    /// <summary>Fase ainda aberta não tem tempo de parede — e não pode virar zero.</summary>
    [Fact]
    public void FaseAbertaNaoViraZeroNoTempoDeParede()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 1m, durationMs: 1000)],
            [Attempt("a1", 1, "approved")],
            [new PhaseSample(1, TimeSpan.FromMinutes(10)), new PhaseSample(2, null)]);

        Assert.Null(report.PhaseWallTime[1]);
        // A utilização usa só o que tem medida, sem fingir que a fase aberta durou nada.
        Assert.Equal(1000 / 600_000d, report.ModelUtilization!.Value, 5);
    }

    /// <summary>
    /// Uma tentativa órfã que ficou dias aberta puxa a média sozinha. Medido em produção: 71
    /// horas num único registro levaram a média de ~4 minutos para ~84. A mediana é a estatística
    /// que descreve o sistema real.
    /// </summary>
    [Fact]
    public void UmOutlierNaoPodeDescreverOSistemaInteiro()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 1m)],
            [
                Attempt("a1", 1, "approved", durationMs: 60_000),
                Attempt("a2", 2, "rejected", durationMs: 60_000),
                Attempt("a3", 3, "rejected", durationMs: 60_000),
                Attempt("a4", 4, "rejected", durationMs: 256_000_000),
            ],
            []);

        Assert.Equal(60, report.MedianRunDuration!.Value.TotalSeconds, 1);
        Assert.True(report.MeanRunDuration!.Value > TimeSpan.FromHours(17));
    }

    /// <summary>
    /// Medir vários projetos devolvia centenas de posições quase todas nulas. Uma posição por
    /// ORDEM de fase é o que torna o número legível.
    /// </summary>
    [Fact]
    public void OTempoDeFaseEAgregadoPorOrdemEntreExecucoes()
    {
        var report = OperationMetricsCalculator.Calculate(
            [Invocation("a1", "completed", 1m)],
            [Attempt("a1", 1, "approved")],
            [
                new PhaseSample(1, TimeSpan.FromMinutes(10)),
                new PhaseSample(1, TimeSpan.FromMinutes(20)),
                new PhaseSample(2, null),
            ]);

        Assert.Equal(2, report.PhaseWallTime.Count);
        Assert.Equal(15, report.PhaseWallTime[0]!.Value.TotalMinutes, 1);
        Assert.Null(report.PhaseWallTime[1]);
    }

    [Fact]
    public void OsTotaisSomamTokensECusto()
    {
        var report = OperationMetricsCalculator.Calculate(
            [
                Invocation("a1", "completed", 1.5m, input: 100, output: 40),
                Invocation("a2", "completed", 2.5m, input: 200, output: 60),
            ],
            [Attempt("a1", 1, "approved")],
            []);

        Assert.Equal(4.0m, report.TotalCostUsd);
        Assert.Equal(300, report.TotalInputTokens);
        Assert.Equal(100, report.TotalOutputTokens);
        Assert.Equal(2, report.InvocationCount);
    }
}
