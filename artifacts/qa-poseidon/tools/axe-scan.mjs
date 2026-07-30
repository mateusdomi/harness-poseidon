import { chromium } from '@playwright/test';
import { AxeBuilder } from '@axe-core/playwright';

const BASE = process.env.BASE ?? 'http://127.0.0.1:5173';
const ROUTES = process.argv.slice(2);

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

for (const route of ROUTES) {
  await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  console.log(`\n=== ${route} — ${blocking.length} violação(ões) bloqueante(s)`);
  for (const v of blocking) {
    console.log(`  [${v.impact}] ${v.id} (${v.nodes.length} nós) — ${v.help}`);
    for (const n of v.nodes.slice(0, 3)) {
      console.log(`      target: ${JSON.stringify(n.target)}`);
      console.log(`      html:   ${n.html.slice(0, 150)}`);
    }
  }
}
await browser.close();
