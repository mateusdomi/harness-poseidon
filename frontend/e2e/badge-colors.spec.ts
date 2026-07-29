import { expect, test, type Page } from '@playwright/test';

const ROUTES = [
  '/cockpit',
  '/projects',
  '/delivery',
  '/chat',
  '/conversations',
  '/board',
  '/workflows',
  '/documents',
  '/prototypes',
  '/architecture',
  '/orchestrator',
  '/agents',
  '/tools',
  '/run-project',
  '/organizations',
  '/providers',
  '/po-assistant',
  '/governance',
  '/licenses',
  '/notifications',
  '/settings',
] as const;

const TRANSPARENT_BACKGROUNDS = new Set([
  'transparent',
  'rgba(0, 0, 0, 0)',
  'rgba(0,0,0,0)',
]);

async function ensureProfile(page: Page) {
  await page.goto('/');
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
  }
}

test('todas as tags do design system têm preenchimento nos dois temas', async ({ page }) => {
  test.setTimeout(120_000);
  await ensureProfile(page);

  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate((preference) => {
      window.localStorage.setItem(
        'poseidon-theme',
        JSON.stringify({ state: { preference }, version: 0 }),
      );
    }, theme);

    for (const route of ROUTES) {
      await page.goto(route);
      await expect(page.getByRole('main')).toBeVisible();
      await expect(page.getByRole('status')).toHaveCount(0, { timeout: 15_000 });

      const badges = page.locator('[data-slot="badge"], [data-slot="tag"]');
      for (let index = 0; index < await badges.count(); index += 1) {
        const badge = badges.nth(index);
        const background = await badge.evaluate(
          (element) => getComputedStyle(element).backgroundColor,
        );
        expect(
          TRANSPARENT_BACKGROUNDS.has(background),
          `${route} (${theme}) contém tag transparente: "${await badge.innerText()}"`,
        ).toBe(false);
      }
    }
  }
});
