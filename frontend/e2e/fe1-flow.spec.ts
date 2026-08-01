import { expect, test } from '@playwright/test';

import { createProject, navTo } from './journeys';

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


test.describe('Gate FE-1', () => {
  test('onboarding → criar projeto → chat → chefe planeja → cards se movem → aprovar gate', async ({
    page,
  }) => {
    // 1. Onboarding pela UI: sem perfil na sessão, o guard redireciona.
    await page.goto('/');
    await expect(page).toHaveURL(/\/onboarding$/);
    await page.getByRole('button', { name: /Mateus/ }).click();
    await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);

    // 2. Criar projeto.
    // A jornada do dono no modo Negócio: título e objetivo. Sigla, repositório, workflow e
    // equipe são derivados pelo sistema — ele é stakeholder, não operador.
    await createProject(page, {
      name: PROJECT_NAME,
      objective: 'Projeto criado pelo gate E2E da FE-1.',
    });

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
    // A prova útil não é uma frase fixa: é a Bruna demonstrando que LEU o contexto do projeto,
    // citando o objetivo que o dono declarou na criação. Casar texto literal fez este teste
    // envelhecer junto com uma redação que mudou.
    await expect(page.getByText(/gate E2E da FE-1/)).toBeVisible({ timeout: 10_000 });
    const brunaAvatar = page
      .getByRole('button', { name: 'Abrir perfil de Bruna Magalhães' })
      .last();
    await expect(brunaAvatar).toBeVisible();
    await expect(brunaAvatar.locator('img')).toHaveCSS('width', '80px');
    await expect(brunaAvatar.locator('img')).toHaveCSS('height', '80px');

    // 6. Quadro: o chefe criou demanda + tarefas (eventos no stream do projeto).
    await navTo(page, 'Quadro');
    const card = page.getByRole('button', { name: new RegExp(TASK_A) });
    await expect(card).toBeVisible({ timeout: 20_000 });

    // O card nasce no backlog e se move sozinho (task.stateChanged) até desenvolvimento.
    //
    // A coluna é uma `section` rotulada pelo próprio título, e o título muda por modo: "Em
    // andamento" para o dono, "Em desenvolvimento" para quem opera a plataforma. Este teste roda
    // em modo Negócio — pedir o rótulo técnico aqui seria cobrar o jargão que o léxico proíbe.
    const developmentColumn = page.getByRole('region', { name: /Em andamento/ });
    await expect(developmentColumn.getByRole('button', { name: new RegExp(TASK_A) })).toBeVisible({
      timeout: 20_000,
    });

    // 7. Aprovar o gate pelo detalhe da tarefa (drawer no desktop, página no mobile).
    await developmentColumn.getByRole('button', { name: new RegExp(TASK_A) }).click();
    await expect(page.getByText(APPROVAL_TITLE)).toBeVisible({ timeout: 20_000 });
    await page.getByRole('button', { name: 'Aprovar', exact: true }).click();
    await expect(page.getByText('Aprovada')).toBeVisible({ timeout: 10_000 });
  });
});
