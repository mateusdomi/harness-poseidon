import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import path from 'node:path';

const ROUTES = [
  '/cockpit',
  '/organizations',
  '/projects',
  '/chat',
  '/conversations',
  '/board',
  '/workflows',
  '/documents',
  '/prototypes',
  '/approvals',
  '/orchestrator',
  '/agents',
  '/tools',
  '/run-project',
  '/providers',
  '/po-assistant',
  '/governance',
  '/licenses',
  '/notifications',
  '/settings',
] as const;

const THEME_STORAGE_KEY = 'poseidon-theme';
const PROFILE_NAME = 'Homologação Frontend';
const ORGANIZATION_NAME = 'Poseidon Homologação';
const PROJECT_NAME = 'Homologação backend real';
const WORKING_DIRECTORY = process.env.POSEIDON_WORKING_DIRECTORY ?? path.resolve(process.cwd(), '..');

interface RuntimeWatch {
  assertClean(context: string): void;
}

function watchRuntime(page: Page): RuntimeWatch {
  const issues: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') issues.push(`console: ${message.text()}`);
  });
  page.on('pageerror', (error) => issues.push(`pageerror: ${error.message}`));
  page.on('response', (response) => {
    const resourceType = response.request().resourceType();
    if (
      response.status() >= 400 &&
      ['font', 'image', 'script', 'stylesheet'].includes(resourceType)
    ) {
      issues.push(`${resourceType} ${response.status()}: ${response.url()}`);
    }
  });
  return {
    assertClean: (context) =>
      expect(issues, `${context}: erros não tratados no console ou assets quebrados`).toEqual([]),
  };
}

async function setTheme(page: Page, light: boolean) {
  await page.addInitScript(
    ({ key, preference }) => {
      window.localStorage.setItem(key, JSON.stringify({ state: { preference }, version: 0 }));
    },
    { key: THEME_STORAGE_KEY, preference: light ? 'light' : 'dark' },
  );
}

async function ensureSession(page: Page) {
  await page.goto('/onboarding');
  await expect(page.getByRole('main')).toBeVisible();

  const wizard = page.getByRole('form', { name: 'Configuração inicial' });
  const profilesResponse = await page.request.get('/api/v1/profiles?limit=100');
  expect(profilesResponse.ok()).toBe(true);
  const profiles = (await profilesResponse.json()) as { items: Array<{ displayName: string }> };
  if (profiles.items.length === 0) {
    await expect(wizard).toBeVisible();
    await page.getByLabel('Nome de exibição').fill(PROFILE_NAME);
    await page.getByLabel('E-mail (opcional)').fill('frontend.homologacao@poseidon.local');
    await page.getByRole('button', { name: 'Avançar' }).click();
    await page.getByRole('button', { name: 'Avançar' }).click();
    await page.getByLabel('Diretório de trabalho').fill('/tmp/poseidon-homologacao');
    await page.getByRole('button', { name: 'Avançar' }).click();
    await page.getByLabel('Entendo os riscos').check();
    await page.getByRole('button', { name: 'Concluir' }).click();
  } else {
    const profile = page.getByText(PROFILE_NAME, { exact: true });
    await expect(profile).toBeVisible();
    await profile.locator('xpath=ancestor::li').getByRole('button', { name: 'Entrar' }).click();
  }

  await expect(page).toHaveURL(/\/cockpit$/);
}

async function ensureWorkingDirectory(page: Page) {
  const list = await page.request.get('/api/v1/settings');
  expect(list.ok()).toBe(true);
  const body = (await list.json()) as { items: Array<{ id: string }> };
  expect(body.items.length).toBeGreaterThan(0);
  const update = await page.request.patch(`/api/v1/settings/${body.items[0].id}`, {
    data: { workingDirectory: WORKING_DIRECTORY },
  });
  expect(update.ok()).toBe(true);
}

async function ensureOrganization(page: Page) {
  await page.goto('/organizations');
  await expect(page.getByRole('heading', { name: 'Organizações' })).toBeVisible();
  if (await page.getByText(ORGANIZATION_NAME, { exact: true }).isVisible()) return;

  await page.getByRole('button', { name: 'Nova organização', exact: true }).click();
  await page.getByLabel('Nome').fill(ORGANIZATION_NAME);
  await page.getByLabel('Slug').fill('poseidon-homologacao');
  await page.getByRole('button', { name: 'Criar organização' }).click();
  await expect(page.getByText(ORGANIZATION_NAME, { exact: true })).toBeVisible();
}

