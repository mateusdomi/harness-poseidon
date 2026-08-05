using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A janela de avaliação TrensRJ — o recorte determinístico que separa preparação de
/// desenvolvimento real. História nenhuma é apagada: o que veio antes fica FORA do relatório,
/// não fora do arquivo.
/// </summary>
public sealed class EvaluationWindowReportTests
{
    private static readonly EvaluationWindow Window = new(
        "TrensRJ Poseidon Evaluation 2026",
        new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.FromHours(-3)),
        new DateTimeOffset(2026, 8, 20, 23, 59, 0, TimeSpan.FromHours(-3)),
        ["PRISMA-FINAL", "INDICADORES-FINAL"]);

    private sealed record LedgerRow(string ProjectId, DateTimeOffset At, string Kind);

    [Fact]
    public void ORelatorioSoEnxergaEventosDaJanelaEDosProjetosDaAvaliacao()
    {
        var ledger = new[]
        {
            // Preflight (antes da janela) — fica no arquivo, fora do relatório.
            new LedgerRow("PRISMA-PREFLIGHT", Window.Start.AddDays(-1), "attempt"),
            // Projeto alheio (Poseidon interno) dentro da janela — fora do relatório.
            new LedgerRow("POSEIDON", Window.Start.AddDays(2), "attempt"),
            // Os que valem.
            new LedgerRow("PRISMA-FINAL", Window.Start.AddHours(6), "attempt"),
            new LedgerRow("INDICADORES-FINAL", Window.Start.AddDays(1), "commit"),
            new LedgerRow("PRISMA-FINAL", Window.Start.AddDays(3), "review"),
        };

        var report = Window.Filter(ledger, row => row.At, row => row.ProjectId);

        Assert.Equal(3, report.Count);
        Assert.All(report, row => Assert.Contains(row.ProjectId, Window.ProjectIds));
        Assert.All(report, row => Assert.True(row.At >= Window.Start));
    }

    [Fact]
    public void ASobreposicaoDosDoisProjetosEDerivavelDoMesmoRecorte()
    {
        var runs = new[]
        {
            new LedgerRow("PRISMA-FINAL", Window.Start.AddDays(1), "run"),
            new LedgerRow("INDICADORES-FINAL", Window.Start.AddDays(1).AddHours(2), "run"),
        };

        var byProject = Window.Filter(runs, r => r.At, r => r.ProjectId)
            .GroupBy(r => r.ProjectId).Count();

        // Dois projetos com atividade no MESMO recorte = a prova de simultaneidade que o dia 20
        // exibe (intervalos sobrepostos, não sequenciais).
        Assert.Equal(2, byProject);
    }
}
