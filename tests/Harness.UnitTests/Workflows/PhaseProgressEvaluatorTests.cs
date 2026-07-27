using Harness.Modules.Workflows.Application;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O progresso da fase mede TRABALHO ACEITO, não documentos previstos. Produzir briefing técnico,
/// code review estruturado, métricas DORA e dicionário ubíquo não implementa nada — e enquanto o
/// indicador vinha só dos documentos, uma fase de Desenvolvimento fechava com zero código.
/// </summary>
public sealed class PhaseProgressEvaluatorTests
{
    private static PhaseObligation Obligation(
        string key,
        PhaseObligationKind kind,
        PhaseObligationState state,
        bool required = true,
        decimal weight = 25m) =>
        new(key, kind, key, required, weight, state, "template");

    [Fact]
    public void ADocumentaryPhaseWalksFromZeroToHalfToWhole()
    {
        var pending = new[]
        {
            Obligation("ficha", PhaseObligationKind.Document, PhaseObligationState.Pending, weight: 50m),
            Obligation("stakeholders", PhaseObligationKind.Document, PhaseObligationState.Pending, weight: 50m),
        };
        Assert.Equal(0m, PhaseProgressEvaluator.Evaluate(pending).Percentage);

        var half = new[] { pending[0] with { State = PhaseObligationState.Accepted }, pending[1] };
        Assert.Equal(50m, PhaseProgressEvaluator.Evaluate(half).Percentage);

        var whole = half.Select(item => item with { State = PhaseObligationState.Accepted }).ToArray();
        var snapshot = PhaseProgressEvaluator.Evaluate(whole);
        Assert.Equal(100m, snapshot.Percentage);
        Assert.True(snapshot.TechnicallyComplete);
    }

