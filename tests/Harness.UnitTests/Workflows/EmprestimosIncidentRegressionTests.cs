using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// REGRESSÃO DO INCIDENTE REAL.
///
/// No primeiro teste da fábrica pediu-se "um sistema simples de empréstimos". O que saiu foi
/// `GET /emprestimos` devolvendo `{"emprestimos": []}` e nenhuma interface, com a justificativa de
/// que o usuário não havia informado a stack. Para quem pediu, nada foi entregue.
///
/// A auditoria do canon mostrou que não foi desobediência do modelo: não existia norma alguma
/// sobre o software que a fábrica constrói, e o executor preencheu a lacuna com juízo próprio.
///
/// Estes testes travam o comportamento nos quatro pontos da cadeia onde o incidente pôde
/// acontecer: a inferência do que foi pedido, a resolução do perfil sem stack informada, os
/// requisitos derivados da modalidade e o portão que recusa a entrega incompleta. Não constroem um
/// sistema de empréstimos — testam o Poseidon.
/// </summary>
public sealed class EmprestimosIncidentRegressionTests
{
    private const string TheRequest = "Crie um sistema simples de empréstimos.";

    [Fact]
    public void SemStackInformadaOPerfilResolveOBaselineCompleto()
    {
        var profile = Resolve();

        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.Equal(".NET 8", profile.Backend.Runtime);
        Assert.Equal("React", profile.Frontend.Framework);
        Assert.Equal("TypeScript", profile.Frontend.Language);
        Assert.Equal("Vite", profile.Frontend.BuildSystem);
        Assert.Equal("SQL Server", profile.Data.Database);
        Assert.Equal("REST", profile.Api.Protocol);
        Assert.True(profile.Api.OpenApiRequired);
        Assert.Equal("Modular Monolith + Clean Architecture", profile.Backend.ArchitectureStyle);

        // A propriedade que impede o incidente: ausência de escolha do usuário virou aplicação do
        // baseline, e não ausência de frontend.
        Assert.True(profile.Frontend.Required);
    }

    [Fact]
    public void AModalidadeExigeInterfaceEJornadaPontaAPonta()
    {
        var required = ProductDeliveryRequirements.For(Resolve());

        Assert.Contains(ProductEvidenceKind.FrontendPresent, required);
        Assert.Contains(ProductEvidenceKind.FrontendBuild, required);
        Assert.Contains(ProductEvidenceKind.FrontendBackendIntegration, required);
        Assert.Contains(ProductEvidenceKind.E2EJourneyPassed, required);
        Assert.Contains(ProductEvidenceKind.OpenApiGenerated, required);
        Assert.Contains(ProductEvidenceKind.PersistenceVerified, required);
    }

    [Fact]
    public void AEntregaDoIncidenteNaoPodeSerDeclaradaPronta()
    {
        // Exatamente o que a fábrica produziu: backend que compila, endpoint respondendo, teste do
        // endpoint verde. Nada de interface, nada de jornada.
        var oQueFoiEntregue = new[]
        {
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendPresent),
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendBuild),
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.ApiPresent),
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.AutomatedTestsPassed),
        };

        var verdict = ProductDeliveryGate.Evaluate(Resolve(), oQueFoiEntregue);

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.E2EJourneyPassed);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.OpenApiGenerated);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.PersistenceVerified);
    }

    [Fact]
    public void OPortaoDaFaseDeDesenvolvimentoRecusaAEntregaDoIncidente()
    {
        var verdict = ProductDeliveryGate.Evaluate(
            Resolve(),
            [
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendPresent),
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendBuild),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.ApiPresent),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.AutomatedTestsPassed),
            ]);

        // Mesmo no modo autônomo, com todas as obrigações documentais da fase aceitas e nenhum
        // achado impeditivo: o portão não abre.
        var decision = PhaseGatePolicy.Decide(
            ProjectOperationMode.Autonomous,
            "5-Desenvolvimento",
            "gate-desenvolvimento",
            null,
            new PhaseGateEvidence(
                HasGate: true,
                AllRequiredObligationsAccepted: true,
                HasBlockingFinding: false,
                HasOpenBlocker: false,
                RequiredObligationCount: 4,
                ProductDelivery: verdict));

        Assert.Equal(PhaseGateDecision.NotReady, decision);
    }

    [Fact]
    public void OMotivoDaRecusaDizExatamenteOQueFaltou()
    {
        var verdict = ProductDeliveryGate.Evaluate(
            Resolve(),
            [
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendPresent),
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendBuild),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.ApiPresent),
            ]);

        // Um diagnóstico que nomeia o que falta é a diferença entre "reprovado" e "reprovado por
        // ausência de frontend" — a primeira forma devolve o problema a quem pediu ajuda.
        Assert.StartsWith("product_dod_failed:", verdict.Summary(), StringComparison.Ordinal);
        Assert.Contains("frontendpresent:missing", verdict.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void OMesmoPedidoComExclusaoExplicitaDeixaDeExigirInterface()
    {
        // A regra não é "sempre frontend": é "frontend quando a modalidade resolvida o exige".
        var apiOnly = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "project", "Crie somente uma API de empréstimos.", []));

        var verdict = ProductDeliveryGate.Evaluate(
            apiOnly,
            [
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendPresent),
            ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.BackendBuild),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.ApiPresent),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.OpenApiGenerated),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.DatabaseMigrationValidated),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.PersistenceVerified),

                // Banco fixado pelo perfil (SQL Server, baseline) exige o driver DECLARADO na
                // entrega — lição do caso Indicadores (avaliação TrensRJ): migration sem driver
                // é persistência de fachada.
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.DataAccessDeclared),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.AutomatedTestsPassed),
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.RunbookPresent),

                // Segurança passou a ser exigida de toda entrega, inclusive API-only: segredo em
                // código não depende de haver tela.
                ProductDeliveryGateTests.Satisfied(ProductEvidenceKind.SecurityScanPassed),
            ]);

        Assert.True(verdict.Satisfied);
    }

    private static ProjectEffectiveProfile Resolve() =>
        EffectiveProfileResolver.Resolve(new EffectiveProfileInputs("project", TheRequest, []));
}
