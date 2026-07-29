import { expect, test, type Page } from '@playwright/test';

async function enterBusinessBoard(page: Page) {
  await page.goto('/onboarding');
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
  await page.goto('/board');
  await expect(page.getByRole('heading', { name: 'Quadro' })).toBeVisible();
  await expect(page.getByRole('region', { name: /Planejado/ })).toBeVisible();
}

async function expectNoPageOverflow(page: Page) {
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
      ),
    )
    .toBe(true);
}

test('Quadro mantém a projeção de negócio enxuta e responsiva', async ({
  page,
}, testInfo) => {
  test.setTimeout(60_000);
  const productErrors: string[] = [];
  page.on('pageerror', (error) => productErrors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') productErrors.push(message.text());
  });

  await enterBusinessBoard(page);

  await expect(page.getByRole('combobox', { name: 'Projeto', exact: true })).toHaveCount(0);
  await expect(page.getByRole('combobox', { name: 'Projeto ativo' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Coluna' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Fase' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Última atividade' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Arquivamento' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Buscar' })).toHaveCount(0);
  await expect(page.getByRole('combobox', { name: 'Responsável' })).toHaveCount(0);
  await expect(page.getByRole('combobox', { name: 'Tipo' })).toHaveCount(0);
  await expect(page.getByRole('combobox', { name: 'Prioridade' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Exportar CSV' })).toHaveCount(0);

  await expect(page.getByRole('region', { name: /Pronto para começar/ })).toBeVisible();
  await expect(page.getByRole('region', { name: /Em andamento/ })).toBeVisible();
  await expect(page.getByText('Tarefa de agente', { exact: true })).toHaveCount(0);
  await expect(page.locator('[data-slot="badge"]')).not.toHaveCount(0);
  await expectNoPageOverflow(page);

  await page.screenshot({
    path: testInfo.outputPath('board-business-dark.png'),
    fullPage: true,
    animations: 'disabled',
  });

  const approvalTask = page.getByRole('button', {
    name: /Abrir detalhes da tarefa Suíte E2E do fluxo de aprovação/,
  });
  await approvalTask.focus();
  await page.keyboard.press('Enter');

  const detail =
    (page.viewportSize()?.width ?? 0) >= 1024
      ? page.getByRole('dialog', { name: 'Detalhes da tarefa' })
      : page.getByRole('main');
  await expect(detail.getByText('Objetivo', { exact: true })).toBeVisible();
  await expect(detail.getByText('Decisões e aprovações')).toBeVisible();
  await expect(
    detail.getByText('Aprovar a publicação das verificações de ponta a ponta'),
  ).toBeVisible();
  await expect(detail.getByText('Entrega', { exact: true })).toBeVisible();
  await expect(detail.getByText('Aprovar publicação da suíte E2E')).toHaveCount(0);
  await expect(
    detail.getByText('Suíte completa rodando em CI; aprovar para marcar o gate.'),
  ).toHaveCount(0);
  await expect(detail.getByText('Instrução enviada ao agente')).toHaveCount(0);
  await expect(detail.getByText('Tentativas e evidências')).toHaveCount(0);
  await expect(detail.getByText('Tokens (entrada/saída)')).toHaveCount(0);
  await expectNoPageOverflow(page);

  await page.screenshot({
    path: testInfo.outputPath('board-task-business-dark.png'),
    fullPage: true,
    animations: 'disabled',
  });

  await page.evaluate(() => {
    window.localStorage.setItem(
      'poseidon-theme',
      JSON.stringify({ state: { preference: 'light' }, version: 0 }),
    );
  });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.getByRole('heading', { name: 'Suíte E2E do fluxo de aprovação' })).toBeVisible();
  await expectNoPageOverflow(page);
  await page.screenshot({
    path: testInfo.outputPath('board-task-business-light.png'),
    fullPage: true,
    animations: 'disabled',
  });

  expect(productErrors).toEqual([]);
});
