using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// Dual Project Gate, Partes C/E/F/G/L — o levantamento REAL do sistema de Indicadores
/// (fixture fiel, 1.194 linhas), o pior caso de intake: um documento escrito como prompt para
/// IA, misturando requisitos, sugestões com três backends alternativos, roadmap, "avaliar" e
/// instruções de processo em sete fases. Estes testes congelam o que o Poseidon EXTRAI dele —
/// e, mais importante, o que ele se RECUSA a extrair.
/// </summary>
public sealed class IndicadoresDocumentTests
{
    private static readonly Lazy<string> Levantamento = new(() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "indicadores-levantamento-real.txt")));

    // ── PARTE C — normalização do documento inteiro ───────────────────────────────────────

    /// <summary>
    /// O documento cita Java, C#, Node, React, Vite, Next.js, Kubernetes, Docker — e a ÚNICA
    /// diretiva de perfil que pode sair dele é o Oracle ("Utilizar banco de dados Oracle").
    /// Sugestão de stack, alternativas de backend e evolução futura INFORMAM; não decidem.
    /// </summary>
    [Fact]
    public void DoDocumentoInteiroSoOracleViraDiretivaDePerfil()
    {
        var directives = ProfileDirectiveParser.Parse(
            Levantamento.Value, ProfileAuthority.ProjectRequirement,
            "indicadores-levantamento", "solicitation");

        var oracle = Assert.Single(directives);
        Assert.Equal(EffectiveProfileResolver.AreaDatabase, oracle.Area);
        Assert.Equal("Oracle", oracle.Value);
    }

    /// <summary>Parte L — o Effective Profile preview do Projeto B, resolvido do documento real.</summary>
    [Fact]
    public void OPerfilPreviewDoProjetoBEWebOracleComBackendDoBaseline()
    {
        var directives = ProfileDirectiveParser.Parse(
            Levantamento.Value, ProfileAuthority.ProjectRequirement,
            "indicadores-levantamento", "solicitation");
        var profile = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "01KZ9MWXYXJZCJ0VAMWQHAQHDC", Levantamento.Value, directives));

        // "sistema web responsivo" → Web, frontend obrigatório.
        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.True(profile.Frontend.Required);
        // O baseline resolve frontend/backend (React/.NET) — o documento sugeriu três backends
        // e NENHUM virou decisão: Java aparecer no texto não faz o projeto ser Java.
        Assert.Equal("React", profile.Frontend.Framework);
        Assert.Contains(".NET", profile.Backend.Runtime, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Java", profile.Backend.Framework, StringComparison.OrdinalIgnoreCase);
        // Oracle: override com autoridade de requisito de projeto, com proveniência.
        Assert.Equal("Oracle", profile.Data.Database);
        var oracle = Assert.Single(
            profile.Overrides, over => over.Area == EffectiveProfileResolver.AreaDatabase);
        Assert.Equal(ProfileAuthority.ProjectRequirement, oracle.Authority);
        Assert.True(profile.Api.OpenApiRequired);
    }

    // ── PARTE F — os critérios de aceite da seção 32, sem depender do número ──────────────

    [Fact]
    public void OsCriteriosDeAceiteSaoExtraidosComIdsEstaveis()
    {
        var criteria = AcceptanceCriteriaExtractor.Extract(Levantamento.Value);

        // O documento real define 22 cláusulas de aceite ("For possível instalar…" até
        // "…estrutura configurável."). Zero perdidos.
        Assert.Equal(22, criteria.Count);
        Assert.Contains(criteria, c =>
            c.Text.Contains("conectar ao Oracle", StringComparison.Ordinal));
        Assert.Contains(criteria, c =>
            c.Text.Contains("erros por linha e coluna", StringComparison.Ordinal));
        Assert.Contains(criteria, c =>
            c.Text.Contains("novo indicador utilizando a estrutura configurável", StringComparison.Ordinal));
        Assert.All(criteria, c => Assert.Equal(KnowledgeBinding.Required, c.Binding));

        // Independência do número/título: renumerar e retitular preserva TODOS os ids.
        var mutated = Levantamento.Value.Replace(
            "32. CRITÉRIOS DE ACEITE", "9) Critérios de aceitação do sistema", StringComparison.Ordinal);
        var after = AcceptanceCriteriaExtractor.Extract(mutated);
        Assert.Equal(
            criteria.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal),
            after.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    // ── PARTE E — classificação de escopo ─────────────────────────────────────────────────

    [Fact]
    public void OEscopoSeparaMvpFinalFuturoEOpcional()
    {
        var sections = AttachmentSectionizer.Split(Levantamento.Value);

        // O documento é seccionável sem markdown: §27 (MVP) e §28 (final) endereçáveis.
        var mvp = AttachmentSectionizer.Find(sections, "27");
        var final = AttachmentSectionizer.Find(sections, "28");
        Assert.NotNull(mvp);
        Assert.Contains("MVP", mvp.Title, StringComparison.Ordinal);
        Assert.NotNull(final);
        Assert.Contains("ENTREGA FINAL", final.Title, StringComparison.Ordinal);

        // FUTURE: o que o documento declara como futuro NUNCA entra no MVP por default.
        foreach (var future in new[]
        {
            "Kubernetes como evolução da infraestrutura.",
            "Preparar a arquitetura para futura integração com Microsoft Entra ID.",
            "Preparar para integração futura com Microsoft Teams.",
        })
        {
            Assert.Equal(
                KnowledgeBinding.Future,
                SourceKnowledgeClassifier.Classify(future, "levantamento").Binding);
        }

        // OPTIONAL: "avaliar" é decisão aberta.
        Assert.Equal(
            KnowledgeBinding.Optional,
            SourceKnowledgeClassifier.Classify(
                "Avaliar a criação de um editor de dashboard baseado em grade, com movimentação e redimensionamento dos componentes.",
                "levantamento").Binding);

        // PROCESS INSTRUCTION: as sete fases do documento não governam o Playbook.
        Assert.Equal(
            KnowledgeBinding.NonAuthoritative,
            SourceKnowledgeClassifier.Classify(
                "Execute o desenvolvimento por fases. Ao final de cada fase aguarde a validação antes de mudanças estruturais críticas.",
                "levantamento").Binding);
    }

    /// <summary>
    /// Parte J — o próprio documento proíbe gráfico fixo por indicador: a frase vira requisito
    /// REQUIRED, e os dashboards HTML de referência são exemplos a representar pela
    /// configuração genérica, nunca componentes hardcoded.
    /// </summary>
    [Fact]
    public void AProibicaoDeGraficoFixoEUmRequisitoRequired()
    {
        var statement = SourceKnowledgeClassifier.Classify(
            "Não criar gráficos fixos diretamente no código para cada indicador. Desenvolver uma estrutura genérica e configurável.",
            "levantamento");

        Assert.Equal(KnowledgeBinding.Required, statement.Binding);
    }
}
