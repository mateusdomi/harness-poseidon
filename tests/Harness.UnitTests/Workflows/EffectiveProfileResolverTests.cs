using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O perfil efetivo é o <i>constraint profile</i> que o gate da Fase 3 já cobrava e que não
/// existia. Estes testes travam as três propriedades que o tornam útil: ele resolve o silêncio do
/// usuário aplicando o baseline, respeita override sem abandonar as demais dimensões, e obedece à
/// precedência — na qual a preferência do agente não aparece.
/// </summary>
public sealed class EffectiveProfileResolverTests
{
    [Fact]
    public void SilenceResolvesToTheBaselineInsteadOfAnUndefinedStack()
    {
        var profile = Resolve("Quero um sistema para controlar empréstimos.");

        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.Equal(".NET 8", profile.Backend.Runtime);
        Assert.Equal("ASP.NET Core 8", profile.Backend.Framework);
        Assert.Equal("Modular Monolith + Clean Architecture", profile.Backend.ArchitectureStyle);
        Assert.True(profile.Frontend.Required);
        Assert.Equal("React", profile.Frontend.Framework);
        Assert.Equal("TypeScript", profile.Frontend.Language);
        Assert.Equal("Vite", profile.Frontend.BuildSystem);
        Assert.Equal("SQL Server", profile.Data.Database);
        Assert.Equal("REST", profile.Api.Protocol);
        Assert.True(profile.Api.OpenApiRequired);
        Assert.Empty(profile.Overrides);
    }

    [Fact]
    public void OverrideOfOneDimensionDoesNotAbandonTheBaselineInTheOthers()
    {
        var profile = Resolve(
            "Quero um sistema de empréstimos.",
            new ProfileDirective(
                ProfileAuthority.ProjectRequirement,
                EffectiveProfileResolver.AreaFrontendFramework,
                "Angular",
                "Padronização corporativa"));

        Assert.Equal("Angular", profile.Frontend.Framework);
        Assert.Equal(".NET 8", profile.Backend.Runtime);
        Assert.Equal("SQL Server", profile.Data.Database);

        var recorded = Assert.Single(profile.Overrides);
        Assert.Equal(EffectiveProfileResolver.AreaFrontendFramework, recorded.Area);
        Assert.Equal("React", recorded.DefaultValue);
        Assert.Equal("Angular", recorded.OverrideValue);
        Assert.Equal("Padronização corporativa", recorded.Reason);
    }

    [Fact]
    public void HigherAuthorityWinsOverLowerAuthority()
    {
        var profile = Resolve(
            "Sistema de empréstimos.",
            new ProfileDirective(
                ProfileAuthority.ProjectRequirement,
                EffectiveProfileResolver.AreaDatabase, "PostgreSQL", "Preferência do time"),
            new ProfileDirective(
                ProfileAuthority.Regulatory,
                EffectiveProfileResolver.AreaDatabase, "Oracle", "Exigência regulatória do cliente"));

        Assert.Equal("Oracle", profile.Data.Database);
        var recorded = Assert.Single(profile.Overrides, item =>
            item.Area == EffectiveProfileResolver.AreaDatabase);
        Assert.Equal(ProfileAuthority.Regulatory, recorded.Authority);
    }

    [Fact]
    public void ApprovedDecisionCarriesItsAdrIntoTheOverrideRecord()
    {
        var profile = Resolve(
            "Sistema de empréstimos.",
            new ProfileDirective(
                ProfileAuthority.ApprovedDecision,
                EffectiveProfileResolver.AreaArchitectureStyle,
                "Microsserviços",
                "Escala independente por domínio",
                AdrId: "ADR-0042"));

        var recorded = Assert.Single(profile.Overrides);
        Assert.Equal("ADR-0042", recorded.AdrId);
    }

