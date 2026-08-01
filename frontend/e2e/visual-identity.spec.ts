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
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);

  await page.goto('/chat');
  await expect(page.getByRole('heading', { name: 'Chat' })).toBeVisible();
  // O Chat deixou de expor "Perfil de trabalho": escolher perfil técnico é decisão de operador, e
  // o dono é stakeholder. O que sobrou no cabeçalho é a escolha de CONVERSA, que é dele. A
  // asserção acompanha o produto — e a linha seguinte continua garantindo que nenhum jargão de
  // modelo vazou para esta tela.
  // O produto humanizou a projeção pública: "Equipe virtual" virou "Equipe de IA", e "Chief"
  // virou "Bruna Magalhães" (`public-leadership.ts`). O teste segue o produto.
  await expect(page.getByText('Equipe de IA').first()).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Modelo' })).toHaveCount(0);
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth),
    )
    .toBe(true);

  if ((page.viewportSize()?.width ?? 0) >= 1024) {
    const projectPanel = page.getByRole('complementary', {
      name: 'Acompanhamento do projeto',
    });
    await expect(projectPanel).toBeVisible();
    await expect(
      projectPanel.getByRole('progressbar', { name: /Andamento de/ }),
    ).toBeVisible();
  } else {
    await page.getByRole('button', { name: 'Abrir acompanhamento do projeto' }).click();
    const projectDrawer = page.getByRole('dialog', {
      name: 'Acompanhamento do projeto',
    });
    await expect(projectDrawer).toBeVisible();
    await expect(
      projectDrawer.getByRole('progressbar', { name: /Andamento de/ }),
    ).toBeVisible();
    await page.keyboard.press('Escape');
  }

  await page.screenshot({
    path: testInfo.outputPath('chat-dark.png'),
    fullPage: true,
    animations: 'disabled',
  });

  if (testInfo.project.name === 'desktop-1440') {
    await page.setViewportSize({ width: 1280, height: 800 });
    await expect
      .poll(() =>
        page.evaluate(
          () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
        ),
      )
      .toBe(true);
    await page.screenshot({
      path: testInfo.outputPath('chat-macbook-13-dark.png'),
      fullPage: true,
      animations: 'disabled',
    });
    await page.setViewportSize({ width: 1440, height: 900 });
  }

  await page.goto('/cockpit');
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
  await expect(page.getByRole('status')).toHaveCount(0, { timeout: 15_000 });
  await expect(page.getByRole('combobox', { name: 'Projeto ativo' })).toBeVisible();
  // O cartão da equipe no Dashboard chama-se "Quadro da Equipe".
  await expect(page.getByRole('heading', { name: 'Quadro da Equipe' })).toBeVisible();
  // A trilha de etapas do projeto: o rótulo técnico ("timeline das nove fases") deu lugar ao
  // cartão "Etapas do projeto", que é como o dono a enxerga.
  await expect(page.getByRole('heading', { name: 'Etapas do projeto' })).toBeVisible();
  await expect(page.getByText('Fleet operacional')).toHaveCount(0);
  await expect(page.getByText('Cotas críticas')).toHaveCount(0);

  const heading = page.getByRole('heading', { name: 'Dashboard' });
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
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
  await expect(page.getByRole('status')).toHaveCount(0, { timeout: 15_000 });
  await page.screenshot({
    path: testInfo.outputPath('cockpit-light.png'),
    fullPage: true,
    animations: 'disabled',
  });

  await page.goto('/chat');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.getByRole('heading', { name: 'Chat' })).toBeVisible();
  await page.screenshot({
    path: testInfo.outputPath('chat-light.png'),
    fullPage: true,
    animations: 'disabled',
  });

  expect(missingAssets).toEqual([]);
  expect(productErrors).toEqual([]);
});
