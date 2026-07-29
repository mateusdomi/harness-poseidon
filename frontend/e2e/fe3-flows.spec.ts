import { expect, test, type Page } from '@playwright/test';

/**
 * Gate E2E da FE-3: fluxos contra o mock (VITE_API_MODE=mock), nos dois
 * viewports (mobile-360 e desktop-1440 — ver playwright.config.ts).
 *
 * Fluxo 1 — Rodar projeto: a tela lista os serviços detectados (fixture);
 * "Iniciar tudo" dispara os comandos start por serviço e os logs fluem via
 * `run.logAppended` (stream do projeto); "Parar tudo" encerra e os logs de
 * parada aparecem no painel.
 *
 * Fluxo 2 — PO Assistant: texto livre → painéis da análise (determinísticos
 * no mock) → "Criar demanda estruturada" cria a demanda (demand.created) e
 * o painel de sucesso exibe o título com link para o quadro.
 */

const SOLICITATION_TEXT =
  'Precisamos exportar o quadro em CSV. O time comercial vai usar no Excel.';
const DEMAND_TITLE = 'Precisamos exportar o quadro em CSV';

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

/**
 * Troca o modo de apresentação pelo menu do perfil (F4). Telas técnicas e
 * administrativas só existem no menu fora do modo Negócio.
 */
async function setPresentationMode(page: Page, label: RegExp) {
  const viewport = page.viewportSize();
  if (viewport && viewport.width < 1024) {
    await page.getByRole('button', { name: 'Mais' }).click();
  }
  await page
    .getByRole('button', { name: /perfil de/i })
    .first()
    .click();
  await page.getByRole('menuitemradio', { name: label }).first().click();
  await page.keyboard.press('Escape');
  if (viewport && viewport.width < 1024) {
    // O drawer tem dois "Fechar menu": o overlay e o X do cabeçalho.
    await page.getByRole('button', { name: 'Fechar menu' }).last().click();
  }
}

/** Onboarding pela UI: sem perfil na sessão, o guard redireciona. */
async function completeOnboarding(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
}

test.describe('Gate FE-3 — rodar projeto', () => {
  test('iniciar serviços mostra logs fluindo e parar encerra com logs de parada', async ({
    page,
  }) => {
    await completeOnboarding(page);
    await navTo(page, 'Executar Projeto');
    await expect(page.getByRole('heading', { name: 'Executar projeto' })).toBeVisible();

    // Serviços detectados da fixture do projeto ativo (Poseidon Frontend).
    await expect(page.getByText('Frontend Vite (dev)').first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('Backend API (.NET)').first()).toBeVisible();

    // Iniciar tudo: comandos start por serviço → run.logAppended no stream.
    await page.getByRole('button', { name: /^Iniciar (tudo|\d+ parado)/ }).click();
    const logs = page.getByRole('log', { name: 'Logs em tempo real' });
    await expect(logs.getByText(/Iniciando "Backend API \(\.NET\)"/)).toBeVisible({
      timeout: 10_000,
    });
    await expect(logs.getByText(/"Backend API \(\.NET\)" pronto — health check OK/)).toBeVisible({
      timeout: 10_000,
    });
    await expect(page.getByRole('button', { name: 'Iniciar tudo' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Parar tudo' })).toBeEnabled();

    // Parar tudo: logs de encerramento/parada aparecem no painel.
    await page.getByRole('button', { name: 'Parar tudo' }).click();
    await expect(logs.getByText(/"Frontend Vite \(dev\)" parado\./)).toBeVisible({
      timeout: 10_000,
    });
  });
});

test.describe('Gate FE-3 — PO Assistant', () => {
  test('analisar texto, ver painéis e criar demanda estruturada', async ({ page }) => {
    await completeOnboarding(page);
    // Assistente de PO só existe no modo Administrador (D7).
    await setPresentationMode(page, /Administrador/);
    await navTo(page, 'Assistente de PO');
    await expect(page.getByRole('heading', { name: 'Assistente de PO' })).toBeVisible();

    await page.getByLabel('Pedido em texto livre').fill(SOLICITATION_TEXT);
    await page.getByRole('button', { name: 'Analisar pedido' }).click();

    // Painéis de resultado com conteúdo determinístico do mock.
    await expect(
      page.getByRole('heading', { name: 'Requisitos extraídos' }),
    ).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('Qual é o prazo esperado para esta entrega?')).toBeVisible();
    await expect(page.getByText('Nenhuma contradição encontrada.')).toBeVisible();

    // Criar demanda estruturada → demanda criada no mock (demand.created).
    await page.getByRole('button', { name: 'Criar demanda estruturada' }).click();
    await expect(page.getByText(/Demanda estruturada criada/)).toBeVisible({ timeout: 10_000 });
    // A demanda aparece com o título derivado do primeiro requisito (o texto
    // também aparece no textarea e na lista de requisitos — por isso o escopo).
    await expect(page.getByText(DEMAND_TITLE).first()).toBeVisible();

    // Link para o quadro funciona.
    await page.getByRole('link', { name: 'Ver no quadro' }).click();
    await expect(page).toHaveURL(/\/board$/);
    await expect(page.getByRole('heading', { name: 'Quadro' })).toBeVisible({ timeout: 10_000 });
  });
});
