import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

const PROJECT_NAME = 'Portal do Cliente F6';
const PROJECT_OBJECTIVE =
  'Permitir que clientes acompanhem pedidos e recebam atualizações claras.';
const PROJECT_DEADLINE = '2026-09-30';

async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
}

async function navTo(page: Page, name: string) {
  const viewport = page.viewportSize();
  if (viewport && viewport.width < 1024) {
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

test.describe('F6 — criação de projeto no modo Negócio', () => {
  test('quatro campos abrem um plano da Bruna citando objetivo e prazo', async ({ page }) => {
    test.setTimeout(60_000);
    await ensureProfile(page);
    await page.goto('/projects');
    await page.getByRole('button', { name: 'Novo projeto' }).click();

    const main = page.getByRole('main');
    await expect(main.getByRole('tablist')).toHaveCount(0);
    await expect(main.locator('input:visible, textarea:visible, select:visible')).toHaveCount(4);
    await expect(main.getByText('Repositório', { exact: true })).toHaveCount(0);
    await expect(main.getByText('Tecnologias', { exact: true })).toHaveCount(0);
    await expect(main.getByText('Pessoas', { exact: true })).toHaveCount(0);

    await page.getByLabel('Título').fill(PROJECT_NAME);
    await page.getByLabel('Objetivo e contexto').fill(PROJECT_OBJECTIVE);
    await page.getByLabel('Prazo desejado (opcional)').fill(PROJECT_DEADLINE);
    await page.getByLabel('Enviar arquivo de logo').setInputFiles({
      name: 'portal.png',
      mimeType: 'image/png',
      buffer: Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
        'base64',
      ),
    });

    const accessibility = await new AxeBuilder({ page })
      .include('main')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    expect(
      accessibility.violations.filter(
        (violation) => violation.impact === 'critical' || violation.impact === 'serious',
      ),
    ).toEqual([]);

    await page.getByRole('button', { name: 'Criar projeto' }).click();
    await expect(page.getByRole('heading', { name: 'Editar projeto' })).toBeVisible();

    await navTo(page, 'Dashboard');
    await page.getByLabel('Projeto ativo').selectOption({ label: PROJECT_NAME });
    await navTo(page, 'Chat');

    const composer = page.getByLabel('Mensagem para Bruna');
    await expect(composer).toBeEnabled();
    await composer.fill('Abra o primeiro plano deste projeto [plan]');
    await page.getByRole('button', { name: 'Enviar mensagem' }).click();

    const planOpening = page.getByText(new RegExp(PROJECT_OBJECTIVE));
    await expect(planOpening).toBeVisible({ timeout: 10_000 });
    await expect(planOpening).toContainText('30/09/2026');
  });
});
