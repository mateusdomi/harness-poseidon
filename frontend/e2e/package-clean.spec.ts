import { expect, test, type Page, type Request } from '@playwright/test';
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

const PROFILE_NAME = 'Gate pacote limpo';
const ORGANIZATION_NAME = 'Organização pacote limpo';
const PROJECT_NAME = 'Projeto pacote limpo';
const WORKING_DIRECTORY = process.env.POSEIDON_WORKING_DIRECTORY ?? path.resolve(process.cwd(), '..');
const SKELETON_LIMIT_MS = 12_000;

interface NetworkEntry {
  method: string;
  url: string;
  status?: number;
  durationMs?: number;
  failure?: string;
}

function watchNetwork(page: Page) {
  const started = new Map<Request, number>();
  const entries: NetworkEntry[] = [];
  const isApi = (request: Request) => request.url().includes('/api/');
  page.on('request', (request) => {
    if (isApi(request)) started.set(request, Date.now());
  });
  page.on('response', (response) => {
    const request = response.request();
    if (!isApi(request)) return;
    entries.push({
      method: request.method(),
      url: request.url(),
      status: response.status(),
      durationMs: Date.now() - (started.get(request) ?? Date.now()),
    });
    started.delete(request);
  });
  page.on('requestfailed', (request) => {
    if (!isApi(request)) return;
    entries.push({
      method: request.method(),
      url: request.url(),
      durationMs: Date.now() - (started.get(request) ?? Date.now()),
      failure: request.failure()?.errorText ?? 'request failed',
    });
    started.delete(request);
  });
  return { entries, pending: started };
}

function watchRuntime(page: Page) {
  const applicationErrors: string[] = [];
  const browserNetworkDiagnostics: string[] = [];
  const assetErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() !== 'error') return;
    const text = message.text();
    if (
      text.startsWith('Failed to load resource:')
      || (text.startsWith('WebSocket connection to ') && text.includes(' failed:'))
    ) {
      browserNetworkDiagnostics.push(text);
    } else {
      applicationErrors.push(text);
    }
  });
  page.on('pageerror', (error) => applicationErrors.push(error.message));
  page.on('response', (response) => {
    if (
      response.status() >= 400
      && ['font', 'image', 'script', 'stylesheet'].includes(response.request().resourceType())
    ) {
      assetErrors.push(`${response.status()} ${response.url()}`);
    }
  });
  return { applicationErrors, browserNetworkDiagnostics, assetErrors };
}

async function expectTerminalScreen(page: Page, context: string) {
  await expect(page.getByRole('main'), `${context}: a rota deve montar o conteúdo principal`).toBeVisible();
  await expect(
    page.locator('.animate-pulse'),
    `${context}: skeleton permaneceu além de ${SKELETON_LIMIT_MS}ms`,
  ).toHaveCount(0, { timeout: SKELETON_LIMIT_MS });
}

async function createProfile(page: Page) {
  const wizard = page.getByRole('form', { name: 'Configuração inicial' });
  await expect(wizard).toBeVisible();
  await page.getByLabel('Nome de exibição').fill(PROFILE_NAME);
  await page.getByLabel('E-mail (opcional)').fill('package-clean@poseidon.local');
  await page.getByRole('button', { name: 'Avançar' }).click();
  await page.getByRole('button', { name: 'Avançar' }).click();
  await page.getByLabel('Diretório de trabalho').fill(WORKING_DIRECTORY);
  await page.getByRole('button', { name: 'Avançar' }).click();
  await page.getByLabel('Entendo os riscos').check();
  await page.getByRole('button', { name: 'Concluir' }).click();
  await expect(page).toHaveURL(/\/cockpit$/);
}

async function createOrganization(page: Page) {
  await page.goto('/organizations');
  // Coleção vazia: a CTA única é a do empty state (§4). O slug é gerado do nome
  // automaticamente (§7), sem preenchimento manual no fluxo comum.
  await page.getByRole('button', { name: 'Criar organização' }).click();
  await page.getByLabel('Nome').fill(ORGANIZATION_NAME);
  await page.getByRole('button', { name: 'Criar organização' }).click();
  await expect(page.getByText(ORGANIZATION_NAME, { exact: true })).toBeVisible();
}

async function createProject(page: Page) {
  await page.goto('/projects');
  // Coleção vazia: CTA única do empty state.
  await page.getByRole('button', { name: 'Criar projeto' }).click();
  await page.getByLabel('Título').fill(PROJECT_NAME);
  await page.getByLabel('Slug (sigla)').fill('PKGCLEAN');
  await page.getByLabel('Descrição').fill('Regressão do pacote self-contained com data dir vazio.');
  await page.getByRole('tab', { name: 'Pessoas' }).click();
  await page.getByLabel(new RegExp(PROFILE_NAME)).check();
  await page.getByRole('button', { name: 'Criar projeto' }).click();
  await expect(page.getByRole('button', { name: PROJECT_NAME, exact: false })).toBeVisible();
}

async function stubProjects(page: Page, status: number, body: string, contentType = 'application/problem+json') {
  await page.route('**/api/v1/projects*', (route) => route.fulfill({
    status,
    contentType,
    body,
  }));
}

