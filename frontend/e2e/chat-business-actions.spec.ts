import { expect, test, type Page } from '@playwright/test';

async function enterBusinessChat(page: Page) {
  await page.goto('/onboarding');
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
  await page.goto('/chat');
  await expect(page.getByRole('heading', { name: 'Chat' })).toBeVisible();
  // O produto humanizou a projeção pública: "Equipe virtual" virou "Equipe de IA", e "Chief"
  // virou "Bruna Magalhães" (`public-leadership.ts`). O teste segue o produto.
  await expect(page.getByText('Equipe de IA').first()).toBeVisible();
}

test.describe('Chat — fluxos determinísticos da experiência de negócio', () => {
  test('resume o projeto com dados atuais sem vazar a operação interna', async ({ page }) => {
    await enterBusinessChat(page);

    await page.getByRole('button', { name: 'Como estamos?' }).click();
    await expect(page.getByText('Pode me contar como o projeto está avançando?')).toBeVisible();

    const answer = page
      .locator('article')
      .filter({ hasText: /O projeto .* está (na etapa|pausado|preparando)/ })
      .last();
    await expect(answer).toContainText(/A equipe já concluiu \d+ atividades?/);
    await expect(answer).toContainText(/decisão|Nenhuma decisão/);
    await expect(answer).not.toContainText(
      /\b(provider|provedor|model|modelo|quota|cota|token|executor|digest|UTC|backlog|ready|card|cartão|gate|SQL|logs?|projection|projeção|worktree|branch)\b/i,
    );
    await expect(answer).not.toContainText(/[0-9A-HJKMNP-TV-Z]{26}/);
  });

  test('acolhe uma nova entrega e começa pelo resultado desejado', async ({ page }) => {
    await enterBusinessChat(page);

    await page.getByRole('button', { name: 'Planejar uma entrega' }).click();
    await expect(page.getByText('Gostaria de planejar uma nova entrega.')).toBeVisible();

    const answer = page
      .locator('article')
      .filter({ hasText: /Me conte, com suas palavras, qual resultado/ })
      .last();
    await expect(answer).toContainText('Não precisa pensar nos detalhes técnicos');
    await expect(answer).not.toContainText(
      /não vou criar|cards?|risk tier|gate|módulo|repositório|stack/i,
    );
  });
});
