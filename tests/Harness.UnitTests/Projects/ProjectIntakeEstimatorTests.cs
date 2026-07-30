using Harness.Modules.Projects.Application;

namespace Harness.UnitTests.Projects;

/// <summary>
/// D12: o dono leigo descreve o que precisa; quem estima risco e reconhece a pilha é o sistema.
/// Estes testes fixam as duas regras de honestidade da estimativa — não inventar tecnologia e,
/// na dúvida, ficar no meio.
/// </summary>
public sealed class ProjectIntakeEstimatorTests
{
    [Fact]
    public void ObjectiveWithoutRiskSignalStaysMedium()
    {
        var estimate = ProjectIntakeEstimator.EstimateCriticality(
            "Agenda da equipe",
            "Quero organizar os horários das reuniões da equipe em um quadro.");

        Assert.Equal("medium", estimate.Criticality);
        Assert.Equal(ProjectIntakeEstimator.ReasonNoSignal, estimate.ReasonCode);
    }

    [Fact]
    public void MoneySignalRaisesToHigh()
    {
        var estimate = ProjectIntakeEstimator.EstimateCriticality(
            "Loja de bolos",
            "Catálogo de sabores com pagamento por Pix na entrega.");

        Assert.Equal("high", estimate.Criticality);
        Assert.Equal(ProjectIntakeEstimator.ReasonMoney, estimate.ReasonCode);
    }

    /// <summary>O caso que expôs o defeito: pagamento + dados bancários não podem virar `medium`.</summary>
    [Fact]
    public void MoneyAndSensitiveDataTogetherReachCritical()
    {
        var estimate = ProjectIntakeEstimator.EstimateCriticality(
            "Pagamentos com cartao",
            "Sistema de pagamento com cartão de crédito e dados bancários de clientes.");

        Assert.Equal("critical", estimate.Criticality);
    }

    [Fact]
    public void DeclaredSimpleScopeGoesLow()
    {
        var estimate = ProjectIntakeEstimator.EstimateCriticality(
            "Portfólio da Ana",
            "Uma landing page simples para mostrar meus trabalhos.");

        Assert.Equal("low", estimate.Criticality);
        Assert.Equal(ProjectIntakeEstimator.ReasonSimpleScope, estimate.ReasonCode);
    }

    /// <summary>Dinheiro vence "site simples": o sinal de risco não é anulado pelo tamanho.</summary>
    [Fact]
    public void RiskSignalWinsOverSimpleScope()
    {
        var estimate = ProjectIntakeEstimator.EstimateCriticality(
            "Loja",
            "Uma landing page simples com checkout e cobrança por boleto.");

        Assert.Equal("high", estimate.Criticality);
    }

    [Fact]
    public void EveryEstimateExplainsItselfInBusinessLanguage()
    {
        foreach (var (name, description) in new[]
                 {
                     ("Agenda", "Organizar reuniões."),
                     ("Loja", "Pagamento por cartão."),
                     ("Clínica", "Prontuário de paciente com dados pessoais e cobrança."),
                     ("Portfólio", "Uma landing page simples."),
                 })
        {
            var estimate = ProjectIntakeEstimator.EstimateCriticality(name, description);

            Assert.False(string.IsNullOrWhiteSpace(estimate.Rationale));
            foreach (var jargon in new[] { "risk_tier", "criticality", "high", "critical", "medium", "low" })
            {
                Assert.DoesNotContain(jargon, estimate.Rationale, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TechnologiesAreExtractedFromWhatTheOwnerWrote()
    {
        var technologies = ProjectIntakeEstimator.InferTechnologies(
            "Pagamentos",
            "Usar React no site e API em Python com Postgres.");

        Assert.Equal(["PostgreSQL", "Python", "React"], technologies);
    }

    /// <summary>A regra que impede o plano de nascer sobre pilha imaginada.</summary>
    [Fact]
    public void TechnologiesAreNeverInventedWhenTheOwnerNamesNone()
    {
        var technologies = ProjectIntakeEstimator.InferTechnologies(
            "Loja de bolos da Dona Marta",
            "Quero um site onde meus clientes vejam os sabores e façam encomenda.");

        Assert.Empty(technologies);
    }

    [Fact]
    public void TechnologyMatchingRespectsWordBoundaries()
    {
        // "Java" não pode sair de "JavaScript", nem "Go" de "Google".
        Assert.Equal(["JavaScript"], ProjectIntakeEstimator.InferTechnologies(null, "Feito em JavaScript."));
        Assert.Equal(["Google Cloud"], ProjectIntakeEstimator.InferTechnologies(null, "Hospedado no Google Cloud."));
        Assert.Equal(["Go"], ProjectIntakeEstimator.InferTechnologies(null, "Backend em Go."));
    }

    [Fact]
    public void AccentsAndCaseDoNotChangeTheOutcome()
    {
        var withAccent = ProjectIntakeEstimator.EstimateCriticality(null, "Cobrança com cartão de crédito.");
        var withoutAccent = ProjectIntakeEstimator.EstimateCriticality(null, "COBRANCA COM CARTAO DE CREDITO.");

        Assert.Equal(withAccent.Criticality, withoutAccent.Criticality);
        Assert.Equal("high", withAccent.Criticality);
    }

    [Fact]
    public void EstimateIsDeterministic()
    {
        var first = ProjectIntakeEstimator.EstimateCriticality("Clínica", "Prontuário e pagamento.");
        var second = ProjectIntakeEstimator.EstimateCriticality("Clínica", "Prontuário e pagamento.");

        Assert.Equal(first, second);
    }

    [Fact]
    public void EmptyInputIsHandledWithoutSignal()
    {
        var estimate = ProjectIntakeEstimator.EstimateCriticality(null, null);

        Assert.Equal("medium", estimate.Criticality);
        Assert.Empty(ProjectIntakeEstimator.InferTechnologies(null, null));
    }
}