async function ensureProject(page: Page) {
  await page.goto('/projects');
  await expect(page.getByRole('heading', { name: 'Projetos' })).toBeVisible();
  if (await page.getByRole('button', { name: PROJECT_NAME, exact: false }).isVisible()) return;

  await page.getByRole('button', { name: 'Novo projeto', exact: true }).click();
  await page.getByLabel('Título').fill(PROJECT_NAME);
  await page.getByLabel('Slug (sigla)').fill('HOMOLOG');
  await page.getByLabel('Descrição').fill('Validação técnica do frontend contra o Host real.');
  await page.getByRole('tab', { name: 'Pessoas' }).click();
  await page.getByLabel(new RegExp(PROFILE_NAME)).check();
  await page.getByRole('button', { name: 'Criar projeto' }).click();
  await expect(page.getByRole('button', { name: PROJECT_NAME, exact: false })).toBeVisible();
}

async function assertA11y(page: Page, context: string) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  const blocking = results.violations.filter(
    (violation) => violation.impact === 'critical' || violation.impact === 'serious',
  );
  expect(
    blocking,
    `${context}: ${blocking.map((violation) => `${violation.id}:${violation.nodes.length}`).join(', ')}`,
  ).toEqual([]);
}

async function captureEvidence(page: Page, testInfo: TestInfo, route: string) {
  if (!['/cockpit', '/chat', '/board', '/governance', '/settings'].includes(route)) return;
  const name = `${testInfo.project.name}-${route.slice(1)}.png`;
  const evidenceDirectory = process.env.POSEIDON_EVIDENCE_DIR;
  const screenshotPath = evidenceDirectory
    ? path.join(evidenceDirectory, name)
    : testInfo.outputPath(name);
  await page.screenshot({ path: screenshotPath, fullPage: true });
}