test('pacote self-contained encerra todo loading e recupera estados de falha', async ({ page, context }) => {
  test.setTimeout(240_000);
  const network = watchNetwork(page);
  const runtime = watchRuntime(page);
  const packageOrigin = new URL(process.env.POSEIDON_PACKAGE_URL!).origin;

  expect(await context.cookies()).toEqual([]);
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  expect(await page.evaluate(() => localStorage.getItem('poseidon-session'))).toBeNull();
  await expectTerminalScreen(page, 'primeira abertura com navegador e data dir limpos');

  const scripts = await page.locator('script[src]').evaluateAll((nodes) =>
    nodes.map((node) => (node as HTMLScriptElement).src),
  );
  expect(scripts.length).toBeGreaterThan(0);
  for (const scriptUrl of scripts) {
    const body = await (await page.request.get(scriptUrl)).text();
    expect(body).not.toContain('127.0.0.1:5090');
    expect(body).not.toContain('127.0.0.1:5173');
  }

  await createProfile(page);
  await expectTerminalScreen(page, 'cockpit sem organização e sem projeto');
  await expect(page.getByRole('heading', { name: 'Nenhum projeto ativo' })).toBeVisible();
  expect(
    network.entries.filter((entry) => /\/api\/v1\/(tasks|approvals|agents|workflow-runs)/.test(entry.url)),
    'queries dependentes de projeto não devem iniciar sem projeto ativo',
  ).toEqual([]);
  expect(network.pending.size, 'nenhuma chamada pode ficar pendente no estado vazio').toBe(0);

  await createOrganization(page);
  await createProject(page);
  await page.goto('/cockpit');
  await expectTerminalScreen(page, 'cockpit com projeto ativo');

  for (const route of ROUTES) {
    await page.goto(route);
    await expectTerminalScreen(page, route);
  }
  expect(runtime.applicationErrors, 'fluxo nominal sem console error da aplicação').toEqual([]);
  expect(runtime.browserNetworkDiagnostics, 'fluxo nominal sem erro de rede no console').toEqual([]);

  await context.setOffline(true);
  await expect(page.getByText(/Sem conexão em tempo real|Reconectando ao tempo real/)).toBeVisible({ timeout: 40_000 });
  await context.setOffline(false);
  await expect(page.getByText(/Sem conexão em tempo real|Reconectando ao tempo real/)).toBeHidden({ timeout: 15_000 });

  await page.route('**/api/v1/projects*', (route) => route.abort('connectionrefused'));
  await page.goto('/projects');
  await expectTerminalScreen(page, 'API indisponível');
  await expect(page.getByRole('alert')).toContainText('Não foi possível carregar os dados');
  await page.unroute('**/api/v1/projects*');

  await stubProjects(page, 409, JSON.stringify({ status: 409, title: 'Conflict', detail: 'stale request' }));
  await page.goto('/projects');
  await expectTerminalScreen(page, 'HTTP 409');
  await expect(page.getByRole('alert')).toBeVisible();
  await page.unroute('**/api/v1/projects*');

  await stubProjects(page, 500, JSON.stringify({ status: 500, title: 'Failure', detail: 'controlled' }));
  await page.goto('/projects');
  await expectTerminalScreen(page, 'HTTP 500');
  await expect(page.getByRole('alert')).toBeVisible();
  await page.unroute('**/api/v1/projects*');

  await stubProjects(page, 200, '{', 'application/json');
  await page.goto('/projects');
  await expectTerminalScreen(page, 'resposta JSON inválida');
  await expect(page.getByRole('alert')).toBeVisible();
  await page.unroute('**/api/v1/projects*');

  await stubProjects(page, 403, JSON.stringify({ status: 403, title: 'Forbidden', detail: 'controlled' }));
  await page.goto('/projects');
  await expectTerminalScreen(page, 'HTTP 403');
  await expect(page.getByRole('heading', { name: 'Acesso não permitido' })).toBeVisible();
  await page.unroute('**/api/v1/projects*');

  await page.route('**/api/v1/profiles/current', (route) => route.fulfill({
    status: 401,
    contentType: 'application/problem+json',
    body: JSON.stringify({ status: 401, title: 'Unauthorized', detail: 'controlled' }),
  }));
  await page.goto('/cockpit');
  await expect(page).toHaveURL(/\/onboarding$/);
  await expectTerminalScreen(page, 'HTTP 401');
  await page.unroute('**/api/v1/profiles/current');

  await page.getByText(PROFILE_NAME, { exact: true }).locator('xpath=ancestor::li')
    .getByRole('button', { name: 'Entrar' }).click();
  await expect(page).toHaveURL(/\/cockpit$/);
  await expectTerminalScreen(page, 'recuperação após 401');

  await context.addCookies([{
    name: 'harness.profile',
    value: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
    domain: '127.0.0.1',
    path: '/',
    httpOnly: true,
    sameSite: 'Lax',
  }]);
  await page.goto('/cockpit');
  await expect(page).toHaveURL(/\/onboarding$/);
  await expectTerminalScreen(page, 'cookie antigo ou inexistente');

  await expect.poll(() => network.pending.size, {
    message: 'todas as requisições Fetch/XHR devem terminar',
    timeout: 15_000,
  }).toBe(0);
  for (const entry of network.entries) {
    expect(new URL(entry.url).origin, `${entry.method} ${entry.url} deve ser same-origin`).toBe(packageOrigin);
    expect(entry.durationMs).toBeDefined();
  }
  expect(network.entries.some((entry) => entry.status === 401)).toBe(true);
  expect(network.entries.some((entry) => entry.status === 403)).toBe(true);
  expect(network.entries.some((entry) => entry.status === 404)).toBe(true);
  expect(network.entries.some((entry) => entry.status === 409)).toBe(true);
  expect(network.entries.some((entry) => entry.status === 500)).toBe(true);
  expect(runtime.applicationErrors, 'zero console error da aplicação').toEqual([]);
  expect(runtime.browserNetworkDiagnostics.length, 'falhas de rede/status injetadas devem ser observáveis').toBeGreaterThan(0);
  expect(runtime.assetErrors, 'zero asset 404/erro').toEqual([]);
});
