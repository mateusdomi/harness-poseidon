import { expect, test } from '@playwright/test';
import { activateProject, adoptProfile, createProject, navTo } from './journeys';

/**
 * Prisma Launch Gate — isolamento de conversa por projeto.
 *
 * O defeito real que motivou este teste: com o Prisma selecionado, o chat abria a conversa do
 * Poseidon. Uma mensagem (ou um anexo) entregue ao projeto errado é o pior tipo de vazamento —
 * silencioso e com autoridade. O invariante fixado aqui, pela UI: a conversa PERTENCE ao
 * projeto ativo; um projeto novo abre chat SEM o histórico alheio; voltar ao projeto original
 * restaura o histórico dele.
 */
test.describe('Chat — isolamento por projeto', () => {
  test('a conversa segue o projeto ativo e nunca atravessa projetos', async ({ page }) => {
    test.setTimeout(90_000);
    await adoptProfile(page);

    // O projeto default do ambiente tem histórico semeado — é a âncora do teste.
    await navTo(page, 'Chat');
    // O título da conversa vive no SELETOR de conversas do chat — é a identidade observável.
    const seededOption = page.locator('option', { hasText: 'Planejamento da sprint 12' });
    await expect(seededOption).toHaveCount(1);

    // Projeto NOVO ativo: o chat não pode carregar nem exibir a conversa do projeto anterior.
    await createProject(page, {
      name: 'Isolamento B',
      objective: 'Provar que a conversa de um projeto não vaza para outro.',
    });
    await activateProject(page, 'Isolamento B');
    await navTo(page, 'Chat');
    await expect(page.locator('option', { hasText: 'Planejamento da sprint 12' })).toHaveCount(0);

    // Conversas (a listagem) também segue o projeto ativo: nada do projeto default aparece.
    await navTo(page, 'Conversas');
    await expect(page.getByRole('heading', { name: 'Conversas' })).toBeVisible();
    await expect(page.getByText('Planejamento da sprint 12')).toHaveCount(0);

    // Voltar ao projeto original (o semeado 'Poseidon Frontend') restaura o histórico DELE.
    await activateProject(page, 'Poseidon Frontend');
    await navTo(page, 'Chat');
    await expect(page.locator('option', { hasText: 'Planejamento da sprint 12' })).toHaveCount(1);
  });
});
