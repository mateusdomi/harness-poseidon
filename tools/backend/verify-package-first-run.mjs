import { createRequire } from 'node:module';
import fs from 'node:fs/promises';
import path from 'node:path';

const baseURL = process.env.POSEIDON_PACKAGE_URL;
const dependencies = process.env.POSEIDON_PLAYWRIGHT_ROOT;
const evidenceDirectory = process.env.POSEIDON_EVIDENCE_DIR;
const phase = process.argv[2];
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(baseURL ?? '')) {
  throw new Error('POSEIDON_PACKAGE_URL deve apontar para o pacote em loopback HTTP.');
}
if (!dependencies || !evidenceDirectory || !['first-run', 'post-restart'].includes(phase)) {
  throw new Error('Uso: verify-package-first-run.mjs {first-run|post-restart} com diretórios configurados.');
}

const require = createRequire(path.join(dependencies, 'package.json'));
const { chromium } = require('@playwright/test');
await fs.mkdir(evidenceDirectory, { recursive: true });

const PROFILE_NAME = 'Gate First Run';
const ORGANIZATION_NAME = 'Organização First Run';
const PROJECT_NAME = 'Projeto First Run';
const pending = new Set();
const network = [];
const consoleIssues = [];
const assetFailures = [];
const webSockets = [];
const startedAt = new WeakMap();
const harPath = path.join(evidenceDirectory, `${phase}.har`);

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({
  locale: 'pt-BR',
  viewport: { width: 1280, height: 800 },
  recordHar: {
    path: harPath,
    content: 'omit',
  },
});
const page = await context.newPage();
page.on('console', (message) => {
  if (message.type() === 'error') consoleIssues.push(message.text());
});
page.on('pageerror', (error) => consoleIssues.push(error.message));
page.on('request', (request) => {
  pending.add(request);
  startedAt.set(request, performance.now());
});
page.on('requestfailed', (request) => pending.delete(request));
page.on('response', (response) => {
  const request = response.request();
  pending.delete(request);
  const type = request.resourceType();
  network.push({
    method: request.method(),
    path: new URL(request.url()).pathname,
    status: response.status(),
    latencyMs: Math.round((performance.now() - (startedAt.get(request) ?? performance.now())) * 100) / 100,
  });
  const pathname = new URL(request.url()).pathname;
  if (response.status() >= 400 &&
      (pathname === '/favicon.ico' || ['document', 'font', 'image', 'script', 'stylesheet'].includes(type))) {
    assetFailures.push(`${type} ${response.status()} ${request.url()}`);
  }
});
page.on('websocket', (socket) => webSockets.push(new URL(socket.url()).pathname));

async function expectStatus(response, status, operation) {
  if (response.status() !== status) {
    throw new Error(`${operation}: HTTP ${response.status()}, esperado ${status}.`);
  }
  return response;
}

async function expectNoSkeleton(route) {
  await page.waitForURL(new RegExp(`${route}$`), { timeout: 15_000 });
  await page.getByRole('main').waitFor({ state: 'visible' });
  await page.waitForFunction(() => document.querySelectorAll('.animate-pulse').length === 0, null, {
    timeout: 15_000,
  });
}

