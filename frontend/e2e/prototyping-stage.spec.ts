import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
}

test.describe('F10 — etapa de Prototipação e pacote de telas', () => {
  test('mostra o portão e promove o ZIP React a design system', async ({ page }) => {
    await ensureProfile(page);
    await page.goto('/prototypes');

    await expect(page.getByRole('heading', { name: 'Etapa de Prototipação' })).toBeVisible();
    await expect(page.getByText('Aguardando informações')).toBeVisible();

    await page.getByRole('button', { name: 'Enviar referência' }).click();
    const dialog = page.getByRole('dialog', { name: 'Enviar referência visual' });
    await dialog.getByLabel('Arquivo (imagem ou ZIP)').setInputFiles({
      name: 'telas-poseidon.zip',
      mimeType: 'application/zip',
      buffer: Buffer.from('pacote de teste'),
    });
    await dialog.getByLabel('Título').fill('Telas Poseidon');
    await dialog.getByRole('button', { name: 'Enviar referência' }).click();

    await expect(page.getByText('Telas Poseidon')).toBeVisible();
    await expect(page.getByText('Pronta com itens já cadastrados')).toBeVisible();
    await expect(page.getByText(/pacote React/)).toBeVisible();

    const dimensions = await page.getByRole('main').evaluate((main) => ({
      clientWidth: main.clientWidth,
      scrollWidth: main.scrollWidth,
    }));
    expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.clientWidth + 1);

    const results = await new AxeBuilder({ page })
      .include('main')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    expect(
      results.violations.filter(
        (violation) => violation.impact === 'critical' || violation.impact === 'serious',
      ),
    ).toEqual([]);
  });
});
