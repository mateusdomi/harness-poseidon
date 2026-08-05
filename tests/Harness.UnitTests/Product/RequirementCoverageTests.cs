using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A cadeia que o run de 2026-08-04 não tinha: requisito → card → implementação → evidência.
///
/// Ela quebrava no primeiro elo. Havia o requisito — "uma pessoa da equipe consegue registrar e
/// consultar empréstimos" —, havia dois cards de interface, e nada ligava um ao outro de forma
/// verificável. Quando os dois cards foram cancelados como "substituídos", o requisito ficou órfão
/// e a fase de Desenvolvimento fechou como se estivesse tudo certo.
///
/// A asserção central deste arquivo é uma frase só: <b>card cancelado não cobre requisito.</b>
/// </summary>
public sealed class RequirementCoverageTests
{
    private const string Requisito = "01REQ-INTERFACE";
    private const string Titulo = "Uma pessoa da equipe consegue registrar e consultar empréstimos";

    private static IReadOnlyList<(string, string, bool)> Requisitos() =>
        [(Requisito, Titulo, false)];

    private static Dictionary<string, IReadOnlyList<RequirementCard>> Cards(
        params RequirementCard[] cards) =>
        new(StringComparer.Ordinal) { [Requisito] = cards };

    private static RequirementCoverage Analisar(
        Dictionary<string, IReadOnlyList<RequirementCard>> cards, bool entregaAprovada = false) =>
        RequirementCoverageAnalyzer.Analyze(Requisitos(), cards, entregaAprovada).Single();