async function completeOnboarding() {
  await page.goto(`${baseURL}/`, { waitUntil: 'networkidle' });
  // O SPA empacotado não declara <link rel="icon">: o navegador solicita /favicon.ico
  // por padrão e o Host serve o fallback empacotado (image/png). Honramos um link
  // declarado se existir, senão validamos exatamente esse contrato /favicon.ico.
  const iconLink = page.locator('link[rel~="icon"]');
  const faviconHref = (await iconLink.count()) > 0
    ? (await iconLink.first().getAttribute('href')) ?? '/favicon.ico'
    : '/favicon.ico';
  const favicon = await context.request.get(new URL(faviconHref, baseURL).toString());
  await expectStatus(favicon, 200, 'favicon empacotado');
  if (!favicon.headers()['content-type']?.startsWith('image/')) {
    throw new Error('favicon empacotado não retornou Content-Type de imagem.');
  }
  await page.waitForURL(/\/onboarding$/);
  await page.getByRole('form', { name: 'Configuração inicial' }).waitFor();
  await page.screenshot({
    path: path.join(evidenceDirectory, 'first-run-01-onboarding.png'),
    fullPage: true,
  });
  await page.getByLabel('Nome de exibição').fill(PROFILE_NAME);
  await page.getByLabel('E-mail (opcional)').fill('first-run@poseidon.local');
  await page.getByRole('button', { name: 'Avançar' }).click();
  await page.getByRole('button', { name: 'Avançar' }).click();
  await page.getByLabel('Diretório de trabalho').fill('/tmp/poseidon-first-run');
  await page.getByRole('button', { name: 'Avançar' }).click();
  await page.getByLabel('Entendo os riscos').check();
  await page.getByRole('button', { name: 'Concluir' }).click();
  await expectNoSkeleton('/cockpit');
  await page.screenshot({
    path: path.join(evidenceDirectory, 'first-run-02-cockpit-empty.png'),
    fullPage: true,
  });
}

async function createOrganizationAndProject() {
  await page.goto(`${baseURL}/organizations`);
  await page.getByRole('heading', { name: 'Organizações' }).waitFor();
  await page.getByRole('button', { name: 'Nova organização', exact: true }).click();
  await page.getByLabel('Nome').fill(ORGANIZATION_NAME);
  await page.getByLabel('Slug').fill('first-run');
  await page.getByRole('button', { name: 'Criar organização' }).click();
  await page.getByText(ORGANIZATION_NAME, { exact: true }).waitFor();

  await page.goto(`${baseURL}/projects`);
  await page.getByRole('heading', { name: 'Projetos' }).waitFor();
  await page.getByRole('button', { name: 'Novo projeto', exact: true }).click();
  await page.getByLabel('Título').fill(PROJECT_NAME);
  await page.getByLabel('Slug (sigla)').fill('FIRST');
  await page.getByLabel('Descrição').fill('Gate de primeiro uso do pacote self-contained.');
  await page.getByRole('tab', { name: 'Pessoas' }).click();
  await page.getByLabel(new RegExp(PROFILE_NAME)).check();
  await page.getByRole('button', { name: 'Criar projeto' }).click();
  await page.getByRole('button', { name: PROJECT_NAME, exact: false }).waitFor();
  await page.goto(`${baseURL}/cockpit`);
  await expectNoSkeleton('/cockpit');
  await page.screenshot({
    path: path.join(evidenceDirectory, 'first-run-03-cockpit-project.png'),
    fullPage: true,
  });
}

async function verifyInvalidCookieRecovery() {
  const invalid = await browser.newContext();
  try {
    await invalid.addCookies([{
      name: 'harness.profile',
      value: 'not-a-valid-ulid',
      url: baseURL,
      httpOnly: true,
      sameSite: 'Strict',
    }]);
    const response = await invalid.request.get(`${baseURL}/api/v1/profiles/current`);
    await expectStatus(response, 200, 'cookie inválido deve recuperar sessão pessoal');
    const cookies = await invalid.cookies(baseURL);
    const recovered = cookies.find((cookie) => cookie.name === 'harness.profile');
    if (!recovered || recovered.value === 'not-a-valid-ulid') {
      throw new Error('cookie inválido não foi substituído pela sessão pessoal existente.');
    }
  } finally {
    await invalid.close();
  }
}

async function verifyGovernance() {
  await page.goto(`${baseURL}/governance`);
  await page.getByRole('heading', { name: 'Governança de agentes' }).waitFor();
  await page.getByRole('tab', { name: 'Bundles e receipts' }).click();
  await page.getByRole('tab', { name: 'Documentos e saúde' }).click();
  await page.getByRole('tab', { name: 'Aprendizado P2' }).click();
  await page.getByText('Learning candidates').waitFor();
  await page.screenshot({
    path: path.join(evidenceDirectory, 'first-run-04-governance-p1-p2.png'),
    fullPage: true,
  });
}