    [Fact]
    public void DocumentsDoNotCloseAPhaseThatAlsoHasImplementation()
    {
        // O caso exato do defeito: quatro documentos prontos e o código sem começar.
        var obligations = new[]
        {
            Obligation("briefing", PhaseObligationKind.Document, PhaseObligationState.Accepted),
            Obligation("code-review", PhaseObligationKind.Document, PhaseObligationState.Accepted),
            Obligation("dora", PhaseObligationKind.Metric, PhaseObligationState.Accepted),
            Obligation("endpoint", PhaseObligationKind.Implementation, PhaseObligationState.Pending),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(75m, snapshot.Percentage);
        Assert.False(snapshot.TechnicallyComplete);
    }

    [Fact]
    public void WorkInFlightShowsUpSeparatelyAndNeverInflatesThePercentage()
    {
        var obligations = new[]
        {
            Obligation("a", PhaseObligationKind.Implementation, PhaseObligationState.InProgress),
            Obligation("b", PhaseObligationKind.Implementation, PhaseObligationState.InReview),
            Obligation("c", PhaseObligationKind.Implementation, PhaseObligationState.Pending),
            Obligation("d", PhaseObligationKind.Implementation, PhaseObligationState.Accepted),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(25m, snapshot.Percentage);
        Assert.Equal(1, snapshot.InProgress);
        Assert.Equal(1, snapshot.InReview);
        Assert.Equal(1, snapshot.Pending);
        Assert.False(snapshot.TechnicallyComplete);
    }

    [Fact]
    public void ABlockedObligationKeepsThePhaseTechnicallyIncompleteEvenAtFullWeight()
    {
        var obligations = new[]
        {
            Obligation("a", PhaseObligationKind.Implementation, PhaseObligationState.Accepted, weight: 100m),
            Obligation("opcional", PhaseObligationKind.Evidence, PhaseObligationState.Blocked, required: false),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(100m, snapshot.Percentage);
        Assert.False(snapshot.TechnicallyComplete);
        Assert.Equal(1, snapshot.Blocked);
    }

    [Fact]
    public void OptionalObligationsStayOutOfTheRequiredDenominator()
    {
        var obligations = new[]
        {
            Obligation("obrigatoria", PhaseObligationKind.Implementation, PhaseObligationState.Accepted, weight: 100m),
            Obligation("extra", PhaseObligationKind.Evidence,
                PhaseObligationState.Pending, required: false, weight: 500m),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(100m, snapshot.Percentage);
        Assert.True(snapshot.TechnicallyComplete);
        Assert.Equal(1, snapshot.OptionalTotal);
    }

    [Fact]
    public void CancellingWhatFailedDoesNotManufactureCompletion()
    {
        // A obrigação cancelada sai da conta INTEIRA. Ela ainda existe no registro durável com a
        // justificativa — o que não pode é sumir do denominador e do numerador em silêncio.
        var obligations = new[]
        {
            Obligation("entregue", PhaseObligationKind.Implementation, PhaseObligationState.Accepted),
            Obligation("falhou", PhaseObligationKind.Test, PhaseObligationState.Cancelled),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(100m, snapshot.Percentage);
        Assert.Equal(1, snapshot.RequiredTotal);
    }

    [Fact]
    public void APhaseWithoutRequiredObligationsIsNeverComplete()
    {
        var snapshot = PhaseProgressEvaluator.Evaluate([]);
        Assert.Equal(0m, snapshot.Percentage);
        Assert.False(snapshot.TechnicallyComplete);
    }

    [Fact]
    public void APhaseWithOnlyOptionalObligationsReportsZeroNeverInferredProgress()
    {
        // Itens existem, mas nenhum é obrigatório: não há o que medir, e o percentual não pode ser
        // inferido a partir do que é opcional, mesmo que tudo opcional já esteja aceito.
        var obligations = new[]
        {
            Obligation("extra-1", PhaseObligationKind.Evidence, PhaseObligationState.Accepted, required: false),
            Obligation("extra-2", PhaseObligationKind.Metric, PhaseObligationState.Accepted, required: false),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(0m, snapshot.Percentage);
        Assert.Equal(0, snapshot.RequiredTotal);
        Assert.False(snapshot.TechnicallyComplete);
        Assert.Equal(2, snapshot.OptionalAccepted);
    }

    [Fact]
    public void ProgressNeverAveragesPercentagesAcrossObligationKinds()
    {
        // O defeito que este avaliador existe para corrigir, na forma de teste: uma obrigação de
        // Document 100% concluída ao lado de três de Implementation zeradas NÃO pode virar
        // (100% + 0%) / 2 = 50% por "dimensão". É um único total ponderado: 1 de 4 aceita = 25%.
        var obligations = new[]
        {
            Obligation("briefing", PhaseObligationKind.Document, PhaseObligationState.Accepted),
            Obligation("endpoint", PhaseObligationKind.Implementation, PhaseObligationState.Pending),
            Obligation("worker", PhaseObligationKind.Implementation, PhaseObligationState.Pending),
            Obligation("migration", PhaseObligationKind.Implementation, PhaseObligationState.Pending),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(25m, snapshot.Percentage);
        Assert.NotEqual(50m, snapshot.Percentage);
    }

    [Fact]
    public void PendingAndInFlightCountsMatchTheRealStateOfEachObligation()
    {
        // A contagem por estado tem que corresponder ao estado real de CADA obrigação viva
        // (obrigatória e opcional), e a cancelada não pode aparecer em nenhuma delas.
        var obligations = new[]
        {
            Obligation("a", PhaseObligationKind.Implementation, PhaseObligationState.Pending),
            Obligation("b", PhaseObligationKind.Implementation, PhaseObligationState.Pending, required: false),
            Obligation("c", PhaseObligationKind.Test, PhaseObligationState.InProgress),
            Obligation("d", PhaseObligationKind.Review, PhaseObligationState.InReview),
            Obligation("e", PhaseObligationKind.Evidence, PhaseObligationState.Blocked),
            Obligation("f", PhaseObligationKind.Test, PhaseObligationState.Cancelled),
            Obligation("g", PhaseObligationKind.Document, PhaseObligationState.Accepted),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(2, snapshot.Pending);
        Assert.Equal(1, snapshot.InProgress);
        Assert.Equal(1, snapshot.InReview);
        Assert.Equal(1, snapshot.Blocked);
        Assert.Equal(5, snapshot.RequiredTotal);
    }

    [Fact]
    public void ARequiredObligationWithoutAnExplicitWeightStillNeedsAcceptanceToReachOneHundred()
    {
        // Defeito: peso zero/negativo (obrigação sem peso declarado) fazia a obrigação SUMIR do
        // denominador. Antes da correção, "a" com peso 100 aceita e "b" com peso 0 pendente
        // fechavam em 100% mesmo com uma obrigatória ainda pendente.
        var obligations = new[]
        {
            Obligation("a", PhaseObligationKind.Implementation, PhaseObligationState.Accepted, weight: 100m),
            Obligation("b", PhaseObligationKind.Implementation, PhaseObligationState.Pending, weight: 0m),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.NotEqual(100m, snapshot.Percentage);
        Assert.False(snapshot.TechnicallyComplete);
    }

    [Fact]
    public void AllObligationsAcceptedWithoutExplicitWeightsStillReachOneHundredPercent()
    {
        // O mesmo defeito, no outro extremo: se NENHUMA obrigatória do plano tem peso declarado,
        // o peso total somava zero e a divisão era desviada para 0% mesmo com tudo aceito — uma
        // fase inteiramente entregue reportando progresso nulo.
        var obligations = new[]
        {
            Obligation("a", PhaseObligationKind.Implementation, PhaseObligationState.Accepted, weight: 0m),
            Obligation("b", PhaseObligationKind.Test, PhaseObligationState.Accepted, weight: 0m),
        };

        var snapshot = PhaseProgressEvaluator.Evaluate(obligations);

        Assert.Equal(100m, snapshot.Percentage);
        Assert.True(snapshot.TechnicallyComplete);
    }

    [Fact]
    public void DefaultWeightsAreEqualAndNormalized()
    {
        Assert.Equal(25m, PhaseProgressEvaluator.DefaultWeight(4));
        Assert.Equal(0m, PhaseProgressEvaluator.DefaultWeight(0));
    }

    [Fact]
    public void KindAndStateRoundTripThroughStorage()
    {
        foreach (var kind in Enum.GetValues<PhaseObligationKind>())
        {
            Assert.Equal(kind, PhaseProgressEvaluator.ParseKind(PhaseProgressEvaluator.ToStorage(kind)));
        }

        foreach (var state in Enum.GetValues<PhaseObligationState>())
        {
            Assert.Equal(state, PhaseProgressEvaluator.ParseState(PhaseProgressEvaluator.ToStorage(state)));
        }
    }
}
