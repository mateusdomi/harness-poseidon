using Harness.Host.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A defesa contra prova de jornada evidentemente vazia.
///
/// A fronteira que estes testes marcam é tão importante quanto o que eles cobrem: isto NÃO é
/// análise semântica. Nenhuma asserção aqui pretende decidir se a jornada escrita corresponde ao
/// critério de aceite — quem sabe o que "emprestar" significa é o produto. O que se rejeita é a
/// suíte que passa sem olhar para o produto.
/// </summary>
public sealed class E2ESpecAnalysisTests
{
    [Fact]
    public void TesteTrivialmenteVerdadeiroNaoEJornada()
    {
        var verdict = E2ESpecAnalysis.Analyze(
        [
            ("e2e/ok.spec.ts",
             """
             import { test, expect } from '@playwright/test';
             test("ok", async () => { expect(true).toBe(true); });
             """),
        ]);

        Assert.False(verdict.Usable);
        Assert.Contains("Nenhuma navegação", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void JornadaQueNavegaEAfirmaAlgoSobreATelaEUsavel()
    {
        var verdict = E2ESpecAnalysis.Analyze(
        [
            ("e2e/emprestimo.spec.ts",
             """
             import { test, expect } from '@playwright/test';
             test("empresta um livro", async ({ page }) => {
               await page.goto('/emprestimos');
               await page.getByRole('button', { name: 'Novo' }).click();
               await expect(page.getByText('Empréstimo registrado')).toBeVisible();
             });
             """),
        ]);

        Assert.True(verdict.Usable, verdict.Reason);
        Assert.Equal(1, verdict.Tests);
        Assert.Equal(1, verdict.Navigations);
        Assert.True(verdict.Assertions >= 1);
    }

    [Fact]
    public void SuiteInteiramentePuladaNaoProvaNada()
    {
        var verdict = E2ESpecAnalysis.Analyze(
        [
            ("e2e/pulado.spec.ts",
             """
             import { test, expect } from '@playwright/test';
             test.skip("empresta", async ({ page }) => {
               await page.goto('/');
               await expect(page.getByText('x')).toBeVisible();
             });
             """),
        ]);

        Assert.False(verdict.Usable);
        Assert.Contains("pulados", verdict.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Jornada comentada não roda. Sem esta poda, um arquivo com um bloco comentado cheio de
    /// `page.goto` pareceria estatisticamente rico e passaria na inspeção estrutural.
    /// </summary>
    [Fact]
    public void NavegacaoComentadaNaoConta()
    {
        var verdict = E2ESpecAnalysis.Analyze(
        [
            ("e2e/comentado.spec.ts",
             """
             import { test, expect } from '@playwright/test';
             test("ok", async ({ page }) => {
               // await page.goto('/emprestimos');
               /* await page.goto('/devolucoes'); */
               expect(1).toBe(1);
             });
             """),
        ]);

        Assert.False(verdict.Usable);
        Assert.Contains("Nenhuma navegação", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void EntregaSemNenhumaEspecificacaoNaoEJornadaAusenteEBenigna()
    {
        var verdict = E2ESpecAnalysis.Analyze([]);

        Assert.False(verdict.Usable);
        Assert.Contains("não declara nenhuma especificação", verdict.Reason!, StringComparison.Ordinal);
    }
}