async function sanitizeHar(file) {
  const har = JSON.parse(await fs.readFile(file, 'utf8'));
  const visit = (value, parentKey = '') => {
    if (Array.isArray(value)) {
      for (const item of value) {
        if (parentKey === 'cookies' && item && typeof item === 'object' && 'value' in item) {
          item.value = '[REDACTED]';
        }
        visit(item, parentKey);
      }
      return;
    }
    if (!value || typeof value !== 'object') return;
    if (typeof value.name === 'string' &&
        ['authorization', 'cookie', 'set-cookie'].includes(value.name.toLowerCase()) &&
        'value' in value) {
      value.value = '[REDACTED]';
    }
    for (const [key, child] of Object.entries(value)) visit(child, key);
  };
  visit(har);
  await fs.writeFile(file, `${JSON.stringify(har)}\n`, { mode: 0o600 });
  await fs.chmod(file, 0o600);
}

let phaseFailure = null;
try {
  if (phase === 'first-run') {
    const profilesBefore = await context.request.get(`${baseURL}/api/v1/profiles?limit=1`);
    await expectStatus(profilesBefore, 200, 'lista de perfis inicial');
    const initialPage = await profilesBefore.json();
    if (initialPage.items.length !== 0) throw new Error('data dir do first-run não está vazio.');
    await completeOnboarding();
    await createOrganizationAndProject();
    await verifyInvalidCookieRecovery();
    await verifyGovernance();
  } else {
    await page.goto(`${baseURL}/onboarding`, { waitUntil: 'networkidle' });
    await page.getByText(PROFILE_NAME, { exact: true }).waitFor();
    await page.getByText(PROFILE_NAME, { exact: true })
      .locator('xpath=ancestor::li').getByRole('button', { name: 'Entrar' }).click();
    await expectNoSkeleton('/cockpit');
    const projects = await context.request.get(`${baseURL}/api/v1/projects?limit=100`);
    await expectStatus(projects, 200, 'projetos após restart');
    const projectPage = await projects.json();
    if (!projectPage.items.some((item) => item.name === PROJECT_NAME)) {
      throw new Error('projeto criado no first-run não persistiu após restart.');
    }
    await page.screenshot({
      path: path.join(evidenceDirectory, 'post-restart-01-cockpit-persisted.png'),
      fullPage: true,
    });
  }
} catch (error) {
  phaseFailure = error instanceof Error ? error.message : String(error);
  await page.screenshot({
    path: path.join(evidenceDirectory, `${phase}-failure.png`),
    fullPage: true,
  }).catch(() => {});
}

await page.waitForTimeout(1_000);
const pendingApi = [...pending]
  .filter((request) => new URL(request.url()).pathname.startsWith('/api/'))
  .map((request) => `${request.method()} ${new URL(request.url()).pathname}`);
const result = {
  phase,
  baseURL,
  consoleIssues,
  assetFailures,
  pendingApi,
  webSockets,
  network,
  failure: phaseFailure,
};
await context.close();
await browser.close();
await sanitizeHar(harPath);
await fs.writeFile(
  path.join(evidenceDirectory, `${phase}.json`),
  `${JSON.stringify(result, null, 2)}\n`,
  { mode: 0o600 },
);
if (phaseFailure) throw new Error(`Gate ${phase} falhou: ${phaseFailure}`);
if (consoleIssues.length || assetFailures.length || pendingApi.length) {
  throw new Error(`Runtime impuro: ${JSON.stringify({ consoleIssues, assetFailures, pendingApi })}`);
}
if (phase === 'first-run' && !webSockets.some((url) => url === '/hubs/events')) {
  throw new Error('SignalR WebSocket não foi observado no pacote durante o first-run.');
}
console.log(`package-${phase}: verde; ${network.length} respostas; ${webSockets.length} WebSocket(s).`);
