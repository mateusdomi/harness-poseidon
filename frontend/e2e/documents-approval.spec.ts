import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * F9/D9 — Documentos como TELA ÚNICA, com a aprovação dentro do fluxo.
 *
 * Eram quatro telas de documento e o dono não sabia qual era qual; pior, "aprovar"
 * morava fora de Documentos. Aqui: uma tela, duas abas, a decisão a um clique — e
 * o endereço antigo de Aprovações continua levando ao lugar certo.
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
async function openTab(page: Page, name: RegExp, value: 'catalog' | 'approvals') {
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

test.describe('F9 — Documentos e aprovação no fluxo', () => {
  test('a tela explica o que é, abre no catálogo e oferece o pacote', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents');

    await expect(
      page.getByText(/documentos que a equipe produziu para este projeto/),
    ).toBeVisible();
    // Catálogo é o padrão; a aba de aprovação existe e é alcançável.
    await expect(page.getByRole('tabpanel', { name: /Documentos do projeto/ })).toBeVisible();
    // O pacote de documentos aprovados sai daqui também (endpoint da F5).
    await expect(page.getByRole('button', { name: 'Baixar todos' })).toBeVisible();
  });

  test('o endereço antigo de Aprovações leva à aba de aprovação', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/approvals');

    await page.waitForURL(/\/documents\?tab=approvals$/);
    await expect(
      page.getByRole('tabpanel', { name: /Aguardando sua aprovação/ }),
    ).toBeVisible();
    await expect(page.getByRole('list', { name: 'Fila de aprovações' })).toBeVisible();
  });

  test('o dono aprova na própria aba e o item sai da fila', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents?tab=approvals');

    const queue = page.getByRole('list', { name: 'Fila de aprovações' });
    await expect(queue.getByRole('listitem')).toHaveCount(3);

    await queue.getByRole('listitem').first().getByRole('button', { name: 'Aprovar' }).click();
    await expect(queue.getByRole('listitem')).toHaveCount(2);
  });

  test('a aba é alcançável a partir do catálogo, sem sair da tela', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents');

    await openTab(page, /Aguardando sua aprovação/, 'approvals');

    await expect(page.getByRole('list', { name: 'Fila de aprovações' })).toBeVisible();
    // Uma tela, dois painéis: o catálogo sai de cena.
    await expect(page.getByRole('table', { name: 'Catálogo de documentos' })).toHaveCount(0);
    await expect(page).toHaveURL(/tab=approvals/);
  });

  test('modo Negócio não expõe vocabulário técnico na tela', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/documents');

    await expect(page.getByRole('tabpanel', { name: /Documentos do projeto/ })).toBeVisible();
    const body = (await page.locator('body').textContent()) ?? '';
    expect(body).not.toMatch(
      /\b(waiver|worktree|lease|fencing|heartbeat|tenant|slug|endpoint|payload|deploy)\b/i,
    );
  });

  test('sem regressão de acessibilidade nas duas abas', async ({ page }) => {
    await ensureProfile(page);

    for (const address of ['/documents', '/documents?tab=approvals']) {
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
