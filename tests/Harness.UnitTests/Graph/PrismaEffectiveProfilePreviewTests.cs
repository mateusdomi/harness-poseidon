using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Prisma Launch Gate (bloco 19) — o PREVIEW do Effective Profile, resolvido pelo RESOLVER real
/// a partir da solicitação REAL enviada à Bruna (fixture extraída do banco em 2026-08-05), sem
/// nenhum valor escrito à mão. O que este teste congela é a resposta à pergunta "com os
/// artefatos de hoje, que perfil o Prisma recebe?" — e as FONTES de cada decisão.
/// </summary>
public sealed class PrismaEffectiveProfilePreviewTests
{
    private static readonly Lazy<string> Solicitation = new(() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "prisma-solicitacao-real.txt")));

    [Fact]
    public void ASolicitacaoRealProduzOPerfilEsperadoComProveniencia()
    {
        // A mensagem do dono é ProjectRequirement: autoridade acima do baseline, abaixo de
        // decisão aprovada e de regra regulatória — exatamente a precedência do canon.
        var directives = ProfileDirectiveParser.Parse(
            Solicitation.Value,
            ProfileAuthority.ProjectRequirement,
            "01KZ8RCC8MY27RW562STXSQMXK",
            "solicitation");

        var profile = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "01KZ7ZNTXRRQMMA9G1TQ2H5BFW",
            Solicitation.Value,
            directives));

        // Produto Web com frontend OBRIGATÓRIO (há protótipo fornecido, não hipótese).
        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.True(profile.Frontend.Required);
        Assert.Equal("React", profile.Frontend.Framework);

        // Backend do baseline (.NET/ASP.NET Core — nenhuma diretiva o contraria).
        Assert.Contains(".NET", profile.Backend.Runtime, StringComparison.OrdinalIgnoreCase);

        // O banco vem da SOLICITAÇÃO (Oracle, requisito da TrensRJ) — override com proveniência,
        // nunca o default do baseline.
        Assert.Equal("Oracle", profile.Data.Database);
        var oracle = Assert.Single(
            profile.Overrides, over => over.Area == EffectiveProfileResolver.AreaDatabase);
        Assert.Equal(ProfileAuthority.ProjectRequirement, oracle.Authority);
        Assert.Equal("Oracle", oracle.OverrideValue);

        // API REST com OpenAPI obrigatório (baseline).
        Assert.Contains("REST", profile.Api.Protocol, StringComparison.OrdinalIgnoreCase);
        Assert.True(profile.Api.OpenApiRequired);

        // Proveniência declarada: o perfil DIZ de onde veio cada resolução.
        Assert.NotEmpty(profile.ResolutionSources);
    }

    /// <summary>
    /// A regressão do defeito que este preview capturou: "ios" casava por SUBSTRING dentro de
    /// "critérios", e qualquer texto em português com "critérios/relatórios/usuários" virava
    /// produto Mobile. Termo de modalidade casa como palavra, nunca como pedaço de palavra.
    /// </summary>
    [Theory]
    [InlineData("Sistema web com 26 critérios de aceite e relatórios para usuários.", ProductModality.Web)]
    [InlineData("Quero um aplicativo mobile para iOS e Android.", ProductModality.Mobile)]
    public void TermoDeModalidadeCasaComoPalavraNuncaComoSubstring(
        string demanda, ProductModality esperada)
    {
        Assert.Equal(esperada, ProductModalityInference.Infer(demanda));
    }
}