    [Fact]
    public void ExplicitApiOnlyRemovesTheFrontendRequirementButKeepsTheContract()
    {
        var profile = Resolve("Quero somente uma API de empréstimos.");

        Assert.Equal(ProductModality.ApiOnly, profile.Modality);
        Assert.False(profile.Frontend.Required);
        Assert.Null(profile.Frontend.Framework);
        Assert.True(profile.Api.Required);
        Assert.True(profile.Api.OpenApiRequired);
        Assert.True(profile.Data.Required);
    }

    [Theory]
    [InlineData("Quero um sistema de empréstimos", ProductModality.Web)]
    [InlineData("Preciso de um portal de cadastro de clientes", ProductModality.Web)]
    [InlineData("Uma aplicação para gestão de contratos", ProductModality.Web)]
    [InlineData("Somente API de consulta de saldo", ProductModality.ApiOnly)]
    [InlineData("Um worker que processa a fila todo dia", ProductModality.Worker)]
    [InlineData("Um executável desktop para o operador", ProductModality.Desktop)]
    [InlineData("Um aplicativo mobile para os vendedores", ProductModality.Mobile)]
    [InlineData("Uma biblioteca para validar CPF", ProductModality.Library)]
    public void ModalityIsInferredFromTheDemandProse(string demand, ProductModality expected) =>
        Assert.Equal(expected, ProductModalityInference.Infer(demand));

    [Fact]
    public void ExplicitExclusionBeatsTheWordThatSuggestsAProduct()
    {
        // "sistema" sugere produto operado por pessoa; a exclusão explícita vence.
        Assert.Equal(
            ProductModality.ApiOnly,
            ProductModalityInference.Infer("Quero um sistema de empréstimos, mas somente API."));
    }

    [Fact]
    public void SilenceIsNeverInferredAsApiOnly()
    {
        // Deixar `Unspecified` obriga a Triagem a resolver com o usuário. Inferir "API" a partir do
        // silêncio é exatamente o erro que entregou um endpoint no lugar de um sistema.
        Assert.Equal(ProductModality.Unspecified, ProductModalityInference.Infer("faça aquilo que combinamos"));
        Assert.Equal(ProductModality.Unspecified, ProductModalityInference.Infer(null));
    }

    [Fact]
    public void ProfileRoundTripsThroughJsonAndHasAStableFingerprint()
    {
        var profile = Resolve("Sistema de empréstimos.");

        var restored = ProjectEffectiveProfile.FromJson(profile.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(profile.Modality, restored.Modality);
        Assert.Equal(profile.Frontend.Framework, restored.Frontend.Framework);
        Assert.Equal(profile.Fingerprint(), restored.Fingerprint());
    }

    [Fact]
    public void CorruptedProfileNeverDeserializesIntoAnEmptyProfileThatPassesEverything()
    {
        Assert.Null(ProjectEffectiveProfile.FromJson("{ isto não é json"));
        Assert.Null(ProjectEffectiveProfile.FromJson(""));
    }

    [Fact]
    public void ResolutionRecordsItsOwnSourcesSoTheDecisionIsAuditable()
    {
        var profile = Resolve(
            "Sistema de empréstimos.",
            new ProfileDirective(
                ProfileAuthority.OrganizationConstraint,
                EffectiveProfileResolver.AreaHosting, "Azure App Service", "Padrão da organização"));

        Assert.Contains($"baseline:{ProjectEffectiveProfile.CurrentBaselineVersion}", profile.ResolutionSources);
        Assert.Contains(
            profile.ResolutionSources,
            source => source.StartsWith("operation.hosting:", StringComparison.Ordinal));
        Assert.Contains(
            profile.Constraints,
            constraint => constraint.Contains("Azure App Service", StringComparison.Ordinal));
    }

    private static ProjectEffectiveProfile Resolve(string demand, params ProfileDirective[] directives) =>
        EffectiveProfileResolver.Resolve(new EffectiveProfileInputs("project", demand, directives));
}
