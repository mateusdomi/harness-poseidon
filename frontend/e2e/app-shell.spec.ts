import { expect, test, type Page } from '@playwright/test';

import { navTo } from './journeys';

/** Menu principal visível: sidebar no desktop; drawer "Mais" no mobile. */
async function openPrimaryNav(page: Page) {
  const viewport = page.viewportSize();
  const navs = page.getByRole('navigation', { name: 'Navegação principal' });
  if (viewport && viewport.width < 1024) {
    const drawer = page.getByRole('dialog', { name: 'Navegação principal' });
    if (!(await drawer.isVisible())) {
      await page.getByRole('button', { name: 'Mais' }).click();
      await expect(drawer).toBeVisible();
    }
    return navs.last();
  }
  return navs.first();
}

async function signIn(page: Page) {
  await page.goto('/');
  // Sem perfil na sessão, o guard RequireProfile leva ao onboarding.
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
}

test.describe('AppShell smoke', () => {
  test('abre / e navega para 2 rotas', async ({ page }) => {
    await signIn(page);
    await expect(page.getByRole('heading', { name: 'Chat' })).toBeVisible();

    // Rota 1: Projetos
    await navTo(page, 'Projetos');
    await expect(page).toHaveURL(/\/projects$/);
    await expect(page.getByRole('heading', { name: 'Projetos' })).toBeVisible();

    // Rota 2: Profissionais — experiência V3 única, sem seletor global de modo.
    await navTo(page, 'Profissionais');
    await expect(page).toHaveURL(/\/agents$/);
    await expect(page.getByRole('heading', { name: 'Profissionais' })).toBeVisible();
  });

  test('o menu V3 usa experiência única e não expõe seletor global de modo', async ({
    page,
  }) => {
    await signIn(page);

    const nav = await openPrimaryNav(page);
    await expect(nav.getByRole('link', { name: 'Chat', exact: true }).first()).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Dashboard', exact: true }).first()).toBeVisible();
    await page.goto('/settings');
    await expect(page.locator('#settings-presentation')).toHaveCount(0);
    await expect(page.getByRole('combobox', { name: 'Modo de apresentação' })).toHaveCount(0);
  });

  test('o endereço antigo de Aprovações abre Documentos na aba de aprovações', async ({ page }) => {
    await signIn(page);
    await page.goto('/approvals');

    await expect(page).toHaveURL(/\/documents\?tab=approvals$/);
  });
});

test.describe('seleção global de projeto', () => {
  test('o seletor do cabeçalho some nas telas que comparam projetos', async ({ page }) => {
    await signIn(page);

    const selector = page.getByRole('banner').getByRole('combobox', { name: 'Projeto ativo' });
    await expect(selector).toBeVisible();
    await selector.selectOption({ label: 'API de Pagamentos' });
    const chosen = await selector.inputValue();

    // Telas multi-projeto ignoram o recorte: o seletor não aparece.
    await navTo(page, 'Projetos');
    await expect(
      page.getByRole('banner').getByRole('combobox', { name: 'Projeto ativo' }),
    ).toHaveCount(0);

    // A escolha continua valendo nas telas de projeto único, mesmo após recarregar.
    await page.goto('/chat');
    await expect(
      page.getByRole('banner').getByRole('combobox', { name: 'Projeto ativo' }),
    ).toHaveValue(chosen);
  });
});
