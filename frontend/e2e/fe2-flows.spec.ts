import { expect, test, type Page } from '@playwright/test';

/**
 * Gate E2E da FE-2: fluxos contra o mock (VITE_API_MODE=mock), nos dois
 * viewports (mobile-360 e desktop-1440 — ver playwright.config.ts).
 *
 * Fluxo 1 — aprovação de documento: o detalhe da "Spec da API v1" abre com
 * a aprovação pendente vinculada (fixture). Aprovar move o documento para
 * `approved`; reprovar EXIGE observação (regra do contrato) e devolve o
 * documento para `inElaboration`. Cada teste roda com o mock recém-semeado
 * (contexto de browser novo), então os dois caminhos usam o mesmo documento.
 *
 * Fluxo 2 — passagem de bastão: o card do chefe mostra o modelo em uso
 * (GPT-4o, default da definição); o wizard troca para Claude Sonnet 4 e o
 * card reflete o novo modelo sem reload (invalidação das queries).
 */

const DOC_TITLE = 'Spec da API v1';
const REJECT_NOTE = 'Contratos sem exemplos de payload.';
const HANDOFF_REASON = 'Testar o Claude Sonnet 4 na orquestração do projeto.';

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

/** Onboarding pela UI: sem perfil na sessão, o guard redireciona. */
async function completeOnboarding(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/cockpit$/);
}

/** Abre o detalhe: card clicável no mobile (<md); linha da tabela no desktop. */
async function openDocument(page: Page, title: string) {
  const viewport = page.viewportSize();
  if (viewport && viewport.width < 768) {
    await page.getByRole('button', { name: new RegExp(title) }).click();
  } else {
    await page
      .getByRole('table', { name: 'Catálogo de documentos' })
      .getByRole('row', { name: new RegExp(title) })
      .click();
  }
  await expect(page.getByRole('heading', { name: title })).toBeVisible();
}

test.describe('Gate FE-2 — aprovação de documento', () => {
  test('aprovar documento com aprovação pendente move o estado para Aprovado', async ({
    page,
  }) => {
    await completeOnboarding(page);
    await navTo(page, 'Documentos');
    await openDocument(page, DOC_TITLE);

    // Aprovação pendente vinculada (fixture): aprovar é um clique.
    await page.getByRole('button', { name: 'Aprovar', exact: true }).click();

    // Documento awaitingApproval → approved; a aprovação mostra a decisão.
    await expect(page.getByText('Aprovado', { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('Aprovada', { exact: true })).toBeVisible();
  });

  test('reprovar exige observação e devolve o documento para elaboração', async ({ page }) => {
    await completeOnboarding(page);
    await navTo(page, 'Documentos');
    await openDocument(page, DOC_TITLE);

    await page.getByRole('button', { name: 'Reprovar' }).click();

    // Sem observação, a confirmação mostra erro e não resolve.
    await page.getByRole('button', { name: 'Confirmar reprovação' }).click();
    await expect(
      page.getByText('A observação é obrigatória para reprovar.'),
    ).toBeVisible();

    // Com observação, resolve: documento volta para elaboração.
    await page.getByLabel(/Observação/).fill(REJECT_NOTE);
    await page.getByRole('button', { name: 'Confirmar reprovação' }).click();
    await expect(page.getByText('Em elaboração', { exact: true })).toBeVisible({
      timeout: 10_000,
    });
    await expect(page.getByText('Reprovada', { exact: true })).toBeVisible();
    await expect(page.getByText(`Observação: ${REJECT_NOTE}`)).toBeVisible();
  });
});

test.describe('Gate FE-2 — passagem de bastão', () => {
  test('trocar o modelo do chefe pelo wizard e o card reflete sem reload', async ({ page }) => {
    await completeOnboarding(page);
    await navTo(page, 'Orquestrador');
    await expect(page).toHaveURL(/\/orchestrator$/);
    await expect(page.getByRole('heading', { name: 'Orquestrador' })).toBeVisible();

    // Card do chefe: modelo em uso é o default da definição (GPT-4o).
    await expect(page.getByText('GPT-4o', { exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole('button', { name: 'Passar bastão' }).click();
    const dialog = page.getByRole('dialog', { name: 'Passagem de bastão' });

    // Etapa 1: outro modelo + motivo (obrigatório).
    await dialog.getByLabel('Modelo da nova liderança').selectOption({ label: 'Claude Sonnet 4' });
    await dialog.getByRole('button', { name: 'Avançar' }).click();
    await expect(dialog.getByText('Informe o motivo da passagem de bastão.')).toBeVisible();
    await dialog.getByLabel(/Motivo/).fill(HANDOFF_REASON);
    await dialog.getByRole('button', { name: 'Avançar' }).click();

    // Etapa 2: resumo das escolhas → confirma.
    await expect(dialog.getByText('Claude Sonnet 4', { exact: true })).toBeVisible();
    await dialog.getByRole('button', { name: 'Confirmar passagem de bastão' }).click();

    // Sem reload: o card do chefe passa a exibir o novo modelo.
    await expect(dialog).toBeHidden();
    await expect(page.getByText('Claude Sonnet 4', { exact: true })).toBeVisible({
      timeout: 10_000,
    });
    await expect(page.getByText('GPT-4o', { exact: true })).toBeHidden();
  });
});
