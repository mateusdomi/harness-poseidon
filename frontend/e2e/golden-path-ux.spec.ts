import { expect, test, type Page } from '@playwright/test';

/**
 * Gate de UX do golden path (mock determinístico, 2 viewports).
 *
 * Cobre as regras transversais que a homologação humana apontou:
 * CTA única por ação, navegação de volta, jornada guiada até o bloqueio
 * honesto do Chief e atividade humanizada.
 *
 * O estado "zero organização" NÃO é testável aqui — o mock nasce com
 * fixtures. Esse recorte vive em `package-clean.spec.ts`, que roda contra o
 * pacote self-contained com data dir vazio.
 */

const PROJECT_NAME = 'Projeto Golden Path E2E';

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

async function signIn(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
}

/** Cria um projeto novo com o workflow recomendado e o torna ativo. */
async function createProjectWithRecommendedWorkflow(page: Page) {
  await navTo(page, 'Projetos');
  await page.getByRole('button', { name: 'Novo projeto' }).click();
  await page.getByRole('tab', { name: 'Identidade' }).click();
  await page.getByLabel(/Título/).fill(PROJECT_NAME);
  await page.getByRole('tab', { name: 'Objetivo' }).click();
  await page.getByLabel(/Objetivo e contexto/).fill('Projeto do gate de UX do golden path.');
  await page.getByRole('tab', { name: 'Pessoas' }).click();
  await page.getByRole('checkbox', { name: /Mateus/ }).check();
  await page.getByRole('button', { name: 'Criar projeto' }).click();

  await navTo(page, 'Dashboard');
  await page.getByLabel('Projeto ativo').selectOption({ label: PROJECT_NAME });
}

test.describe('Golden path — UX transversal', () => {
  test('a sigla do projeto é derivada do título, sem decisão manual', async ({ page }) => {
    await signIn(page);
    await navTo(page, 'Projetos');
    await page.getByRole('button', { name: 'Novo projeto' }).click();
    await page.getByRole('tab', { name: 'Identidade' }).click();

    await page.getByLabel(/Título/).fill('Plataforma de Faturamento');
    // Derivada: maiúsculas, sem acento/espaço, truncada em 12 (§7).
    await expect(page.getByLabel(/Slug \(sigla\)/)).toHaveValue('PLATAFORMADE');
  });

  test('criação e edição expõem "Voltar" com rótulo específico', async ({ page }) => {
    await signIn(page);
    await navTo(page, 'Projetos');

    await page.getByRole('button', { name: 'Novo projeto' }).click();
    const back = page.getByRole('button', { name: 'Voltar para projetos' });
    await expect(back).toBeVisible();

    // Voltar não depende do menu lateral e devolve à listagem.
    await back.click();
    await expect(page.getByRole('button', { name: 'Novo projeto' })).toBeVisible();
  });

  test('nenhuma ação primária aparece duplicada na mesma tela', async ({ page }) => {
    await signIn(page);

    // Projetos: com itens, só o CTA do topo existe (o empty state some).
    await navTo(page, 'Projetos');
    await expect(page.getByRole('button', { name: 'Novo projeto' })).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Criar projeto' })).toHaveCount(0);

    // Organizações: idem.
    await navTo(page, 'Organizações');
    await expect(page.getByRole('button', { name: 'Nova organização' })).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Criar organização' })).toHaveCount(0);
  });

  test('plano e identificador interno não são escolhas do usuário no formulário', async ({
    page,
  }) => {
    await signIn(page);
    await navTo(page, 'Organizações');
    await page.getByRole('button', { name: 'Nova organização' }).click();

    // Sem seletor de plano (§8) e identificador interno fora do modo Negócio (§7).
    await expect(page.getByLabel('Plano')).toHaveCount(0);
    await expect(page.getByLabel('Identificador da URL')).toHaveCount(0);

    await page.getByLabel('Nome').fill('Organização Golden Path');
    await expect(page.getByLabel('Cor primária', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Opções avançadas' }).click();
    await expect(page.getByLabel('URL do logo')).toBeVisible();
    await expect(page.getByLabel('Identificador da URL')).toHaveCount(0);
  });

  test('projeto novo recebe workflow recomendado e o Chat expõe o ciclo sem bloqueio', async ({
    page,
  }) => {
    await signIn(page);
    await createProjectWithRecommendedWorkflow(page);

    // Chat: o vínculo recomendado nasce com o projeto e fica rastreável no painel.
    await navTo(page, 'Chat');
    await expect(page.getByText('Execução do chefe bloqueada')).toHaveCount(0);
    await expect(page.getByLabel('Mensagem para Bruna')).toBeEnabled();
    const openWorkflowPanel = page.getByRole('button', { name: 'Abrir painel do workflow' });
    if (await openWorkflowPanel.isVisible()) await openWorkflowPanel.click();
    await expect(page.getByText('Workflow do projeto')).toBeVisible();
    await expect(
      page.getByText(/Execução ainda não iniciada|Artefatos esperados/).first(),
    ).toBeVisible();
  });

  // A humanização da atividade é coberta por teste de componente
  // (`cockpit.test.tsx`), com eventos controlados: as fixtures de auditoria
  // são mais antigas que a maior janela do feed, então o recorte fica vazio
  // aqui e um E2E dependeria do relógio.

  test('o orquestrador mostra prontidão real e sinaliza modo simulado', async ({ page }) => {
    await signIn(page);
    await navTo(page, 'Orquestrador');

    // Estado de prontidão explícito + aviso honesto de dado simulado (§15).
    await expect(page.getByText('Modo simulado').first()).toBeVisible();
    await expect(page.getByText('Binding pendente').first()).toBeVisible();
  });
});
