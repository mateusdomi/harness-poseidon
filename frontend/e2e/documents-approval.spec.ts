import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * Documentos V3: Fontes, Registros do Projeto e Documentação de Entrega.
 *
 * Aprovação de fase é compatibilidade técnica legada. No caminho V3 de negócio,
 * a tela separa o que o usuário forneceu, o que o sistema registrou e a
 * documentação final de entrega.
 *
 * As asserções são feitas sobre o PAINEL, não sobre o controle: abaixo de `md` as
 * abas são um seletor e acima são um `tablist`, e o comportamento do dono é o
 * mesmo nos dois.
 */
async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
}

/** Troca de aba pelo controle que estiver visível na largura corrente. */
async function openTab(page: Page, name: RegExp, value: 'sources' | 'records' | 'delivery') {
  // O painel já renderizado garante que o controle de abas existe.
  await expect(page.getByRole('tabpanel')).toBeVisible();
  const tab = page.getByRole('tab', { name });
  if (await tab.isVisible()) {
    await tab.click();
    return;
  }
  // Seletor e tablist compartilham o mesmo rótulo: o papel desempata.
  await page.getByRole('combobox', { name: 'Seções de documentos' }).selectOption(value);
}

test.describe('Documentos V3', () => {
  test('a tela explica o que é, abre no catálogo e oferece o pacote', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents');

    await expect(page.getByText(/fontes do projeto/i)).toBeVisible();
    // Fontes é o padrão; registros e documentação de entrega são seções próprias.
    await expect(page.getByRole('tabpanel').first()).toBeVisible();
    await expect(
      page.getByRole('tab', { name: /Registros do Projeto/ }).or(
        page.getByRole('combobox', { name: 'Seções de documentos' }),
      ),
    ).toBeVisible();
    await expect(page.getByRole('button', { name: 'Baixar todos' })).toBeVisible();
  });

  test('o endereço antigo de Aprovações não expõe fila V1 no modo business', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/approvals');

    await page.waitForURL(/\/documents\?tab=approvals$/);
    await expect(page.getByRole('tabpanel')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Fila de aprovações' })).toHaveCount(0);
  });

  test('registros e documentação de entrega são alcançáveis sem workflow de aprovação', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents');

    await openTab(page, /Registros do Projeto/, 'records');
    await expect(page).toHaveURL(/tab=records/);

    await openTab(page, /Documentação de Entrega/, 'delivery');
    await expect(page).toHaveURL(/tab=delivery/);
    await expect(page.getByRole('list', { name: 'Fila de aprovações' })).toHaveCount(0);
  });

  test('modo Negócio não expõe vocabulário técnico na tela', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents');

    await expect(page.getByRole('tabpanel').first()).toBeVisible();
    const body = (await page.locator('body').textContent()) ?? '';
    expect(body).not.toMatch(
      /\b(waiver|worktree|lease|fencing|heartbeat|tenant|slug|endpoint|payload|deploy)\b/i,
    );
  });

  test('sem regressão de acessibilidade nas duas abas', async ({ page }) => {
    await ensureProfile(page);

    for (const address of ['/documents', '/documents?tab=records', '/documents?tab=delivery']) {
      await page.goto(address);
      await expect(page.getByRole('tabpanel')).toBeVisible();
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
