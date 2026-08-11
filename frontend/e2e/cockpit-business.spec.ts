import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

async function ensureProfile(page: Page) {
  await page.goto('/');
  await page.waitForURL(/\/(?:onboarding|cockpit|chat(?:\/[^/]+)?)$/);
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await page.waitForURL(/\/(?:cockpit|chat(?:\/[^/]+)?)$/);
  }
  await page.goto('/cockpit');
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
}

test.describe('F3 — Dashboard de negócio', () => {
  test('prioriza equipe e mostra o lifecycle V3 sem fases legadas', async ({
    page,
  }) => {
    await ensureProfile(page);

    const teamBoard = page.getByRole('heading', { name: 'Quadro da Equipe' });
    const timeline = page.getByRole('heading', { name: 'Lifecycle V3' });
    await expect(teamBoard).toBeVisible();
    await expect(timeline).toBeVisible();

    const teamPosition = await teamBoard.boundingBox();
    const timelinePosition = await timeline.boundingBox();
    expect(teamPosition?.y ?? Number.POSITIVE_INFINITY).toBeLessThan(
      timelinePosition?.y ?? Number.NEGATIVE_INFINITY,
    );

    await expect(page.getByRole('progressbar', { name: 'Trabalho aceito' })).toHaveCount(1);
    await expect(page.getByRole('progressbar', { name: 'Executado' })).toHaveCount(0);
    await expect(page.getByRole('progressbar', { name: 'Validado' })).toHaveCount(0);
    await expect(page.getByRole('progressbar', { name: 'Aprovado' })).toHaveCount(0);

    const lifecycle = page.getByRole('list', { name: 'Lifecycle V3 do projeto' });
    await expect(lifecycle).toBeVisible();
    await expect(lifecycle.getByText('Entendimento')).toBeVisible();
    await expect(lifecycle.getByText('Desenvolvimento')).toBeVisible();
    await expect(lifecycle.getByText('Validação')).toBeVisible();
    await expect(lifecycle.getByText('Aceite Humano')).toBeVisible();
    await expect(page.getByRole('button', { name: /Ver detalhes da etapa/i })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Produtividade da equipe' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Capacidade da equipe' })).toBeVisible();
    // O cartão foi renomeado para o que o dono realmente vê: "Aprovações pendentes".
    await expect(page.getByRole('heading', { name: 'Aprovações pendentes' })).toBeVisible();

    const visibleDashboard = await page.getByRole('main').innerText();
    expect(visibleDashboard).not.toMatch(
      /\b(fleet|provedor|provider|modelo|model|token|tenant|slug|worktree|lease|fencing|heartbeat|gate|cota|deploy|staging|plugin)\b/iu,
    );

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
