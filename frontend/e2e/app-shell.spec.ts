import { expect, test, type Page } from '@playwright/test';

/**
 * Smoke do AppShell: abre `/` (redireciona para /cockpit) e navega
 * para 2 rotas. Roda nos viewports 360px e 1440px (ver playwright.config.ts).
 */

async function navTo(page: Page, name: string) {
  const viewport = page.viewportSize();
  if (viewport && viewport.width < 1024) {
    // Mobile: itens fora da barra inferior ficam no drawer "Mais".
    const directLink = page.getByRole('link', { name, exact: true });
    if (!(await directLink.first().isVisible())) {
      await page.getByRole('button', { name: 'Mais' }).click();
    }
    await directLink.first().click();
    return;
  }
  await page
    .getByRole('navigation', { name: 'Navegação principal' })
    .getByRole('link', { name, exact: true })
    .click();
}

test.describe('AppShell smoke', () => {
  test('abre / e navega para 2 rotas', async ({ page }) => {
    await page.goto('/');
    // Sem perfil na sessão, o guard RequireProfile leva ao onboarding.
    await expect(page).toHaveURL(/\/onboarding$/);
    await page.getByRole('button', { name: /Mateus/ }).click();
    await expect(page).toHaveURL(/\/cockpit$/);
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();

    // Rota 1: Projetos
    await navTo(page, 'Projetos');
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { name: 'Projetos' })).toBeVisible();

    // Rota 2: Agentes
    await navTo(page, 'Agentes');
    await expect(page).toHaveURL(/\/agents$/);
    await expect(page.getByRole('heading', { name: 'Agentes' })).toBeVisible();
  });
});
