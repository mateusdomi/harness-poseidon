using Harness.Modules.Documents.Application;

namespace Harness.UnitTests.Documents;

/// <summary>
/// Fase 2A.1 — o contrato Fixed/Flexible dos templates do playbook.
///
/// FIXO: existência das seções obrigatórias e a ordem delas. FLEXÍVEL: prosa, vocabulário, tom e
/// extensão. Uma verificação que julgasse a prosa transformaria o template em camisa de força e
/// produziria documentos que passam no gate sem dizer nada.
/// </summary>
public sealed class DocumentTemplateComplianceTests
{
    private const string Gmud =
        """["janela","plano_execucao","plano_rollback_testado","criterios_saude","aprovador"]""";

    [Fact]
    public void ADocumentWithEveryRequiredSectionInOrderIsCompliant()
    {
        var body = """
            # GMUD — publicação da release 1.4

            ## Janela
            2026-08-05T02:00-03:00 a 2026-08-05T04:00-03:00.

            ## Plano de execução
            Subir a migração, depois o serviço, depois liberar o tráfego.

            ## Plano de rollback testado
            Ensaiado em 2026-08-01: restauração completa em 6 minutos.

            ## Critérios de saúde
            Erro 5xx abaixo de 0,1% por 30 minutos.

            ## Aprovador
            Mateus (dono), no gate de mudança.
            """;

        Assert.True(DocumentTemplateCompliance.Check(body, Gmud).IsCompliant);
    }

    [Fact]
    public void AGmudWithoutRollbackIsRefusedAndTheMissingSectionIsNamed()
    {
        // Este era o defeito concreto: o campo obrigatório existia no catálogo e ninguém o lia,
        // então uma GMUD sem plano de rollback entrava no acervo como se estivesse pronta.
        var body = """
            ## Janela
            Madrugada de terça.

            ## Plano de execução
            Subir tudo de uma vez.

            ## Critérios de saúde
            Ninguém reclamar.

            ## Aprovador
            Mateus.
            """;

        var result = DocumentTemplateCompliance.Check(body, Gmud);

        Assert.False(result.IsCompliant);
        Assert.Contains("plano_rollback_testado", result.MissingFields);
        Assert.Contains("plano_rollback_testado", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void SectionsOutOfTheTemplateOrderAreRefusedAndNamed()
    {
        var body = """
            ## Plano de execução
            Antes da janela, que o template manda vir primeiro.

            ## Janela
            2026-08-05T02:00-03:00 a 2026-08-05T04:00-03:00.

            ## Plano de rollback testado
            Ensaiado.

            ## Critérios de saúde
            Erro 5xx abaixo de 0,1%.

            ## Aprovador
            Mateus.
            """;

        var result = DocumentTemplateCompliance.Check(body, Gmud);

        Assert.False(result.IsCompliant);
        Assert.Empty(result.MissingFields);
        // Acusa a seção ADIANTADA, não a atrasada: "mova o plano de execução para depois da
        // janela" é acionável; "a janela está atrasada" faz o autor procurar o problema no lugar
        // onde ele não está.
        Assert.Contains("plano_execucao", result.OutOfOrderFields);
    }

    [Fact]
    public void AccentCaseAndUnderscoreDoNotChangeTheSection()
    {
        // O template declara `plano_rollback_testado`; a pessoa escreve "Plano de Rollback
        // Testado". Recusar por isso seria rigor onde não há risco nenhum.
        var body = """
            ## JANELA
            Madrugada.

            ## Plano de Execução
            Passo a passo.

            ## Plano de Rollback Testado
            Ensaiado.

            ## Critérios de Saúde
            Erro abaixo do limiar.

            ## Aprovador
            Mateus.
            """;

        Assert.True(DocumentTemplateCompliance.Check(body, Gmud).IsCompliant);
    }

    [Fact]
    public void ProseInsideASectionIsNeverJudged()
    {
        // O FLEXÍVEL do contrato: seção presente e na ordem basta. O conteúdo é do autor.
        var body = """
            ## Janela
            x

            ## Plano de execução
            x

            ## Plano de rollback testado
            x

            ## Critérios de saúde
            x

            ## Aprovador
            x
            """;

        Assert.True(DocumentTemplateCompliance.Check(body, Gmud).IsCompliant);
    }

    [Fact]
    public void NumberedHeadingsAndExplanatorySuffixesPreserveTheCanonicalField()
    {
        const string demand =
            """["valor_negocio","criticidade","decisao_bbir","justificativa","caminho_tecnico"]""";
        var body = """
            # Ficha de Demanda Qualificada

            ## 5. `valor_negocio`
            Controle dos empréstimos.

            ## 6. `criticidade`
            Média.

            ## 7. `decisao_bbir` (Build / Buy / Integrate / Reuse / Reject)
            Build.

            ## 8. `justificativa`
            Pedido novo e pequeno.

            ## 9. `caminho_tecnico` — em nível de capacidades
            Cadastro, empréstimo e devolução.
            """;

        Assert.True(DocumentTemplateCompliance.Check(body, demand).IsCompliant);
    }

    [Fact]
    public void ARepeatedMadrUsesStandaloneStrongLabelsAsStructuralFields()
    {
        const string adr =
            """["contexto","decisao","status","alternativas_consideradas","consequencias","consequencias_negativas"]""";
        var body = """
            # Registros de decisão

            ### ADR-001 — Persistência local

            **contexto**
            Uma pessoa opera o produto.

            **decisao**
            Usar persistência local na primeira versão.

            **status**
            Aceita.

            **alternativas_consideradas**
            - Servidor dedicado.

            **consequencias**
            Menor custo operacional.

            **consequencias_negativas**
            Exige migração para uso concorrente.

            ### ADR-002 — Aplicação única

            **contexto:**
            Uma jornada pequena.

            **decisao**:
            Um único processo implantável.

            **status**
            Aceita.

            **alternativas_consideradas**
            - Serviços separados.

            **consequencias**
            Operação simples.

            **consequencias_negativas**
            Crescimento futuro exige revisão.
            """;

        Assert.True(DocumentTemplateCompliance.Check(body, adr).IsCompliant);
    }

    [Fact]
    public void StrongTextInProseOrCodeDoesNotSatisfyARequiredSection()
    {
        const string required = """["contexto","decisao"]""";
        var body = """
            **contexto** aparece nesta frase, mas não delimita uma seção.

            ```markdown
            **decisao**
            ```
            """;

        var result = DocumentTemplateCompliance.Check(body, required);

        Assert.Contains("contexto", result.MissingFields);
        Assert.Contains("decisao", result.MissingFields);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void AMalformedOrEmptyCatalogEntryNeverBlocksTheAuthor(string? fields)
    {
        // Catálogo malformado é problema do catálogo. Bloquear quem está escrevendo um documento
        // legítimo por causa disso transferiria o defeito para a pessoa errada.
        Assert.True(DocumentTemplateCompliance.Check("# Qualquer coisa", fields).IsCompliant);
    }

    [Fact]
    public void TheDeclaredFieldOrderIsPreservedWhenParsed()
    {
        Assert.Equal(
            ["janela", "plano_execucao", "plano_rollback_testado", "criterios_saude", "aprovador"],
            DocumentTemplateCompliance.ParseFields(Gmud));
    }
}
