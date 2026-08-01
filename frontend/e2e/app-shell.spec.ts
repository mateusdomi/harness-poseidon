import { expect, test, type Page } from '@playwright/test';

import { navTo } from './journeys';


/**
 * Troca o modo de apresentação em Configurações (D7). O que o menu mostra é
 * decidido pelo modo — e o modo só oferece o que o perfil tem autorização.
 */
async function setPresentationMode(page: Page, label: string) {
  await page.goto('/settings');
  const select = page.locator('#settings-presentation');
  await expect(select).toBeEnabled();
  await select.selectOption({ label });
}

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

    // Rota 2: Agentes — tela técnica, então o modo precisa ser trocado antes.
    await setPresentationMode(page, 'Técnico');
    await navTo(page, 'Agentes');
    await expect(page).toHaveURL(/\/agents$/);
    await expect(page.getByRole('heading', { name: 'Agentes' })).toBeVisible();
  });

  test('o menu é o do modo: Negócio esconde o bastidor, Técnico e Administrador somam', async ({
    page,
  }) => {
    await signIn(page);

    // Padrão do cliente leigo: Chat na frente, nada de bastidor.
    let nav = await openPrimaryNav(page);
    await expect(nav.getByRole('link', { name: 'Chat', exact: true }).first()).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Agentes', exact: true })).toHaveCount(0);
    await expect(nav.getByRole('link', { name: 'Governança', exact: true })).toHaveCount(0);
    // Léxico do menu: "Orquestrador" virou "Equipe".
    await expect(nav.getByRole('link', { name: 'Equipe', exact: true }).first()).toBeVisible();

    await setPresentationMode(page, 'Técnico');
    nav = await openPrimaryNav(page);
    await expect(nav.getByRole('link', { name: 'Agentes', exact: true }).first()).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Governança', exact: true }).first()).toBeVisible();
    // Arquitetura só no Administrador.
    await expect(nav.getByRole('link', { name: 'Arquitetura', exact: true })).toHaveCount(0);

    await setPresentationMode(page, 'Administrador');
    nav = await openPrimaryNav(page);
    await expect(nav.getByRole('link', { name: 'Arquitetura', exact: true }).first()).toBeVisible();
    await expect(
      nav.getByRole('link', { name: 'Assistente de PO', exact: true }).first(),
    ).toBeVisible();
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
