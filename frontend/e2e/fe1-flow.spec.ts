import { expect, test, type Page } from '@playwright/test';

/**
 * Gate E2E da FE-1: fluxo completo contra o mock (VITE_API_MODE=mock),
 * nos dois viewports (mobile-360 e desktop-1440 — ver playwright.config.ts).
 *
 * Fluxo: onboarding pela UI → criar projeto → conversar no chat →
 * chefe cria demanda/tarefas (gatilho determinístico `[plan]` do
 * MockApiClient) → cards se movem por eventos `task.stateChanged` →
 * aprovar gate pelo detalhe da tarefa no quadro.
 *
 * O mock cria a tarefa SEMPRE no backlog (`create('tasks')`) e a move
 * backlog → ready → development via eventos; o expect final na coluna
 * "Em desenvolvimento" prova que a movimentação por eventos aconteceu.
 */

const PROJECT_NAME = 'Projeto E2E FE-1';
const PLAN_MESSAGE = 'Preciso de um plano de entrega do MVP [plan]';
const TASK_A = 'Decompor escopo do plano';
const APPROVAL_TITLE = 'Aprovar gate do plano simulado';

/** Navega pelo shell: barra lateral no desktop; barra inferior + drawer "Mais" no mobile. */
async function navTo(page: Page, name: string) {
  const viewport = page.viewportSize();
  if (viewport && viewport.width < 1024) {
    const directLink = page.getByRole('link', { name, exact: true });
    if (!(await directLink.first().isVisible())) {
      await page.getByRole('button', { name: 'Mais' }).click();
    }
    await directLink.first().click();
    return;
  }
  await page
    .getByRole('navigation', { name: 'Navegação principal' })
    .getByRole('link', { name, exact: true })
    .click();
}

test.describe('Gate FE-1', () => {
  test('onboarding → criar projeto → chat → chefe planeja → cards se movem → aprovar gate', async ({
    page,
  }) => {
    // 1. Onboarding pela UI: sem perfil na sessão, o guard redireciona.
    await page.goto('/');
    await expect(page).toHaveURL(/\/onboarding$/);
    await page.getByRole('button', { name: /Mateus/ }).click();
    await expect(page).toHaveURL(/\/cockpit$/);

    // 2. Criar projeto (abas: identificação + pessoas; demais com defaults válidos).
    await navTo(page, 'Projetos');
    await page.getByRole('button', { name: 'Novo projeto' }).click();
    await page.getByRole('tab', { name: 'Identidade' }).click();
    await page.getByLabel(/Título/).fill(PROJECT_NAME);
    // A sigla é derivada do nome; sobrescrevemos com um valor determinístico.
    await page.getByLabel(/Slug \(sigla\)/).fill('E2EFE1');
    await page.getByRole('tab', { name: 'Objetivo' }).click();
    await page.getByLabel(/Objetivo e contexto/).fill('Projeto criado pelo gate E2E da FE-1.');
    await page.getByRole('tab', { name: 'Pessoas' }).click();
    await page.getByRole('checkbox', { name: /Mateus/ }).check();
    await page.getByRole('button', { name: 'Criar projeto' }).click();

    // 3. Tornar o projeto recém-criado o projeto ativo.
    await navTo(page, 'Dashboard');
    await page.getByLabel('Projeto ativo').selectOption({ label: PROJECT_NAME });

    // 4. O workflow recomendado já vem vinculado pelo formulário de criação.
    await navTo(page, 'Chat');
    await expect(page.getByLabel('Mensagem para Bruna')).toBeEnabled();

    // 5. Chat: com as dependências prontas, o composer libera. A conversa é
    // criada automaticamente ao enviar — sem precisar de "Nova conversa".
    const composer = page.getByLabel('Mensagem para Bruna');
    await expect(composer).toBeEnabled();
    await composer.fill(PLAN_MESSAGE);
    await page.getByRole('button', { name: 'Enviar mensagem' }).click();
    await expect(page.getByText(/Entendi o contexto/)).toBeVisible({ timeout: 10_000 });

    // 6. Quadro: o chefe criou demanda + tarefas (eventos no stream do projeto).
    await navTo(page, 'Quadro');
    const card = page.getByRole('button', { name: new RegExp(TASK_A) });
    await expect(card).toBeVisible({ timeout: 20_000 });

    // O card nasce no backlog e se move sozinho (task.stateChanged) até desenvolvimento.
    const developmentColumn = page.getByRole('region', { name: /Em desenvolvimento/ });
    await expect(
      developmentColumn.getByRole('button', { name: new RegExp(TASK_A) }),
    ).toBeVisible({ timeout: 20_000 });

    // 7. Aprovar o gate pelo detalhe da tarefa (drawer no desktop, página no mobile).
    await developmentColumn.getByRole('button', { name: new RegExp(TASK_A) }).click();
    await expect(page.getByText(APPROVAL_TITLE)).toBeVisible({ timeout: 20_000 });
    await page.getByRole('button', { name: 'Aprovar', exact: true }).click();
    await expect(page.getByText('Aprovada')).toBeVisible({ timeout: 10_000 });
  });
});
