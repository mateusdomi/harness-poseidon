import { chromium } from '@playwright/test';
const B='http://127.0.0.1:5173';
const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport:{width:1440,height:900} });
const page = await ctx.newPage();
await page.goto(`${B}/`, {waitUntil:'networkidle'});
if (page.url().includes('/onboarding')) { await page.getByRole('button',{name:/Entrar|Operador/}).first().click(); await page.waitForTimeout(2000); }
for (const route of ['/board','/chat']) {
  await page.goto(`${B}${route}`, {waitUntil:'networkidle'});
  await page.waitForTimeout(1500);
  const txt = await page.locator('body').innerText();
  const idx = txt.toLowerCase().indexOf('migration');
  console.log(`\n=== ${route} (${page.url()})`);
  console.log(idx>=0 ? 'CONTEXTO: …'+txt.slice(Math.max(0,idx-160), idx+160).replace(/\n+/g,' ⏎ ')+'…' : 'sem ocorrência');
}
await browser.close();
