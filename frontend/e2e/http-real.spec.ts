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
  '/governance-docs',
  '/prototypes',
  '/architecture',
  '/approvals',
  '/orchestrator',
  '/agents',
  '/tools',
  '/run-project',
  '/providers',
  '/channels',
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
  let selectedName = PROFILE_NAME;
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
    selectedName = profiles.items.find((item) => item.displayName === PROFILE_NAME)?.displayName
      ?? profiles.items[0].displayName;
    const profile = page.getByText(selectedName, { exact: true });
    await expect(profile).toBeVisible();
    await profile.locator('xpath=ancestor::li').getByRole('button', { name: 'Entrar' }).click();
  }

  await expect(page).toHaveURL(/\/cockpit$/);
  return selectedName;
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

  // Com coleção vazia a CTA única vem do empty state; com itens, do topo (§4).
  const newOrgCta = page.getByRole('button', { name: 'Nova organização', exact: true });
  await ((await newOrgCta.isVisible())
    ? newOrgCta
    : page.getByRole('button', { name: 'Criar organização' })
  ).click();
  await page.getByLabel('Nome').fill(ORGANIZATION_NAME);
  // Slug gerado do nome automaticamente (§7).
  await page.getByRole('button', { name: 'Criar organização' }).click();
  await expect(page.getByText(ORGANIZATION_NAME, { exact: true })).toBeVisible();
}