async function exerciseRealtimeAndAudit(page: Page, testInfo: TestInfo) {
  if (testInfo.project.name !== 'desktop-13-dark') return;

  await page.goto('/chat');
  const createdConversationResponse = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === '/api/v1/conversations',
  );
  await page.getByRole('button', { name: 'Nova conversa', exact: true }).click();
  const createdConversation = (await (await createdConversationResponse).json()) as { id: string };
  const conversation = page.locator('select#chat-conversation');
  await expect(conversation).toBeVisible();
  await expect(conversation).toHaveValue(createdConversation.id);
  const conversationId = createdConversation.id;
  expect(conversationId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);

  const message = `DEMANDA: Homologação realtime ${Date.now()} | Validar o frontend contra o Host real.`;
  const composer = page.getByRole('textbox', { name: 'Mensagem para o chefe' });
  await composer.fill(message);
  await expect(composer).toHaveValue(message);
  const send = page.getByRole('button', { name: 'Enviar mensagem' });
  await expect(send).toBeEnabled();
  await send.click();
  await expect(page.getByText(message, { exact: true })).toBeVisible();
  await expect(page.getByText(/Recebi sua mensagem\./)).toBeVisible({ timeout: 30_000 });

  const stream = `conversation:${conversationId}`;
  const snapshotResponse = await page.request.get(
    `/api/v1/event-streams/snapshot?stream=${encodeURIComponent(stream)}&afterSequence=0`,
  );
  expect(snapshotResponse.ok()).toBe(true);
  const snapshot = (await snapshotResponse.json()) as {
    sequence: number;
    delta: Array<{ sequence: number; type: string }>;
  };
  expect(snapshot.sequence).toBeGreaterThan(0);
  expect(snapshot.delta.map((event) => event.type)).toEqual(
    expect.arrayContaining([
      'message.appended',
      'chat.turnStarted',
      'chat.turnChunk',
      'chat.turnCompleted',
    ]),
  );
  expect(snapshot.delta.map((event) => event.sequence)).toEqual(
    [...snapshot.delta].map((event) => event.sequence).sort((a, b) => a - b),
  );

  await page.evaluate(() => {
    const root = document.documentElement;
    const recordInterruption = () => {
      if (/Reconectando ao tempo real|Sem conexão em tempo real/.test(document.body.innerText)) {
        root.dataset.realtimeInterruptionSeen = 'true';
      }
    };
    new MutationObserver(recordInterruption).observe(document.body, { childList: true, subtree: true });
  });
  const disconnect = await page.request.post('/__e2e__/disconnect-realtime');
  expect(disconnect.ok()).toBe(true);
  expect(((await disconnect.json()) as { disconnected: number }).disconnected).toBeGreaterThan(0);
  await expect
    .poll(() => page.evaluate(() => document.documentElement.dataset.realtimeInterruptionSeen))
    .toBe('true');
  await expect(page.getByText(/Reconectando ao tempo real|Sem conexão em tempo real/)).toHaveCount(0, {
    timeout: 30_000,
  });

  const postReconnectMessage = `Confirme a reconexão ${Date.now()}.`;
  await composer.fill(postReconnectMessage);
  await page.getByRole('button', { name: 'Enviar mensagem' }).click();
  await expect(page.getByText(postReconnectMessage, { exact: true })).toBeVisible();
  await expect(page.getByText(/O turno foi registrado de forma durável para: Confirme a reconexão/)).toBeVisible({
    timeout: 30_000,
  });

  await page.goto('/documents');
  await page.getByRole('button', { name: 'Enviar documento' }).click();
  const uploadDialog = page.getByRole('dialog', { name: 'Enviar documento externo' });
  const documentTitle = `Evidência de homologação ${Date.now()}`;
  await uploadDialog.getByLabel('Arquivo (.md, .txt)').setInputFiles({
    name: 'homologacao.md',
    mimeType: 'text/markdown',
    buffer: Buffer.from('# Evidência\n\nDocumento criado pelo gate contra o Host real.'),
  });
  await uploadDialog.getByLabel('Nome do documento').fill(documentTitle);
  await uploadDialog.getByRole('button', { name: 'Enviar', exact: true }).click();
  await expect(uploadDialog).toHaveCount(0, { timeout: 15_000 });
  const documentRow = page.getByRole('row', { name: new RegExp(documentTitle) });
  await expect(documentRow).toBeVisible();
  await documentRow.press('Enter');
  await expect(page).toHaveURL(/\/documents\?doc=/);
  await expect(page.getByRole('heading', { name: documentTitle })).toBeVisible();
  await page.getByRole('button', { name: 'Voltar ao catálogo' }).click();

  await page.goto('/providers');
  const syncProvider = page.getByRole('button', { name: 'Sincronizar' }).first();
  await expect(syncProvider).toBeVisible();
  await syncProvider.click();
  await expect(page.getByRole('status').filter({ hasText: /Catálogo sincronizado:/ })).toBeVisible();

  await page.goto('/run-project');
  await expect(page.getByRole('heading', { name: 'Serviços detectados' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Modo local' })).toBeVisible();
  await expect(page.getByText(WORKING_DIRECTORY, { exact: true })).toBeVisible();

  await page.goto('/governance');
  await expect(page.getByRole('heading', { name: 'Governança de agentes' })).toBeVisible();
  await expect(page.getByText(/Sinal técnico: (GO|NO-GO)/)).toBeVisible();
  await page.getByRole('tab', { name: 'Bundles e receipts' }).click();
  await expect(page.getByRole('button', { name: 'Ver bundle' }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Ver bundle' }).first().click();
  await expect(page.getByText('Context bundle reproduzível')).toBeVisible();
  await assertA11y(page, 'governança P1 — receipts');
  await page.getByRole('tab', { name: 'Documentos e saúde' }).click();
  await expect(page.getByText('Catálogo canônico não publicado')).toBeVisible();
  await assertA11y(page, 'governança P1 — documentos');
  await page.getByRole('tab', { name: 'Aprendizado P2' }).click();
  await expect(page.getByText('Contratos P2 ainda não publicados')).toBeVisible();
  await page.getByRole('tab', { name: 'Auditoria' }).click();
  const csvDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Exportar CSV' }).click();
  const download = await csvDownload;
  expect(download.suggestedFilename()).toBe('poseidon-auditoria.csv');
  await download.saveAs(testInfo.outputPath(download.suggestedFilename()));
}

test('Host real — onboarding, navegação, HTTP, SignalR, responsividade e a11y', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  const runtime = watchRuntime(page);
  await setTheme(page, testInfo.project.name.includes('light'));
  await ensureSession(page);
  await ensureWorkingDirectory(page);
  await ensureOrganization(page);
  await ensureProject(page);

  for (const route of ROUTES) {
    await page.goto(route);
    await expect(page.getByRole('main')).toBeVisible();
    await expect(page.getByText('Não foi possível carregar os dados. Tente novamente.')).toHaveCount(0);
    await page.waitForTimeout(400);
    await assertA11y(page, `${testInfo.project.name} ${route}`);
    await captureEvidence(page, testInfo, route);
  }

  await exerciseRealtimeAndAudit(page, testInfo);

  await page.goto('/cockpit');
  await expect(page.getByRole('main')).toBeVisible();
  const isMac = await page.evaluate(() => /mac|iphone|ipad|ipod/i.test(navigator.platform));
  await page.keyboard.press(isMac ? 'Meta+K' : 'Control+K');
  const palette = page.getByRole('combobox', { name: 'Busca global de telas' });
  await expect(palette).toBeFocused();
  await palette.fill('governança');
  await palette.press('Enter');
  await expect(page).toHaveURL(/\/governance$/);

  await page.keyboard.press('Tab');
  await expect
    .poll(() => page.evaluate(() => document.activeElement !== document.body))
    .toBe(true);

  runtime.assertClean(testInfo.project.name);
});
