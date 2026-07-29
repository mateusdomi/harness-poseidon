import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * F5/D10 — a Central de Entregas no modelo "rastreamento de encomenda".
 *
 * O que o dono precisa ver ao abrir a tela: quando começou, para quando ele
 * pediu, como está indo, e como levar o que já ficou pronto. O que ele NÃO
 * pode ver: previsão calculada ocupando o lugar do prazo que ninguém combinou.
 */
async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
  await page.goto('/delivery');
}

test.describe('F5 — Central de Entregas', () => {
  test('mostra início, prazo e o pacote de documentos de cada entrega', async ({ page }) => {
    await ensureProfile(page);

    await expect(page.getByText('Começou em').first()).toBeVisible();
    await expect(page.getByText('Prazo combinado').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Baixar documentos' }).first()).toBeVisible();
  });

  test('projeto sem prazo declarado diz que não há — não exibe previsão no lugar', async ({
    page,
  }) => {
    await ensureProfile(page);

    // A fixture tem uma entrega sem prazo: o texto honesto precisa aparecer.
    await expect(page.getByText('Sem prazo definido').first()).toBeVisible();
  });

  test('o relatório e a diária deixaram de morar aqui (migraram para o PO)', async ({ page }) => {
    await ensureProfile(page);

    await expect(page.getByRole('tab', { name: 'Relatórios' })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'Copiloto da daily' })).toHaveCount(0);
  });

  test('sem regressão de acessibilidade', async ({ page }) => {
    await ensureProfile(page);

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    const blocking = results.violations.filter(
      (violation) => violation.impact === 'critical' || violation.impact === 'serious',
    );
    expect(blocking, JSON.stringify(blocking.map((item) => item.id))).toEqual([]);
  });
});
