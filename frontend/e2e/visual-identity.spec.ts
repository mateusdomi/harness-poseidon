import { expect, test } from '@playwright/test';

test('registra a identidade visual local nos temas escuro e claro', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  const productErrors: string[] = [];
  const missingAssets: string[] = [];
  const fontResponses: string[] = [];

  page.on('pageerror', (error) => productErrors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') productErrors.push(message.text());
  });
  page.on('response', (response) => {
    const url = response.url();
    if (response.status() === 404) missingAssets.push(url);
    if (/\.woff2?(?:$|\?)/.test(url) && response.ok()) fontResponses.push(url);
  });

  await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' });
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/cockpit$/);
  await expect(page.getByRole('heading', { name: 'Cockpit' })).toBeVisible();
  await expect(page.getByRole('status')).toHaveCount(0, { timeout: 15_000 });

  const heading = page.getByRole('heading', { name: 'Cockpit' });
  await expect
    .poll(() => heading.evaluate((element) => getComputedStyle(element).fontFamily))
    .toContain('Space Grotesk');
  await expect.poll(() => fontResponses.length).toBeGreaterThan(0);

  await page.screenshot({
    path: testInfo.outputPath('cockpit-dark.png'),
    fullPage: true,
    animations: 'disabled',
  });

  await page.getByRole('button', { name: 'Mudar para tema claro' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.screenshot({
    path: testInfo.outputPath('cockpit-light.png'),
    fullPage: true,
    animations: 'disabled',
  });

  expect(missingAssets).toEqual([]);
  expect(productErrors).toEqual([]);
});
