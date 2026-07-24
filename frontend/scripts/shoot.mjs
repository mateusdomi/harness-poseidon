import { chromium } from '@playwright/test';

const BASE = process.env.SHOOT_BASE ?? 'http://localhost:4173';
const OUT = process.env.SHOOT_OUT ?? '.artifacts-screens';
const TAG = process.env.SHOOT_TAG ?? 'after';

// Rotas a capturar: [rota, nome, açãoOpcionalDeNavegação]
const ROUTES = [
  ['/cockpit', 'cockpit'],
  ['/chat', 'chat'],
  ['/board', 'board'],
  ['/providers', 'providers'],
  ['/delivery', 'delivery'],
  ['/agents', 'agents'],
  ['/orchestrator', 'orchestrator'],
];

const VIEWPORTS = [
  ['mobile', 375, 812],
  ['desktop', 1440, 900],
];
const THEMES = ['dark', 'light'];

// Só o cockpit em todos os viewports/temas; demais rotas: só mobile 375 dark (auditoria de quebras).
const FULL_MATRIX = new Set(['cockpit']);

function themeStorage(theme) {
  return JSON.stringify({ state: { preference: theme }, version: 0 });
}

async function pickProfile(page) {
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  // Guard leva ao onboarding; seleciona o perfil "Mateus".
  const btn = page.getByRole('button', { name: /Mateus/ });
  if (await btn.first().isVisible().catch(() => false)) {
    await btn.first().click();
    await page.waitForURL(/\/cockpit$/, { timeout: 15000 }).catch(() => {});
  }
}

const browser = await chromium.launch();
let shot = 0;
for (const [vpName, w, h] of VIEWPORTS) {
  for (const theme of THEMES) {
    const context = await browser.newContext({
      viewport: { width: w, height: h },
      deviceScaleFactor: 2,
    });
    // Seed do tema + perfil antes de qualquer carga.
    await context.addInitScript(
      ([t]) => {
        localStorage.setItem('poseidon-theme', t);
      },
      [themeStorage(theme)],
    );
    const page = await context.newPage();
    await pickProfile(page);

    for (const [route, name] of ROUTES) {
      if (name !== 'cockpit' && !(vpName === 'mobile' && theme === 'dark')) continue;
      if (!FULL_MATRIX.has(name) && !(vpName === 'mobile' && theme === 'dark')) continue;
      await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle' });
      // Espera o conteúdo real substituir os skeletons das queries.
      await page
        .locator('[role="status"]')
        .first()
        .waitFor({ state: 'hidden', timeout: 8000 })
        .catch(() => {});
      await page.waitForTimeout(1500);
      const file = `${OUT}/${TAG}-${name}-${vpName}-${theme}.png`;
      await page.screenshot({ path: file, fullPage: true });
      shot += 1;
      console.log('shot', file);
    }
    await context.close();
  }
}
await browser.close();
console.log(`done: ${shot} screenshots`);
