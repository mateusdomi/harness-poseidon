import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * F8/D1+D8 — a Equipe humanizada e "Abrir <projeto>".
 *
 * O dono é stakeholder, não operador: ele vê pessoas com nome, cargo e estado em
 * português, e abre o próprio produto com um clique. Saúde de processo, conta,
 * modelo, jornada, portas e a lista de serviços continuam existindo — no modo
 * Técnico, para quem opera.
 */
async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
}

/** Troca o modo de apresentação pela própria tela de Configurações. */
async function useMode(page: Page, label: 'Negócio' | 'Técnico') {
  await page.goto('/settings');
  const select = page.getByLabel('Modo de apresentação');
  await expect(select).toBeVisible();
  await select.selectOption({ label });
}

test.describe('F8 — Equipe e Executar Projeto', () => {
  test('modo Negócio: pessoa, estado em português e ações sem jargão', async ({ page }) => {
    await ensureProfile(page);
    await useMode(page, 'Negócio');
    await page.goto('/orchestrator');

    await expect(page.getByText('Bruna Magalhães').first()).toBeVisible();
    await expect(page.getByText('Como a Bruna se comunica')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Pausar trabalhos' })).toBeVisible();
    await expect(
      page.getByRole('button', { name: 'Concluir pendências e pausar' }),
    ).toBeVisible();
    await expect(page.getByRole('button', { name: 'Transferir liderança' })).toBeVisible();

    // Operação de máquina fica fora: nem saúde, nem conta, nem jornada, nem diagnóstico.
    await expect(page.getByText('Saúde', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Conta em uso')).toHaveCount(0);
    await expect(page.getByText('Jornada', { exact: true })).toHaveCount(0);
    const body = (await page.locator('body').textContent()) ?? '';
    expect(body).not.toMatch(/\b(lease|fencing|heartbeat|worktree|provider)\b/i);
  });

  test('modo Negócio: perfil da pessoa mostra competências e procedência', async ({ page }) => {
    await ensureProfile(page);
    await useMode(page, 'Negócio');
    await page.goto('/orchestrator');

    await page.getByRole('button', { name: 'Ver perfil' }).first().click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('Competências')).toBeVisible();
    await expect(dialog.getByText('O que está fazendo agora')).toBeVisible();
    await expect(dialog.getByText('De onde vem este perfil')).toBeVisible();
    // O perfil de pessoa não fala de assinatura nem de modelo.
    await expect(dialog.getByText('Conta em uso')).toHaveCount(0);
  });

  test('modo Negócio: Executar Projeto vira um botão e um endereço', async ({ page }) => {
    await ensureProfile(page);
    await useMode(page, 'Negócio');
    await page.goto('/run-project');

    await expect(page.getByRole('link', { name: /^Abrir / })).toBeVisible();
    await expect(page.getByText('No ar')).toBeVisible();
    // A lista de serviços internos e a limpeza de ambiente não existem aqui.
    await expect(page.getByText('Backend API (.NET)')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Modo local' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Limpar ambiente' })).toHaveCount(0);
  });

  test('modo Técnico recupera a lista completa de serviços e o diagnóstico', async ({ page }) => {
    await ensureProfile(page);
    await useMode(page, 'Técnico');

    await page.goto('/run-project');
    await expect(page.getByText('Backend API (.NET)').first()).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Modo local' })).toBeVisible();

    await page.goto('/orchestrator');
    await expect(page.getByText('Conta em uso')).toBeVisible();
    await expect(page.getByText('Diagnóstico avançado')).toBeVisible();
  });

  test('sem regressão de acessibilidade nas duas telas do modo Negócio', async ({ page }) => {
    await ensureProfile(page);
    await useMode(page, 'Negócio');

    for (const address of ['/orchestrator', '/run-project']) {
      await page.goto(address);
      await expect(page.getByRole('main')).toBeVisible();
      // Mesma disciplina da varredura de a11y da casa: esperar o conteúdo real
      // substituir o esqueleto de rota antes de medir.
      await page.waitForTimeout(500);
      const results = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
        .analyze();
      const blocking = results.violations.filter(
        (violation) => violation.impact === 'critical' || violation.impact === 'serious',
      );
      expect(blocking, `${address}: ${JSON.stringify(blocking.map((item) => item.id))}`).toEqual(
        [],
      );
    }
  });
});
