using Harness.Host.Product;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// A prova de PRONTIDÃO DA PLATAFORMA, não de uma entrega.
///
/// A pergunta que este arquivo responde é diferente de todas as outras da suíte: não "esta entrega
/// está pronta?", mas <b>"o Poseidon tem como julgar uma entrega Web padrão sem buraco silencioso?"</b>
///
/// Buraco silencioso é o modo de falha que interessa aqui. Um requisito exigido pelo perfil, sem
/// nenhum verificador capaz de produzi-lo, não se anuncia: ele simplesmente nunca aparece no
/// conjunto de evidências, e o portão reprova por "ausente" como se o problema fosse do produto.
/// Enquanto essa confusão existir, ninguém consegue distinguir uma entrega incompleta de uma
/// plataforma incompleta — e o Golden Run começaria sem saber qual das duas está sendo medido.
///
/// O registro examinado é o CANÔNICO (<see cref="ProductVerifierCatalog"/>), o mesmo que o Host
/// injeta. Montar uma lista própria aqui provaria apenas que a lista deste arquivo está completa.
/// </summary>
public sealed class GoldenRunReadinessTests
{
    /// <summary>O perfil default do Golden Run: Web, .NET, React/TS/Vite, SQL Server, REST, OpenAPI.</summary>
    private static ProjectEffectiveProfile WebDefault() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs(
            "golden-run", "Crie um sistema web de controle de empréstimos de livros.", []));

    private static ProductVerificationPlan Plan()
    {
        var runner = new ProductVerificationRunner(
            ProductVerifierCatalog.CreateAll(new TrustedProcessRunner()));
        return ProductVerificationPlan.From(
            WebDefault(), runner.NativeVerifiers, runner.ProjectControlledVerifiers);
    }

    [Fact]
    public void OPerfilDefaultDoGoldenRunEWebComContratoEPersistencia()
    {
        var profile = WebDefault();

        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.True(profile.Backend.Required);
        Assert.True(profile.Frontend.Required);
        Assert.True(profile.Api.Required);
        Assert.True(profile.Api.OpenApiRequired);
        Assert.True(profile.Data.Required);
    }

    [Fact]
    public void NenhumRequisitoObrigatorioFicaSemVerificador()
    {
        var plan = Plan();

        // A asserção central da missão. Cada item desta lista é um requisito que o perfil exige e
        // que ninguém sabe verificar: enquanto ela não estiver vazia, a plataforma não está pronta
        // para o Golden Run, por mais verde que a suíte esteja.
        Assert.Empty(plan.Unsupported);
    }

    [Fact]
    public void TodoRequisitoObrigatorioTemProvedorProvenienciaEConfiancaDeclaradas()
    {
        var plan = Plan();
        var required = plan.Steps.Where(step => step.Required).ToList();

        Assert.NotEmpty(required);
        foreach (var step in required)
        {
            Assert.NotEqual(ProductVerificationDisposition.NotApplicable, step.Disposition);
            Assert.NotEqual(ProductVerificationDisposition.RequiredUnsupported, step.Disposition);

            // Proveniência mínima nunca pode ser `Declared`: seria dizer que afirmação do executor
            // satisfaz o requisito, que é exatamente o que este subsistema existe para impedir.
            Assert.True(
                step.MinimumProvenance >= ProductEvidenceProvenance.Observed,
                $"{step.Kind} aceitaria evidência {step.MinimumProvenance}.");

            Assert.False(
                string.IsNullOrWhiteSpace(step.PreferredVerifier) &&
                    step.Disposition == ProductVerificationDisposition.RequiredNative,
                $"{step.Kind} está marcada como nativa e não nomeia o verificador.");
        }
    }

    /// <summary>
    /// Os quatro requisitos que a Fase 6 deixou em aberto e que esta missão existiu para fechar.
    /// Ficam nomeados um a um de propósito: uma asserção agregada continuaria verde se algum deles
    /// regredisse para prova controlada pelo produto.
    /// </summary>
    [Theory]
    [InlineData(ProductEvidenceKind.OpenApiGenerated, "openapi-native")]
    [InlineData(ProductEvidenceKind.E2EJourneyPassed, "playwright-journey")]
    [InlineData(ProductEvidenceKind.PersistenceVerified, "persistence-native")]
    [InlineData(ProductEvidenceKind.SecurityScanPassed, "security-baseline")]
    [InlineData(ProductEvidenceKind.FrontendBackendIntegration, "playwright-journey:traffic")]
    public void OsRequisitosCriticosExigemProvaControladaPeloPoseidon(
        ProductEvidenceKind kind, string expectedVerifier)
    {
        var step = Plan().Steps.Single(item => item.Kind == kind);

        Assert.True(step.Required, $"{kind} deixou de ser exigida do perfil Web default.");
        Assert.Equal(ProductEvidenceProvenance.Verified, step.MinimumProvenance);
        Assert.Equal(VerificationTrustLevel.PoseidonControlled, step.MinimumTrust);
        Assert.Contains(expectedVerifier.Split(':')[0], step.PreferredVerifier ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void RequisitoDeExistenciaSeSatisfazComConstatacaoENaoExigeExecucao()
    {
        var plan = Plan();

        foreach (var kind in (ProductEvidenceKind[])
        [
            ProductEvidenceKind.BackendPresent,
            ProductEvidenceKind.FrontendPresent,
            ProductEvidenceKind.ApiPresent,
            ProductEvidenceKind.RunbookPresent,
            ProductEvidenceKind.DatabaseMigrationValidated,
        ])
        {
            var step = plan.Steps.Single(item => item.Kind == kind);
            Assert.Equal(ProductEvidenceProvenance.Observed, step.MinimumProvenance);
        }
    }

    /// <summary>
    /// A distinção do §16, provada em vez de documentada: um requisito que a modalidade não exige é
    /// `NotApplicable`; um requisito exigido sem verificador é `RequiredUnsupported`. Sem esta
    /// separação, apagar um verificador do registro faria o requisito parecer dispensado.
    /// </summary>
    [Fact]
    public void RequisitoSemVerificadorNaoEConfundidoComRequisitoNaoAplicavel()
    {
        var semNativos = ProductVerificationPlan.From(WebDefault());

        var e2e = semNativos.Steps.Single(step => step.Kind == ProductEvidenceKind.E2EJourneyPassed);
        Assert.True(e2e.Required);
        Assert.Equal(ProductVerificationDisposition.RequiredUnsupported, e2e.Disposition);
        Assert.DoesNotContain(ProductEvidenceKind.E2EJourneyPassed, semNativos.NotApplicable);
        Assert.Contains(ProductEvidenceKind.E2EJourneyPassed, semNativos.Unsupported);
    }

    /// <summary>
    /// A modalidade API-only continua dispensando o que não existe naquele produto — e isso É
    /// `NotApplicable`, com todas as letras. A prontidão não pode ser obtida exigindo menos.
    /// </summary>
    [Fact]
    public void ApiOnlyDispensaTelaEJornadaSemVirarBuracoDeVerificacao()
    {
        var runner = new ProductVerificationRunner(
            ProductVerifierCatalog.CreateAll(new TrustedProcessRunner()));
        var apiOnly = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("golden-run", "Crie somente uma API de empréstimos.", []));
        var plan = ProductVerificationPlan.From(
            apiOnly, runner.NativeVerifiers, runner.ProjectControlledVerifiers);

        Assert.Contains(ProductEvidenceKind.FrontendBuild, plan.NotApplicable);
        Assert.Contains(ProductEvidenceKind.E2EJourneyPassed, plan.NotApplicable);
        Assert.Empty(plan.Unsupported);

        // O que sobrevive à ausência de tela: contrato, persistência, testes e segurança.
        Assert.Contains(ProductEvidenceKind.OpenApiGenerated, plan.Required);
        Assert.Contains(ProductEvidenceKind.PersistenceVerified, plan.Required);
        Assert.Contains(ProductEvidenceKind.SecurityScanPassed, plan.Required);
    }
}
