using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A regressão do <b>ADR-0005</b> do run real — a decisão que amputou o produto.
///
/// Em 2026-08-04T11:37, dentro do run de empréstimos, um ADR chamado "fronteira do piloto: entrega
/// backend-only" foi escrito, revisado por agente distinto e aceito. Duas horas depois a fase de
/// Desenvolvimento fechou sem interface, e a tela só nasceu porque uma pessoa reverteu a decisão à
/// mão. O ADR não era irracional: dizia que criar o diretório de interface sem stack decidida seria
/// arquitetura tomada sozinha. Estava certo sobre o processo e errado sobre o produto.
///
/// O que estes testes fixam: <b>arquitetura decide COMO o produto é construído, não SE ele existe.</b>
/// </summary>
public sealed class ScopeIntegrityGuardTests
{
    /// <summary>O perfil que o pedido original produz: produto Web, operado por pessoa.</summary>
    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs(
            "emprestimos",
            "Quero um sistema simples para controlar emprestimos de equipamentos. " +
            "Quero saber quem pegou cada equipamento, quando precisa devolver e quando esta atrasado.",
            []));

    /// <summary>O perfil que o ADR-0005 teria produzido: backend-only.</summary>
    private static ProjectEffectiveProfile BackendOnly() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("emprestimos", "Crie somente uma API de empréstimos.", []));

    [Fact]
    public void UmADRNaoPodeTirarAInterfaceDoProdutoSozinho()
    {
        var verdict = ScopeIntegrityGuard.Evaluate(
            Web(), BackendOnly(), ProfileAuthority.ApprovedDecision,
            "adr-0005-fronteira-do-piloto-entrega-backend-only");

        Assert.False(verdict.Allowed);
        Assert.Contains(verdict.Removals, item => item.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.Contains(verdict.Removals, item => item.Kind == ProductEvidenceKind.E2EJourneyPassed);
        Assert.Contains("BLOQUEADA", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("não decide SE ele existe", verdict.Reason, StringComparison.Ordinal);

        // O diagnóstico precisa apontar o caminho certo, não só barrar: dificuldade de construir
        // resolve-se decidindo o que falta, e foi essa a decisão que faltou naquele dia.
        Assert.Contains(
            "não cortar a parte que dá sentido ao produto", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Quem PEDIU o software pode reduzir o escopo — e isso aconteceu de verdade neste mesmo run,
    /// quando o usuário mandou reduzir a observabilidade ao mínimo. A trava não pode transformar o
    /// dono do produto em refém do próprio requisito inicial.
    /// </summary>
    [Theory]
    [InlineData(ProfileAuthority.ProjectRequirement)]
    [InlineData(ProfileAuthority.Regulatory)]
    public void QuemTemAutoridadeDeProdutoPodeReduzirOEscopoEARemocaoFicaRegistrada(
        ProfileAuthority autoridade)
    {
        var verdict = ScopeIntegrityGuard.Evaluate(
            Web(), BackendOnly(), autoridade, "decisao-do-usuario");

        Assert.True(verdict.Allowed);
        Assert.NotEmpty(verdict.Removals);
        Assert.Contains("tem autoridade para isso", verdict.Reason, StringComparison.Ordinal);

        // Permitir não é esquecer: o que saiu do produto fica escrito.
        Assert.Contains("interface", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProfileAuthority.Baseline)]
    [InlineData(ProfileAuthority.OrganizationConstraint)]
    [InlineData(ProfileAuthority.ApprovedDecision)]
    public void NenhumaAutoridadeAbaixoDeProdutoRemoveCapacidade(ProfileAuthority autoridade)
    {
        Assert.False(ScopeIntegrityGuard.MayRemoveCapability(autoridade));
    }

    /// <summary>
    /// A trava é ESTREITA de propósito. Trocar React por Angular, .NET por Java ou SQL Server por
    /// Oracle é exatamente o trabalho do arquiteto, e nada disso remove capacidade: o produto
    /// continua tendo tela, API e persistência. Uma trava que atrapalhasse isso seria abandonada na
    /// primeira semana.
    /// </summary>
    [Fact]
    public void TrocarAStackNaoERemoverCapacidadeEPassaSemCerimonia()
    {
        var web = Web();
        var comOutraStack = web with
        {
            Frontend = web.Frontend with { Framework = "Angular" },
            Data = web.Data with { Database = "Oracle" },
        };

        var verdict = ScopeIntegrityGuard.Evaluate(
            web, comOutraStack, ProfileAuthority.ApprovedDecision, "adr-troca-de-stack");

        Assert.True(verdict.Allowed);
        Assert.Empty(verdict.Removals);
        Assert.Contains("não remove nenhuma capacidade", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// ACRESCENTAR capacidade nunca é bloqueado: o produto passar a ter interface é o oposto do
    /// problema que esta trava existe para impedir.
    /// </summary>
    [Fact]
    public void AcrescentarCapacidadeNuncaEBloqueado()
    {
        var verdict = ScopeIntegrityGuard.Evaluate(
            BackendOnly(), Web(), ProfileAuthority.ApprovedDecision, "adr-0006-stack-da-interface");

        Assert.True(verdict.Allowed);
        Assert.Empty(verdict.Removals);
    }

    /// <summary>
    /// A análise é de COBERTURA, não de texto. O ADR-0005 real era bem escrito e convincente; ler a
    /// prosa para adivinhar intenção falharia justamente no caso em que ela convence.
    /// </summary>
    [Fact]
    public void AAnaliseOlhaACoberturaExigidaENaoAProsaDaDecisao()
    {
        var web = Web();
        var semPersistencia = web with { Data = web.Data with { Required = false } };

        var verdict = ScopeIntegrityGuard.Evaluate(
            web, semPersistencia, ProfileAuthority.ApprovedDecision, "adr-sem-banco");

        Assert.False(verdict.Allowed);
        Assert.Contains(
            verdict.Removals, item => item.Kind == ProductEvidenceKind.PersistenceVerified);
    }
}