async function ensureProject(page: Page, profileName: string) {
  await page.goto('/projects');
  await expect(page.getByRole('heading', { name: 'Projetos' })).toBeVisible();
  if (await page.getByRole('button', { name: PROJECT_NAME, exact: false }).isVisible()) return;

  const newProjectCta = page.getByRole('button', { name: 'Novo projeto', exact: true });
  await ((await newProjectCta.isVisible())
    ? newProjectCta
    : page.getByRole('button', { name: 'Criar projeto' })
  ).click();
  await page.getByLabel('Título').fill(PROJECT_NAME);
  await page.getByLabel('Slug (sigla)').fill('HOMOLOG');
  await page.getByLabel('Descrição').fill('Validação técnica do frontend contra o Host real.');
  await page.getByRole('tab', { name: 'Pessoas' }).click();
  await page.getByRole('checkbox', { name: profileName, exact: true }).check();
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

async function exerciseMermaidDocument(page: Page, testInfo: TestInfo) {
  if (testInfo.project.name !== 'desktop-13-dark') return;

  await page.goto('/governance-docs');
  await expect(page.getByRole('heading', { name: 'Documentos de Governança' })).toBeVisible();
  await page.getByRole('button', { name: 'MERMAID-RENDERING-EVIDENCE.md' }).click();
  const diagram = page.getByRole('img', { name: 'Visualização do diagrama Mermaid' });
  await expect(diagram).toBeVisible({ timeout: 20_000 });
  await expect(diagram.locator('svg')).toBeVisible();
  await assertA11y(page, 'documento Mermaid renderizado localmente');
  await page.screenshot({
    path: testInfo.outputPath('desktop-13-dark-governance-doc-mermaid.png'),
    fullPage: true,
  });
}

async function exerciseLearningP2(page: Page, testInfo: TestInfo) {
  if (testInfo.project.name !== 'desktop-13-dark') return;
  const projectsResponse = await page.request.get('/api/v1/projects?limit=100');
  expect(projectsResponse.ok()).toBe(true);
  const projects = (await projectsResponse.json()) as { items: Array<{ id: string; name: string }> };
  const project = projects.items.find((item) => item.name === PROJECT_NAME);
  expect(project).toBeDefined();
  const agentsResponse = await page.request.get(`/api/v1/agents?projectId=${project!.id}&limit=100`);
  expect(agentsResponse.ok()).toBe(true);
  const agents = (await agentsResponse.json()) as { items: Array<{ id: string }> };
  expect(agents.items.length).toBeGreaterThan(0);
  const actorId = agents.items[0].id;
  const marker = Date.now();
  const title = `Candidate P2 E2E ${marker}`;
  const create = await page.request.post('/api/v1/governance-runtime/learning-candidates', {
    headers: { 'Idempotency-Key': `e2e-create-${marker}` },
    data: {
      projectId: project!.id,
      type: 'rule',
      observation: `Falha transitória observada ${marker}.`,
      evidence: [{ kind: 'test', reference: `evidence://e2e/${marker}`, checksum: `sha256:${String(marker).padEnd(64, '0').slice(0, 64)}`, summary: 'Evidência descartável do E2E real.' }],
      payload: { title, statement: 'Retry somente falhas transitórias com limite.' },
      actorAgentId: actorId,
      actorProvider: 'e2e-actor',
      actorModel: 'actor-model',
      baselineVersion: 'rule/1',
      proposedVersion: 'rule/2',
    },
  });
  expect(create.ok()).toBe(true);
  const created = (await create.json()) as { candidate: { candidateId: string } };

  await page.goto('/governance');
  await page.getByRole('tab', { name: 'Aprendizado P2' }).click();
  await page.getByLabel('Projeto', { exact: true }).selectOption(project!.id);
  await expect(page.getByRole('button', { name: new RegExp(title) })).toBeVisible();
  await page.getByRole('button', { name: new RegExp(title) }).click();
  await expect(page.getByText('Comparação de versões')).toBeVisible();
  await expect(page.getByText('Evidência descartável do E2E real.')).toBeVisible();
  const expectState = async (state: string) => {
    const header = page.getByRole('heading', { name: title, exact: true }).locator('..');
    await expect(header.getByText(state, { exact: true })).toBeVisible();
  };

  const transition = async (button: string, expectedState: string, note?: string) => {
    await page.getByRole('button', { name: button, exact: true }).click();
    if (note !== undefined) await page.getByLabel('Justificativa').fill(note);
    await page.getByRole('button', { name: 'Confirmar transição' }).click();
    await expectState(expectedState);
  };

  await transition('Solicitar revisão', 'in_review', 'Revisão humana solicitada.');
  await transition('Solicitar avaliação', 'awaiting_evaluation', 'Avaliação independente solicitada.');

  const handoff = await page.request.post(`/api/v1/projects/${project!.id}/chief/handoff`, {
    data: { targetDefinitionId: null, targetModelId: null, note: 'Evaluator independente para homologação P2.' },
  });
  expect(handoff.ok()).toBe(true);
  const evaluator = (await handoff.json()) as { id: string };
  expect(evaluator.id).not.toBe(actorId);

  await page.getByRole('button', { name: 'Registrar avaliação independente' }).click();
  await page.getByLabel('Agente avaliador').fill(evaluator.id);
  await page.getByLabel('Provedor avaliador').fill('e2e-independent');
  await page.getByLabel('Modelo avaliador').fill('critic-model');
  await page.getByLabel('Justificativa').fill('Avaliação independente aprovada.');
  await page.getByRole('button', { name: 'Confirmar transição' }).click();
  await expectState('evaluated');

  await page.getByRole('button', { name: 'Registrar shadow validation' }).click();
  await page.getByLabel('Amostra').fill('30');
  await page.getByLabel('Delta de primeira tentativa').fill('0.12');
  await page.getByLabel('Delta de erro repetido').fill('-0.08');
  await page.getByLabel('Impacto em tokens').fill('-120');
  await page.getByLabel('Delta de custo').fill('-0.05');
  await page.getByLabel('Regressões').fill('0');
  await page.getByLabel('Referência da evidência').fill(`evidence://shadow/${created.candidate.candidateId}`);
  await page.getByLabel('Justificativa').fill('Shadow sem regressões.');
  await page.getByRole('button', { name: 'Confirmar transição' }).click();
  await expectState('shadow');
  await expect(page.getByText('Monitoramento do shadow')).toBeVisible();

  await transition('Aprovar', 'approved', 'Aprovação humana após shadow.');
  await page.getByRole('button', { name: 'Promover manualmente' }).click();
  const promotion = page.getByRole('button', { name: 'Confirmar transição' });
  await expect(promotion).toBeDisabled();
  await page.getByText(/Confirmo a promoção manual/).click();
  await promotion.click();
  await expectState('promoted');

  await transition('Executar rollback', 'rolled_back', 'Regressão simulada no E2E.');
  await transition('Depreciar', 'deprecated', 'Versão substituída com segurança.');
  await expect(page.getByText('Histórico imutável')).toBeVisible();
  await expect(page.getByRole('list', { name: 'Histórico imutável' }).getByText('deprecate', { exact: true })).toBeVisible();
  await assertA11y(page, 'governança P2 — lifecycle completo');
  await page.screenshot({ path: testInfo.outputPath('desktop-13-dark-governance-p2.png'), fullPage: true });
}

/** Id do projeto de homologação no Host real. */
async function currentProjectId(page: Page): Promise<string> {
  const response = await page.request.get('/api/v1/projects?limit=100');
  expect(response.ok()).toBe(true);
  const body = (await response.json()) as { items: Array<{ id: string; name: string }> };
  const project = body.items.find((item) => item.name === PROJECT_NAME) ?? body.items[0];
  expect(project, 'projeto de homologação deve existir').toBeDefined();
  return project.id;
}

async function exerciseRealtimeAndAudit(page: Page, testInfo: TestInfo) {
  if (testInfo.project.name !== 'desktop-13-dark') return;

  await page.goto('/chat');
  const createdConversationResponse = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === '/api/v1/conversations',
  );
  // Sem nenhuma conversa, a CTA única é a do estado vazio; com conversa, o
  // botão do cabeçalho assume (§4 — nunca as duas ao mesmo tempo).
  const newConversationCta = page.getByRole('button', { name: 'Nova conversa', exact: true });
  const startConversationCta = page.getByRole('button', { name: 'Iniciar conversa', exact: true });
  await expect(newConversationCta.or(startConversationCta)).toBeVisible();
  await ((await newConversationCta.isVisible())
    ? newConversationCta
    : startConversationCta
  ).click();
  const createdConversation = (await (await createdConversationResponse).json()) as { id: string };
  const conversation = page.locator('select#chat-conversation');
  await expect(conversation).toBeVisible();
  await expect(conversation).toHaveValue(createdConversation.id);
  const conversationId = createdConversation.id;
  expect(conversationId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);

  // PRONTIDÃO CANÔNICA: desde o ADR-018/019 o Host real é fail-closed — sem
  // provedor/modelo configurados ele RECUSA o turno (400
  // `invalid_chief_invocation_selection`, "No model is configured for the
  // Chief"), em vez de simular resposta do chefe. Portanto o percurso de turno
  // só é exercitável com provider/model reais no ambiente de teste (§19).
  // Sem eles, validamos o que É verdade contra o Host real: o bloqueio honesto.
  const readiness = await page.request.get(
    `/api/v1/projects/${await currentProjectId(page)}/readiness`,
  );
  expect(readiness.ok(), 'readiness canônico deve responder').toBe(true);
  const readinessSnapshot = (await readiness.json()) as {
    steps: Array<{ step: string; state: string; blockers: Array<{ code: string }> }>;
  };
  const execution = readinessSnapshot.steps.find((step) => step.step === 'ExecutionReady');
  const executable = execution?.state === 'Ready' || execution?.state === 'Simulated';

  if (!executable) {
    // A UI reflete o read model, mas envia: o backend persiste a mensagem e
    // devolve o bloqueio tipado no handle 202.
    await expect(page.getByRole('heading', { name: 'Execução do chefe bloqueada' })).toBeVisible();
    const composer = page.getByRole('textbox', { name: 'Mensagem para o chefe' });
    await expect(composer).toBeEnabled();
    await composer.fill('Registre este turno bloqueado.');
    await page.getByRole('button', { name: 'Enviar mensagem' }).click();
    await expect(page.getByText('Turno registrado, execução bloqueada')).toBeVisible();
    for (const blocker of execution?.blockers ?? []) {
      expect(
        ['provider_account.missing', 'model.none_chat_enabled', 'workflow.unbound', 'chief.model_unresolved'],
        'bloqueador do read model deve ser conhecido pela UI',
      ).toContain(blocker.code);
    }
    await assertA11y(page, 'chat com execução bloqueada (Host real sem provedor)');
    await page.screenshot({
      path: testInfo.outputPath('desktop-13-dark-chat-blocked.png'),
      fullPage: true,
    });
    test.info().annotations.push({
      type: 'skip-motivo',
      description:
        'Turno do chefe não exercitado: Host real sem provedor/modelo configurados (fail-closed, ADR-018/019).',
    });
    return;
  }

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
  await expect(page.getByText('Learning candidates')).toBeVisible();
  await page.getByRole('tab', { name: 'Auditoria' }).click();
  const csvDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Exportar CSV' }).click();
  const download = await csvDownload;
  expect(download.suggestedFilename()).toBe('poseidon-auditoria.csv');
  await download.saveAs(testInfo.outputPath(download.suggestedFilename()));
}

test('Host real — onboarding, navegação, HTTP, SignalR, responsividade e a11y', async ({ page }, testInfo) => {
  test.setTimeout(300_000);
  const runtime = watchRuntime(page);
  await setTheme(page, testInfo.project.name.includes('light'));
  const profileName = await ensureSession(page);
  await ensureWorkingDirectory(page);
  await ensureOrganization(page);
  await ensureProject(page, profileName);

  for (const route of ROUTES) {
    await page.goto(route);
    await expect(page.getByRole('main')).toBeVisible();
    await expect(page.getByText('Não foi possível carregar os dados. Tente novamente.')).toHaveCount(0);
    await page.waitForTimeout(400);
    await assertA11y(page, `${testInfo.project.name} ${route}`);
    await captureEvidence(page, testInfo, route);
  }

  await exerciseRealtimeAndAudit(page, testInfo);
  await exerciseMermaidDocument(page, testInfo);
  await exerciseLearningP2(page, testInfo);

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
