import { createRequire } from 'node:module';
import fs from 'node:fs/promises';
import path from 'node:path';

// Gate de governança contra o pacote self-contained: cria uma sessão mínima e exerce todos os
// GETs de governança publicados no OpenAPI, exigindo ZERO respostas 500 no cenário nominal vazio,
// corpos JSON válidos e páginas Governança/Notificações sem console error nem asset 404.
// Preserva os corpos de resposta sanitizados como evidência. Reproduz o defeito P1 da RC2
// (stale-doc-findings 500 por documento do manifest ausente no pacote instalado).

const baseURL = process.env.POSEIDON_PACKAGE_URL;
const dependencies = process.env.POSEIDON_PLAYWRIGHT_ROOT;
const evidenceDirectory = process.env.POSEIDON_EVIDENCE_DIR;
const phase = process.argv[2] ?? 'initial';
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(baseURL ?? '')) {
  throw new Error('POSEIDON_PACKAGE_URL deve apontar para o pacote em loopback HTTP.');
}
if (!dependencies || !evidenceDirectory) {
  throw new Error('Uso: verify-governance-endpoints.mjs [fase] com POSEIDON_PLAYWRIGHT_ROOT e POSEIDON_EVIDENCE_DIR.');
}

const require = createRequire(path.join(dependencies, 'package.json'));
const { chromium } = require('@playwright/test');
await fs.mkdir(evidenceDirectory, { recursive: true });

// GETs de leitura sem parâmetro de rota: devem responder 200 + JSON no cenário nominal vazio.
const NOMINAL_GETS = [
  '/api/v1/diagnostics',
  '/api/v1/governance-runtime/stale-doc-findings',
  '/api/v1/governance-runtime/receipts?limit=100',
  '/api/v1/governance-runtime/executors',
  '/api/v1/governance-runtime/hashline-benchmark',
  '/api/v1/governance-runtime/learning-candidates?limit=100',
  '/api/v1/governance-runtime/learning-candidates/metrics',
];

// GETs com parâmetro de rota: com identificador inexistente devem devolver 404/400, nunca 500.
const MISSING_ID_GETS = [
  '/api/v1/governance-runtime/receipts/nonexistent-turn',
  '/api/v1/governance-runtime/receipts/nonexistent-turn/metrics',
  '/api/v1/governance-runtime/learning-candidates/nonexistent-candidate',
  '/api/v1/governance-runtime/learning-candidates/nonexistent-candidate/compare',
  '/api/v1/governance-runtime/learning-candidates/nonexistent-candidate/evidence',
  '/api/v1/governance-runtime/learning-candidates/nonexistent-candidate/history',
];

function sanitizeHeaders(headers) {
  const clone = { ...headers };
  for (const key of Object.keys(clone)) {
    if (['set-cookie', 'cookie', 'authorization'].includes(key.toLowerCase())) clone[key] = '[REDACTED]';
  }
  return clone;
}

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ locale: 'pt-BR', viewport: { width: 1280, height: 800 } });

// Sessão mínima: onboarding cria o perfil pessoal (com cookie HttpOnly gerido pelo Host).
const created = await context.request.post(`${baseURL}/api/v1/profiles`, {
  data: { displayName: `Governance Gate ${phase}`, email: null, avatarUrl: null, locale: 'pt-BR' },
});
if (![200, 201, 409].includes(created.status())) {
  await browser.close();
  throw new Error(`sessão mínima falhou: POST /profiles HTTP ${created.status()}.`);
}

const results = [];
const failures = [];

for (const route of NOMINAL_GETS) {
  const response = await context.request.get(`${baseURL}${route}`);
  const status = response.status();
  const body = await response.text();
  let parsed = null;
  let jsonValid = true;
  try { parsed = JSON.parse(body); } catch { jsonValid = false; }
  const record = { route, status, jsonValid, headers: sanitizeHeaders(response.headers()) };
  results.push({ ...record, body: parsed });
  if (status >= 500) failures.push(`${route} => HTTP ${status} (500+ proibido no cenário nominal)`);
  else if (status !== 200) failures.push(`${route} => HTTP ${status} (esperado 200 no cenário nominal vazio)`);
  else if (!jsonValid) failures.push(`${route} => corpo não é JSON válido`);
}

for (const route of MISSING_ID_GETS) {
  const response = await context.request.get(`${baseURL}${route}`);
  const status = response.status();
  results.push({ route, status, headers: sanitizeHeaders(response.headers()) });
  if (status >= 500) failures.push(`${route} => HTTP ${status} (id inexistente deve dar 404/400, nunca 500)`);
}

// Requisições concorrentes ao endpoint de leitura idempotente não podem produzir 500
// (acesso duplicado/concorrência sobre o catálogo de documentos e o store).
const concurrent = await Promise.all(
  Array.from({ length: 8 }, () => context.request.get(`${baseURL}/api/v1/governance-runtime/stale-doc-findings`)));
const concurrentStatuses = concurrent.map((response) => response.status());
results.push({ route: 'stale-doc-findings x8 (concorrente)', statuses: concurrentStatuses });
if (concurrentStatuses.some((status) => status >= 500)) {
  failures.push(`stale-doc-findings concorrente retornou 500: ${JSON.stringify(concurrentStatuses)}`);
}
if (new Set(concurrentStatuses).size !== 1) {
  failures.push(`stale-doc-findings concorrente não determinístico: ${JSON.stringify(concurrentStatuses)}`);
}

// Páginas Governança e Notificações: sem console error da aplicação e sem asset 404.
const consoleIssues = [];
const assetFailures = [];
const page = await context.newPage();
page.on('console', (message) => { if (message.type() === 'error') consoleIssues.push(message.text()); });
page.on('pageerror', (error) => consoleIssues.push(error.message));
page.on('response', (response) => {
  const pathname = new URL(response.request().url()).pathname;
  const type = response.request().resourceType();
  if (response.status() >= 400 &&
      (pathname === '/favicon.ico' || ['document', 'font', 'image', 'script', 'stylesheet'].includes(type))) {
    assetFailures.push(`${type} ${response.status()} ${pathname}`);
  }
  if (response.status() >= 500 && pathname.startsWith('/api/')) {
    failures.push(`navegação: ${pathname} => HTTP ${response.status()}`);
  }
});

for (const [route, screenshot] of [['/governance', 'governance'], ['/notifications', 'notifications']]) {
  await page.goto(`${baseURL}${route}`, { waitUntil: 'networkidle' });
  await page.getByRole('main').waitFor({ state: 'visible' });
  await page.waitForFunction(() => document.querySelectorAll('.animate-pulse').length === 0, null, { timeout: 15_000 })
    .catch(() => { failures.push(`${route}: skeleton não terminou em 15s`); });
  await page.screenshot({ path: path.join(evidenceDirectory, `governance-endpoints-${phase}-${screenshot}.png`), fullPage: true });
}

if (consoleIssues.length) failures.push(`console errors: ${JSON.stringify(consoleIssues)}`);
if (assetFailures.length) failures.push(`asset 404: ${JSON.stringify(assetFailures)}`);

await fs.writeFile(
  path.join(evidenceDirectory, `governance-endpoints-${phase}.json`),
  `${JSON.stringify({ phase, baseURL, results, failures }, null, 2)}\n`,
  { mode: 0o600 });

await context.close();
await browser.close();

if (failures.length) {
  throw new Error(`Gate de governança (${phase}) falhou:\n - ${failures.join('\n - ')}`);
}
console.log(`governance-endpoints-${phase}: verde; ${NOMINAL_GETS.length} GETs nominais + ${MISSING_ID_GETS.length} com id inexistente sem 500.`);
