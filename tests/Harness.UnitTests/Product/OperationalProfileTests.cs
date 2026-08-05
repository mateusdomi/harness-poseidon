using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// O perfil operacional com proveniência. A regra que ele existe para impor: nenhum número de NFR
/// é inventado em silêncio — em particular carga, porque um número de concorrência chutado calibra
/// o teste inteiro para o cenário errado e produz uma prova sobre nada.
/// </summary>
public sealed class OperationalProfileTests
{
    [Fact]
    public void FonteDeRequisitosVenceDefaultDaOrganizacaoEBaseline()
    {
        var profile = OperationalProfileResolver.Resolve(
            new Dictionary<string, string> { ["accessibility"] = "WCAG 2.2 AAA" },
            new Dictionary<string, string> { ["accessibility"] = "WCAG 2.1 AA" });

        Assert.Equal("WCAG 2.2 AAA", profile.Accessibility.Value);
        Assert.Equal(NfrProvenance.RequirementSource, profile.Accessibility.Provenance);
        Assert.False(profile.Accessibility.IsAssumption);
    }

    [Fact]
    public void DefaultDaOrganizacaoEntraComoPremissaRegistrada()
    {
        var profile = OperationalProfileResolver.Resolve(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["availability"] = "24x7 com janela de manutenção" });

        Assert.Equal(NfrProvenance.OrganizationDefault, profile.Availability.Provenance);
        Assert.True(profile.Availability.IsAssumption);
        Assert.Contains(
            profile.Assumptions, item => item.StartsWith("availability=", StringComparison.Ordinal));
    }

    [Fact]
    public void BaselineCobreQualidadeMasNuncaCarga()
    {
        var profile = OperationalProfileResolver.Resolve(new Dictionary<string, string>());

        // Qualidade tem default honesto…
        Assert.Equal("WCAG 2.2 AA", profile.Accessibility.Value);
        Assert.Equal(NfrProvenance.Baseline, profile.Accessibility.Provenance);

        // …carga não tem: fica declaradamente aberta.
        Assert.Equal(NfrProvenance.Unanswered, profile.ConcurrentUsers.Provenance);
        Assert.Contains("concurrent_users", profile.Open);
    }

    /// <summary>
    /// Teste de carga sem carga conhecida é prova sobre cenário inventado. A aplicabilidade deriva
    /// do perfil — nunca de exigência cega.
    /// </summary>
    [Fact]
    public void LoadTestSoSeAplicaComCargaRespondidaOuAssumidaDeFonteDeclarada()
    {
        var semCarga = OperationalProfileResolver.Resolve(new Dictionary<string, string>());
        Assert.False(semCarga.LoadTestApplicable);

        var comCarga = OperationalProfileResolver.Resolve(
            new Dictionary<string, string> { ["concurrent_users"] = "40" });
        Assert.True(comCarga.LoadTestApplicable);

        var comDefaultDaOrg = OperationalProfileResolver.Resolve(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["peak_load"] = "200 req/min" });
        Assert.True(comDefaultDaOrg.LoadTestApplicable);
        Assert.True(comDefaultDaOrg.PeakLoad.IsAssumption);
    }

    [Fact]
    public void DimensoesAbertasSaoListadasParaOPlanoDePerguntas()
    {
        var profile = OperationalProfileResolver.Resolve(new Dictionary<string, string>());

        Assert.Contains("latency_target", profile.Open);
        Assert.Contains("rto", profile.Open);
        Assert.DoesNotContain("accessibility", profile.Open);
    }
}
