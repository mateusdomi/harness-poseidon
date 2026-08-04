using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// As decisões humanas chegando ao perfil. O modelo de precedência existia desde a Fase 2; o que
/// faltava era a ponte entre o que a pessoa escreveu e a diretiva tipada.
///
/// A propriedade central do parser é PRECISÃO, não cobertura: frase ambígua não vira decisão. Uma
/// decisão inventada é pior que uma decisão ausente, porque tem aparência de aprovação.
/// </summary>
public sealed class ProfileDirectiveParserTests
{
    [Fact]
    public void PedidoExplicitoDoUsuarioViraDiretivaDeRequisitoDoProjeto()
    {
        var directives = ProfileDirectiveParser.Parse(
            "Quero o frontend em Angular.", ProfileAuthority.ProjectRequirement, "sol-1", "solicitation");

        var directive = Assert.Single(directives);
        Assert.Equal(EffectiveProfileResolver.AreaFrontendFramework, directive.Area);
        Assert.Equal("Angular", directive.Value);
        Assert.Equal(ProfileAuthority.ProjectRequirement, directive.Authority);
        Assert.Contains("solicitation:sol-1", directive.Reason, StringComparison.Ordinal);
        Assert.Contains("Quero o frontend em Angular", directive.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AdrAprovadoViraDecisaoComAutoridadeDeDecisaoAprovada()
    {
        var directives = ProfileDirectiveParser.Parse(
            "ADR-0007 — O banco deve ser PostgreSQL por exigência do cliente.",
            ProfileAuthority.ApprovedDecision, "doc-9", "adr", "ADR-0007");

        var directive = Assert.Single(directives);
        Assert.Equal(EffectiveProfileResolver.AreaDatabase, directive.Area);
        Assert.Equal("PostgreSQL", directive.Value);
        Assert.Equal("ADR-0007", directive.AdrId);
    }

    [Fact]
    public void MencaoSemVerboDeDecisaoNaoViraDiretiva()
    {
        // "O time conhece Angular" é contexto, não escolha.
        Assert.Empty(ProfileDirectiveParser.Parse(
            "O time conhece Angular e React.", ProfileAuthority.ProjectRequirement, "s", "solicitation"));
    }

    [Fact]
    public void DecisaoNegadaNaoViraDiretivaPositiva()
    {
        // "não vamos usar MongoDB" jamais pode virar "usaremos MongoDB".
        Assert.Empty(ProfileDirectiveParser.Parse(
            "Não vamos usar MongoDB neste projeto.",
            ProfileAuthority.ProjectRequirement, "s", "solicitation"));
    }

    [Fact]
    public void VerboDeUmaFraseNaoAutorizaTecnologiaDeOutra()
    {
        // Sem a separação por frase, o "usar" da primeira autorizaria o "Oracle" da segunda.
        var directives = ProfileDirectiveParser.Parse(
            "Vamos usar PostgreSQL. O cliente antigo tinha Oracle.",
            ProfileAuthority.ProjectRequirement, "s", "solicitation");

        var directive = Assert.Single(directives);
        Assert.Equal("PostgreSQL", directive.Value);
    }

    [Fact]
    public void TextoSemDecisaoTecnicaProduzSilencioENaoPalpite()
    {
        Assert.Empty(ProfileDirectiveParser.Parse(
            "Crie um sistema simples de empréstimos.",
            ProfileAuthority.ProjectRequirement, "s", "solicitation"));
    }
}

/// <summary>A precedência entre autoridades, ponta a ponta, no resolvedor.</summary>
public sealed class ProfileAuthorityPrecedenceTests
{
    [Fact]
    public void SemDiretivaOBaselineVale()
    {
        var profile = Resolve();

        Assert.Equal("React", profile.Frontend.Framework);
        Assert.Equal(".NET 8", profile.Backend.Runtime);
        Assert.Equal("SQL Server", profile.Data.Database);
    }

    [Fact]
    public void RequisitoDoProjetoVenceOBaseline()
    {
        var profile = Resolve(Directive(ProfileAuthority.ProjectRequirement, "Angular"));

        Assert.Equal("Angular", profile.Frontend.Framework);
        Assert.Equal(".NET 8", profile.Backend.Runtime);
    }

    [Fact]
    public void ConstraintDaOrganizacaoVenceOBaselineEPerdeParaORequisito()
    {
        Assert.Equal(
            "Angular",
            Resolve(Directive(ProfileAuthority.OrganizationConstraint, "Angular")).Frontend.Framework);

        Assert.Equal(
            "Vue",
            Resolve(
                Directive(ProfileAuthority.OrganizationConstraint, "Angular"),
                Directive(ProfileAuthority.ProjectRequirement, "Vue")).Frontend.Framework);
    }

    [Fact]
    public void DecisaoAprovadaVenceORequisitoDoProjeto()
    {
        var profile = Resolve(
            Directive(ProfileAuthority.ProjectRequirement, "Vue"),
            Directive(ProfileAuthority.ApprovedDecision, "Angular", "ADR-0042"));

        Assert.Equal("Angular", profile.Frontend.Framework);
        var recorded = Assert.Single(profile.Overrides);
        Assert.Equal(ProfileAuthority.ApprovedDecision, recorded.Authority);
        Assert.Equal("ADR-0042", recorded.AdrId);
    }

    [Fact]
    public void RestricaoRegulatoriaVenceTodas()
    {
        var profile = Resolve(
            Directive(ProfileAuthority.OrganizationConstraint, "Angular"),
            Directive(ProfileAuthority.ProjectRequirement, "Vue"),
            Directive(ProfileAuthority.ApprovedDecision, "Svelte", "ADR-0042"),
            Directive(ProfileAuthority.Regulatory, "Blazor"));

        Assert.Equal("Blazor", profile.Frontend.Framework);
    }

    [Fact]
    public void DuasDecisoesIncompativeisDaMesmaAutoridadeNaoSaoResolvidasEmSilencio()
    {
        var exception = Assert.Throws<ProfileResolutionConflictException>(() => Resolve(
            Directive(ProfileAuthority.ApprovedDecision, "Angular", "ADR-0007"),
            Directive(ProfileAuthority.ApprovedDecision, "Vue", "ADR-0008")));

        var conflict = Assert.Single(exception.Conflicts);
        Assert.StartsWith("profile_resolution_conflict:frontend.framework:approveddecision", conflict, StringComparison.Ordinal);
        Assert.Contains("Angular", conflict, StringComparison.Ordinal);
        Assert.Contains("Vue", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void OPerfilRegistraFonteEAutoridadeDaDecisao()
    {
        var profile = Resolve(Directive(ProfileAuthority.ApprovedDecision, "PostgreSQL", "ADR-0007",
            EffectiveProfileResolver.AreaDatabase));

        var recorded = Assert.Single(profile.Overrides);
        Assert.Equal(EffectiveProfileResolver.AreaDatabase, recorded.Area);
        Assert.Equal("SQL Server", recorded.DefaultValue);
        Assert.Equal("PostgreSQL", recorded.OverrideValue);
        Assert.Equal(ProfileAuthority.ApprovedDecision, recorded.Authority);
        Assert.Equal("ADR-0007", recorded.AdrId);
        Assert.Contains(
            profile.ResolutionSources,
            source => source.StartsWith("data.database:approveddecision", StringComparison.Ordinal));
    }

    private static ProfileDirective Directive(
        ProfileAuthority authority,
        string value,
        string? adr = null,
        string area = "frontend.framework") =>
        new(authority, area, value, $"decidido por {authority}", adr);

    private static ProjectEffectiveProfile Resolve(params ProfileDirective[] directives) =>
        EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "projeto", "Crie um sistema simples de empréstimos.", directives));
}
