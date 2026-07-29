import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

const TECHNICAL_TERMS =
  /\b(?:provider|provedor(?:es)?|modelos?|token|tenant|slug|heartbeat|lease|fencing|worktree|branch|gate|diagnóstico)\b/i;

async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
}

async function expectNoBlockingAxeViolations(page: Page, context: string) {
  const results = await new AxeBuilder({ page })
    .include('main')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  const blocking = results.violations.filter(
    (violation) => violation.impact === 'critical' || violation.impact === 'serious',
  );
  expect(blocking, `${context}: violações críticas/sérias`).toEqual([]);
}

async function expectMainFitsViewport(page: Page) {
  const dimensions = await page.getByRole('main').evaluate((main) => {
    return {
      clientWidth: main.clientWidth,
      scrollWidth: main.scrollWidth,
    };
  });
  expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.clientWidth + 1);
}

async function mainText(page: Page) {
  return (await page.getByRole('main').innerText()).replace(/\s+/g, ' ');
}

test.describe('F11 — telas administrativas por público', () => {
  test('modo Negócio mantém a jornada simples e sem vocabulário técnico', async ({ page }) => {
    await ensureProfile(page);

    await page.goto('/workflows');
    await expect(page.getByRole('heading', { name: 'Etapas do fluxo de trabalho' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Modelos de fluxo de trabalho' })).toHaveCount(
      0,
    );
    expect(await mainText(page)).not.toMatch(TECHNICAL_TERMS);
    await expectMainFitsViewport(page);
    await expectNoBlockingAxeViolations(page, 'fluxos no modo Negócio');

    await page.goto('/channels');
    await expect(
      page.getByRole('heading', { name: 'Conecte um canal em três passos' }),
    ).toBeVisible();
    await expect(page.getByText(/Abra o link do canal/)).toBeVisible();
    await expect(page.getByText(/Envie “oi”/)).toBeVisible();
    await expect(page.getByText(/Cole o número abaixo/)).toBeVisible();
    expect(await mainText(page)).not.toMatch(TECHNICAL_TERMS);
    await expectMainFitsViewport(page);
    await expectNoBlockingAxeViolations(page, 'canais no modo Negócio');

    await page.goto('/organizations');
    await expect(page.getByText('Organize a marca e os projetos de cada negócio.')).toBeVisible();
    const firstOrganization = page.getByRole('main').locator('ul button').first();
    await expect(firstOrganization).toBeVisible();
    await firstOrganization.click();
    await expect(page.getByRole('heading', { name: 'Marca' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Projetos associados' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Políticas' })).toHaveCount(0);
    expect(await mainText(page)).not.toMatch(TECHNICAL_TERMS);

    await page.goto('/licenses');
    await expect(page.getByText('Sua licença do Poseidon')).toBeVisible();
    await expect(page.getByText('Projetos ativos ao mesmo tempo')).toBeVisible();
    expect(await mainText(page)).not.toMatch(TECHNICAL_TERMS);
    await expectNoBlockingAxeViolations(page, 'licenças no modo Negócio');

    for (const route of ['/providers', '/tools']) {
      await page.goto(route);
      await expect(page.getByText('Área disponível no modo Técnico')).toBeVisible();
      expect(await mainText(page)).not.toMatch(TECHNICAL_TERMS);
    }

    await page.goto('/settings');
    await expect(page.getByText('Preferências')).toBeVisible();
    await expect(page.getByText('Backup e restauração')).toBeVisible();
    await expect(page.getByText('Diagnóstico')).toHaveCount(0);
    expect(await mainText(page)).not.toMatch(TECHNICAL_TERMS);
    await expectMainFitsViewport(page);
  });

  test('modo Técnico preserva administração e instruções operacionais', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/settings');
    await page.getByRole('combobox', { name: 'Modo de apresentação' }).selectOption('technical');
    await expect(page.getByRole('heading', { name: 'Diagnóstico' })).toBeVisible();

    await page.goto('/workflows');
    await expect(page.getByRole('heading', { name: 'Modelos de fluxo de trabalho' })).toBeVisible();
    await expectMainFitsViewport(page);

    await page.goto('/channels');
    await expect(page.getByText('Prefere fazer pela CLI/gateway?')).toBeVisible();
    await page.getByRole('button', { name: 'Vincular novo canal' }).click();
    await expect(page.getByLabel('Conversa unificada')).toBeVisible();

    await page.goto('/providers');
    await expect(page.getByRole('heading', { level: 2, name: 'OpenAI' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sincronizar' }).first()).toBeVisible();

    await page.goto('/tools');
    await expect(page.getByText('Como registrar um novo item')).toBeVisible();
    if ((page.viewportSize()?.width ?? 0) < 768) {
      await expect(page.getByRole('combobox', { name: 'Categorias do catálogo' })).toBeVisible();
    } else {
      await expect(page.getByRole('tablist', { name: 'Categorias do catálogo' })).toBeVisible();
    }
    await expectMainFitsViewport(page);
    await expectNoBlockingAxeViolations(page, 'ferramentas no modo Técnico');
  });
});
