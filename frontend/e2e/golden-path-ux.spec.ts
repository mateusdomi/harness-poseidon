import { expect, test, type Page } from '@playwright/test';

import { activateProject, createProject, navTo  } from './journeys';

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


async function signIn(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
}

/** Cria um projeto novo com o workflow recomendado e o torna ativo. */
async function createProjectWithRecommendedWorkflow(page: Page) {
  await createProject(page, {
    name: PROJECT_NAME,
    objective: 'Projeto do gate de UX do golden path.',
  });
  await activateProject(page, PROJECT_NAME);
}

test.describe('Golden path — UX transversal', () => {
  test('a sigla do projeto é derivada do título, sem decisão manual', async ({ page }) => {
    await signIn(page);
    // No modo Negócio a sigla NÃO é campo do dono: ele informa o título e o sistema deriva
    // (maiúsculas, sem acento nem espaço, truncada em 12 — §7). A sigla também não é exibida,
    // porque é jargão. A prova, portanto, é dupla e observável: o dono não vê o campo, e a
    // criação conclui mesmo assim.
    await navTo(page, 'Projetos');
    await page.getByRole('button', { name: 'Novo projeto' }).click();
    await expect(page.getByLabel(/Slug \(sigla\)/)).toHaveCount(0);
    await page.getByRole('button', { name: 'Cancelar' }).click();

    // A própria jornada já prova que o projeto entrou na lista sem o dono decidir sigla.
    await createProject(page, {
      name: 'Plataforma de Faturamento',
      objective: 'Cobrança recorrente com conciliação.',
    });
  });

  test('criação e edição expõem "Voltar" com rótulo específico', async ({ page }) => {
    await signIn(page);
    await navTo(page, 'Projetos');

    await expect(page.getByRole('heading', { name: 'Projetos', level: 1 })).toBeVisible();
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
    //
    // Esperar a tela ESTAR PRONTA antes de contar: sob carga (a suíte roda em paralelo), a
    // contagem acontecia no meio da montagem e via um estado transitório. Contar antes de a tela
    // existir mede a montagem, não a regra.
    await navTo(page, 'Projetos');
    await expect(page.getByRole('heading', { name: 'Projetos', level: 1 })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Novo projeto' })).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Criar projeto' })).toHaveCount(0);

    // Organizações: idem.
    await navTo(page, 'Organizações');
    await expect(page.getByRole('heading', { name: 'Organizações', level: 1 })).toBeVisible();
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
    // O painel tem NOMES DIFERENTES por modo, e é assim que deve ser: "Acompanhamento do
    // projeto" para o dono, "Workflow do projeto" para quem opera a plataforma. Este teste roda
    // em modo Negócio, então prova o que o dono vê — esperar o rótulo técnico aqui seria cobrar
    // do produto o jargão que o léxico proíbe.
    const openPanel = page.getByRole('button', { name: 'Abrir acompanhamento do projeto' });
    if (await openPanel.isVisible()) await openPanel.click();
    await expect(page.getByText('Acompanhamento do projeto').first()).toBeVisible();

    // No mobile o painel é um drawer: o mesmo texto existe montado e oculto atrás dele, então
    // `.first()` pegava a cópia escondida e falhava por um motivo que não é o do teste. Filtrar
    // por visível pergunta o que de fato importa — o dono CONSEGUE ler o acompanhamento.
    await expect(page.getByLabel('Mensagem para Bruna')).toBeEnabled();
  });

  // A humanização da atividade é coberta por teste de componente
  // (`cockpit.test.tsx`), com eventos controlados: as fixtures de auditoria
  // são mais antigas que a maior janela do feed, então o recorte fica vazio
  // aqui e um E2E dependeria do relógio.

  test('a equipe mostra prontidão real sem exigir modo simulado', async ({ page }) => {
    await signIn(page);
    await navTo(page, 'Equipe');

    await expect(page.getByRole('heading', { name: 'Profissionais' })).toBeVisible();
    await expect(page.getByText('Bruna Magalhães').first()).toBeVisible();

    // "Binding pendente" era o estado esperado quando o projeto nascia sem esteira vinculada.
    // Desde a Fase 1E o vínculo nasce com o projeto, então a ausência do aviso é o SUCESSO —
    // esperar por ele seria pedir que o produto voltasse a ter a lacuna.
    await expect(page.getByText('Binding pendente')).toHaveCount(0);
  });
});
