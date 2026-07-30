/**
 * QA real de navegador contra o Host em execução (não o mock MSW).
 * Faz login com perfil local, percorre as rotas e mede, por rota:
 *   - violações axe sérias/críticas (WCAG 2.1 AA)
 *   - erros de console e exceções de página
 *   - respostas HTTP >= 400
 *   - vazamento de vocabulário técnico no modo Negócio
 */
import { chromium } from '@playwright/test';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.BASE ?? 'http://127.0.0.1:5173';
const ROUTES = process.argv.slice(2).length
  ? process.argv.slice(2)
  : ['/cockpit', '/chat', '/projects', '/delivery', '/board', '/conversations', '/documents',
     '/workflows', '/prototypes', '/organizations', '/channels', '/licenses', '/notifications',
     '/settings', '/run-project', '/orchestrator', '/approvals'];

// Termos do léxico §2 que jamais podem aparecer na tela no modo Negócio.
const FORBIDDEN = /\b(worktree|lease|fencing|heartbeat|tenant|slug|provider|payload|endpoint|webhook|outbox|ledger|watchdog|scopeclaim|migration)\b/i;

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();

const consoleErrors = [];
const httpErrors = [];
page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push({ route: page.url(), text: m.text().slice(0, 200) }); });
page.on('pageerror', (e) => consoleErrors.push({ route: page.url(), text: 'PAGEERROR: ' + String(e).slice(0, 200) }));
page.on('response', (r) => { if (r.status() >= 400) httpErrors.push({ route: page.url(), entry: `${r.status()} ${r.request().method()} ${r.url().replace(BASE, '')}` }); });

// ---- login: escolhe o perfil local existente
await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
if (page.url().includes('/onboarding')) {
  const enter = page.getByRole('button', { name: /Entrar|Operador/ }).first();
  await enter.click();
  await page.waitForTimeout(2000);
}
console.log('após login →', page.url());

const report = [];
for (const route of ROUTES) {
  const before = consoleErrors.length;
  const beforeHttp = httpErrors.length;
  try {
    await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle', timeout: 30000 });
  } catch { /* segue: registra o que conseguiu medir */ }
  await page.waitForTimeout(1200);

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');

  const body = await page.locator('body').innerText().catch(() => '');
  const leak = body.match(FORBIDDEN);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);

  report.push({
    route,
    url: page.url(),
    axe: blocking.map((v) => `${v.id}:${v.nodes.length}`),
    axeDetail: blocking.flatMap((v) => v.nodes.slice(0, 2).map((n) => `${v.id} ← ${n.html.slice(0, 110)}`)),
    console: consoleErrors.length - before,
    http: httpErrors.slice(beforeHttp).map((h) => h.entry),
    leak: leak ? leak[0] : null,
    overflow,
  });
}

console.log('\n╔══════════ QA DE NAVEGADOR — HOST REAL ' + BASE);
for (const r of report) {
  const flags = [
    r.axe.length ? `axe[${r.axe.join(',')}]` : null,
    r.console ? `console:${r.console}` : null,
    r.http.length ? `http:${[...new Set(r.http)].join(' ')}` : null,
    r.leak ? `LÉXICO:"${r.leak}"` : null,
    r.overflow ? 'OVERFLOW-X' : null,
  ].filter(Boolean);
  console.log(`${flags.length ? '✘' : '✓'} ${r.route.padEnd(16)} ${flags.join('  ') || 'ok'}`);
  for (const d of r.axeDetail) console.log(`      ${d}`);
}
console.log('\nERROS DE CONSOLE (amostra):');
for (const e of consoleErrors.slice(0, 10)) console.log('  -', e.text);
await browser.close();
