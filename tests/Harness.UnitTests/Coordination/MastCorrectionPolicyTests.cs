using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Fase 1D — regressão permanente do consumo da distribuição MAST.
///
/// Classificar o modo de falha já era feito e morria como telemetria: um painel que ninguém
/// consultava na hora de decidir. O ponto da taxonomia nunca foi medir — é que cada categoria pede
/// uma correção DIFERENTE, e aplicar a correção errada é pior do que não corrigir.
/// </summary>
public sealed class MastCorrectionPolicyTests
{
    [Fact]
    public void ASingleOccurrenceIsAccidentNotPattern()
    {
        var correction = MastCorrectionPolicy.Evaluate(
        [
            new MastObservation("disobey_task_specification", MastCategory.SpecificationAndDesign, 1),
        ]);

        // Reagir a acidente faz o planejamento oscilar sem aprender nada.
        Assert.False(correction.HasSignal);
        Assert.Equal(MastCorrectionPolicy.ReasonNoSignal, correction.ReasonCode);
    }

    [Fact]
    public void RecurrentSpecificationFailureSlicesSmallerInsteadOfReviewingDeeper()
    {
        var correction = MastCorrectionPolicy.Evaluate(
        [
            new MastObservation("disobey_task_specification", MastCategory.SpecificationAndDesign, 3),
        ]);

        Assert.True(correction.HasSignal);
        Assert.True(correction.PreferSmallerSlices);
        Assert.True(correction.RequireExplicitAcceptanceCriteria);
        // Revisar mais fundo não conserta enunciado ambíguo: o revisor reprova de novo pelo mesmo
        // motivo e o custo dobra sem o defeito sair do lugar.
        Assert.Equal(0, correction.ExtraReviewDepth);
        Assert.Contains("3 vezes", correction.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurrentVerificationFailureIsTheOnlyOneThatDeepensReview()
    {
        var correction = MastCorrectionPolicy.Evaluate(
        [
            new MastObservation("premature_termination", MastCategory.VerificationAndTermination, 4),
        ]);

        Assert.True(correction.HasSignal);
        Assert.Equal(1, correction.ExtraReviewDepth);
        Assert.False(correction.PreferSmallerSlices);
    }

    [Fact]
    public void RecurrentMisalignmentSlicesSmallerAndDoesNotDeepenReview()
    {
        var correction = MastCorrectionPolicy.Evaluate(
        [
            new MastObservation("step_repetition", MastCategory.InterAgentMisalignment, 2),
        ]);

        Assert.True(correction.HasSignal);
        Assert.True(correction.PreferSmallerSlices);
        // Profundidade de revisão não arbitra conflito entre agentes.
        Assert.Equal(0, correction.ExtraReviewDepth);
    }

    [Fact]
    public void TheDominantModeDecidesAndTheDecisionCitesTheEvidence()
    {
        var correction = MastCorrectionPolicy.Evaluate(
        [
            new MastObservation("step_repetition", MastCategory.InterAgentMisalignment, 2),
            new MastObservation("premature_termination", MastCategory.VerificationAndTermination, 5),
        ]);

        // O modo mais frequente manda — e a decisão precisa poder ser citada, não só aplicada.
        Assert.Equal(MastCorrectionPolicy.ReasonVerification, correction.ReasonCode);
        Assert.Contains("premature_termination", correction.Evidence, StringComparison.Ordinal);
        Assert.Contains("5 vezes", correction.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDistributionAlwaysProducesTheSameCorrection()
    {
        MastObservation[] observations =
        [
            new("disobey_task_specification", MastCategory.SpecificationAndDesign, 3),
            new("premature_termination", MastCategory.VerificationAndTermination, 3),
        ];

        // Determinismo: sem ele, ninguém consegue dizer se a decomposição mudou pelo aprendizado
        // ou pela ordem em que os dados chegaram.
        Assert.Equal(
            MastCorrectionPolicy.Evaluate(observations),
            MastCorrectionPolicy.Evaluate(observations));
    }
}