    /// <summary>
    /// A REGRESSÃO DE 2026-08-04, em uma asserção: os dois cards de interface existiram, foram
    /// cancelados, e o requisito precisa aparecer como DESCOBERTO — não como coberto por cards que
    /// um dia existiram.
    /// </summary>
    [Fact]
    public void CardsCanceladosDeixamORequisitoDescoberto()
    {
        var coverage = Analisar(Cards(
            new RequirementCard("card-t02-a", "FEAT/T02 Frontend", "cancelled", Archived: false),
            new RequirementCard("card-t02-b", "FEAT/T02 Frontend", "cancelled", Archived: false)));

        Assert.Equal(RequirementCoverageStatus.Unplanned, coverage.Status);
        Assert.False(coverage.IsCovered);
        Assert.Empty(coverage.AliveCardIds);
        Assert.Contains("cancelados ou arquivados", coverage.Reason, StringComparison.Ordinal);
        Assert.Contains("sem que outro assumisse", coverage.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RequisitoSemNenhumCardApareceComoNaoPlanejado()
    {
        var coverage = RequirementCoverageAnalyzer
            .Analyze(Requisitos(), new Dictionary<string, IReadOnlyList<RequirementCard>>(), false)
            .Single();

        Assert.Equal(RequirementCoverageStatus.Unplanned, coverage.Status);
        Assert.Contains("Nenhum card foi criado", coverage.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Um card vivo entre cancelados RESGATA o requisito: foi exatamente o que a intervenção humana
    /// fez às 13:43 daquele dia, e é o comportamento que o mecanismo precisa reproduzir sozinho.
    /// </summary>
    [Fact]
    public void UmCardVivoEntreCanceladosDevolveODonoAoRequisito()
    {
        var coverage = Analisar(Cards(
            new RequirementCard("card-t02-a", "FEAT/T02 Frontend", "cancelled", false),
            new RequirementCard("card-novo", "FEAT/T02 Interface", "ready", false)));

        Assert.Equal(RequirementCoverageStatus.Planned, coverage.Status);
        Assert.Equal(["card-novo"], coverage.AliveCardIds);
    }

    [Fact]
    public void CardEmCursoApareceComoTrabalhoEmAndamento()
    {
        var coverage = Analisar(Cards(
            new RequirementCard("card", "FEAT/T02", "development", false)));

        Assert.Equal(RequirementCoverageStatus.InProgress, coverage.Status);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("escalated")]
    public void CardBloqueadoOuEscaladoSeguraAFaseEmVezDeSumir(string estado)
    {
        var coverage = Analisar(Cards(new RequirementCard("card", "FEAT/T02", estado, false)));

        Assert.Equal(RequirementCoverageStatus.Blocked, coverage.Status);
        Assert.False(coverage.IsCovered);
        Assert.Contains("alguém precisa decidir", coverage.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Trabalho concluído NÃO é produto funcionando. Enquanto o portão do produto não aprovar, o
    /// requisito para em `Implemented` — que é a distinção que o Product DoD existe para fazer, e
    /// que este modelo não pode desfazer.
    /// </summary>
    [Fact]
    public void CardConcluidoComEntregaReprovadaParaEmImplementadoENaoEmSatisfeito()
    {
        var coverage = Analisar(
            Cards(new RequirementCard("card", "FEAT/T02", "completed", false)),
            entregaAprovada: false);

        Assert.Equal(RequirementCoverageStatus.Implemented, coverage.Status);
        Assert.False(coverage.IsCovered);
        Assert.Contains("não é produto funcionando", coverage.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CardConcluidoComEntregaAprovadaSatisfazORequisito()
    {
        var coverage = Analisar(
            Cards(new RequirementCard("card", "FEAT/T02", "completed", false)),
            entregaAprovada: true);

        Assert.Equal(RequirementCoverageStatus.Satisfied, coverage.Status);
        Assert.True(coverage.IsCovered);
    }

    [Fact]
    public void RequisitoSubstituidoNaoEMaisCobrado()
    {
        var coverage = RequirementCoverageAnalyzer
            .Analyze([(Requisito, Titulo, true)],
                new Dictionary<string, IReadOnlyList<RequirementCard>>(), false)
            .Single();

        Assert.Equal(RequirementCoverageStatus.Superseded, coverage.Status);
        Assert.True(coverage.IsCovered);
    }

    /// <summary>
    /// O que o portão de fase consulta: só descoberto e bloqueado seguram o Desenvolvimento.
    /// Planejado e em curso são trabalho normal, e travar por eles pararia a fábrica o tempo todo.
    /// </summary>
    [Fact]
    public void SomenteDescobertoEBloqueadoSeguramAFase()
    {
        var coverage = RequirementCoverageAnalyzer.Analyze(
            [
                ("req-descoberto", "sem dono", false),
                ("req-bloqueado", "travado", false),
                ("req-andando", "em curso", false),
                ("req-pronto", "entregue", false),
            ],
            new Dictionary<string, IReadOnlyList<RequirementCard>>(StringComparer.Ordinal)
            {
                ["req-bloqueado"] = [new RequirementCard("b", "x", "blocked", false)],
                ["req-andando"] = [new RequirementCard("c", "x", "development", false)],
                ["req-pronto"] = [new RequirementCard("d", "x", "completed", false)],
            },
            deliverySatisfied: true);

        var bloqueando = RequirementCoverageAnalyzer.Blocking(coverage);

        Assert.Equal(2, bloqueando.Count);
        Assert.Contains(bloqueando, item => item.RequirementId == "req-descoberto");
        Assert.Contains(bloqueando, item => item.RequirementId == "req-bloqueado");
    }

    /// <summary>
    /// O cenário completo de 2026-08-04: quatro requisitos do usuário, o de interface órfão. A fase
    /// não pode fechar — e o diagnóstico precisa dizer QUAL requisito, não "a entrega falhou".
    /// </summary>
    [Fact]
    public void OCenarioDoRunRealSeguraAFaseENomeiaORequisitoOrfao()
    {
        var coverage = RequirementCoverageAnalyzer.Analyze(
            [
                ("req-quem-pegou", "saber quem pegou cada equipamento", false),
                ("req-prazo", "saber quando precisa devolver", false),
                ("req-atraso", "saber quando está atrasado", false),
                (Requisito, Titulo, false),
            ],
            new Dictionary<string, IReadOnlyList<RequirementCard>>(StringComparer.Ordinal)
            {
                ["req-quem-pegou"] = [new RequirementCard("t01a", "FEAT/T01", "completed", false)],
                ["req-prazo"] = [new RequirementCard("t01b", "FEAT/T01", "completed", false)],
                ["req-atraso"] = [new RequirementCard("t01c", "FEAT/T01", "completed", false)],
                [Requisito] =
                [
                    new RequirementCard("t02a", "FEAT/T02 Frontend", "cancelled", false),
                    new RequirementCard("t02b", "FEAT/T02 Frontend", "cancelled", false),
                ],
            },
            deliverySatisfied: false);

        var bloqueando = RequirementCoverageAnalyzer.Blocking(coverage);

        var orfao = Assert.Single(bloqueando);
        Assert.Equal(Requisito, orfao.RequirementId);
        Assert.Equal(RequirementCoverageStatus.Unplanned, orfao.Status);
        Assert.Contains("registrar e consultar", orfao.Title, StringComparison.Ordinal);
    }
}
