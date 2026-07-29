import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

async function enterConversations(page: Page) {
  await page.goto('/onboarding');
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
  await page.goto('/conversations');
  await expect(page.getByRole('heading', { name: 'Conversas' })).toBeVisible();
}

test.describe('F7 — Conversas do projeto ativo', () => {
  test('mostra uma grade compacta, pesquisável e retoma a conversa', async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    await enterConversations(page);

    await expect(page.getByRole('combobox', { name: 'Projeto ativo' })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Projeto', exact: true })).toHaveCount(0);

    const grid = page.getByTestId('conversation-grid');
    const cards = grid.getByRole('listitem');
    await expect(cards).toHaveCount(2);
    expect(await cards.count()).toBeLessThanOrEqual(20);

    const viewportWidth = page.viewportSize()?.width ?? 0;
    const columnCount = await grid.evaluate(
      (element) => getComputedStyle(element).gridTemplateColumns.split(' ').filter(Boolean).length,
    );
    if (viewportWidth >= 1280) {
      expect(columnCount).toBe(4);
      const firstCard = await cards.first().boundingBox();
      expect(firstCard?.height ?? Number.POSITIVE_INFINITY).toBeLessThanOrEqual(190);
    } else {
      expect(columnCount).toBe(1);
    }

    const search = page.getByRole('searchbox', { name: 'Busca' });
    await search.fill('sprint 12');
    await expect(cards).toHaveCount(1);
    await expect(page.getByText('Planejamento da sprint 12')).toBeVisible();
    await search.clear();
    await expect(cards).toHaveCount(2);

    const visibleText = await page.getByRole('main').innerText();
    expect(visibleText).not.toMatch(
      /\b(agent task|tarefa de agente|provider|modelo|token|tenant|slug|worktree|lease|fencing|heartbeat|gate)\b/iu,
    );

    const accessibility = await new AxeBuilder({ page })
      .include('main')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    expect(
      accessibility.violations.filter(
        (violation) => violation.impact === 'critical' || violation.impact === 'serious',
      ),
    ).toEqual([]);

    await page.screenshot({
      path: testInfo.outputPath('conversations-business.png'),
      fullPage: true,
      animations: 'disabled',
    });

    await cards
      .filter({ hasText: 'Planejamento da sprint 12' })
      .getByRole('button', { name: 'Abrir' })
      .click();
    await expect(page).toHaveURL(/\/chat\/[^/]+$/);
    await expect(page.getByText('Planejamento da sprint 12')).toBeVisible();
  });
});
