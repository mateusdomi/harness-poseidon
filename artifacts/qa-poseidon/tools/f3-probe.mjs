import { chromium } from '@playwright/test';
const B='http://127.0.0.1:5173';
const br = await chromium.launch(); const ctx = await br.newContext({viewport:{width:1440,height:900}});
const p = await ctx.newPage();
await p.goto(`${B}/`,{waitUntil:'networkidle'});
if (p.url().includes('/onboarding')) { await p.getByRole('button',{name:/Entrar|Operador/}).first().click(); await p.waitForTimeout(2500); }
await p.goto(`${B}/cockpit`,{waitUntil:'networkidle'}); await p.waitForTimeout(1500);
const t = await p.locator('body').innerText();
console.log('contém "Aprovaç":', /Aprovaç/i.test(t));
console.log('contém "Saúde da governança":', /Saúde da governança/i.test(t));
console.log('\nTÍTULOS DE CARD (h2/h3):');
console.log((await p.locator('h2,h3').allTextContents()).map(s=>'  - '+s.trim()).join('\n'));
await br.close();
