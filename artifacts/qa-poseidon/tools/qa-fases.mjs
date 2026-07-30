/**
 * QA de requisitos de tela, fase a fase, contra o Host real.
 * Cada asserção nasce de um requisito literal da arquitetura v6.
 */
import { chromium } from '@playwright/test';

const B = process.env.BASE ?? 'http://127.0.0.1:5173';
const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();

const out = [];
const check = (fase, req, ok, detail = '') =>
  out.push({ fase, req, ok, detail: String(detail).slice(0, 120) });

await page.goto(`${B}/`, { waitUntil: 'networkidle' });
if (page.url().includes('/onboarding')) {
  await page.getByRole('button', { name: /Entrar|Operador/ }).first().click();
  await page.waitForTimeout(2500);
}

// ───────── F4: shell de navegação e seleção global de projeto
await page.goto(`${B}/cockpit`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const navItems = await page.getByRole('navigation').getByRole('link').allTextContents();
check('F4', 'Chat é o primeiro item do menu', /chat/i.test(navItems[0] ?? ''), `1º = "${navItems[0]}"`);
check('F4', 'Onboarding fora do menu', !navItems.some((t) => /onboarding|primeiro acesso/i.test(t)), navItems.length + ' itens');
const menuLex = navItems.filter((t) => /orquestrador|fleet|frota|agentes|cards?/i.test(t));
check('F4', 'Menu sem termo proibido (modo Negócio)', menuLex.length === 0, menuLex.join(','));
const projSelector = await page.getByLabel(/Projeto ativo|Projeto/i).count();
check('F4', 'Seletor global de projeto no cabeçalho', projSelector > 0, `${projSelector} controle(s)`);

// ───────── F3: dashboard de negócio
const bodyCockpit = await page.locator('body').innerText();
check('F3', 'Quadro da Equipe presente', /Quadro da Equipe|Equipe/i.test(bodyCockpit));
check('F3', 'Linha do tempo de etapas', /etapa/i.test(bodyCockpit));
check('F3', 'Capacidade da equipe (ex-Cotas críticas)', /Capacidade/i.test(bodyCockpit) && !/cotas? crítica/i.test(bodyCockpit));
check('F3', 'Aprovações pendentes (ex-Saúde da governança)', /Aprovaç/i.test(bodyCockpit) && !/Saúde da governança/i.test(bodyCockpit));
check('F3', 'Sem "Produtividade por assinatura"', !/assinatura/i.test(bodyCockpit));

// ───────── F7: quadro e conversas
await page.goto(`${B}/board`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const boardText = await page.locator('body').innerText();
check('F7', 'Quadro sem "tarefa de agente"', !/tarefa de agente/i.test(boardText));
const filterCount = await page.getByRole('combobox').count();
check('F7', 'Filtros do Quadro reduzidos (≤6 combos)', filterCount <= 6, `${filterCount} combos`);

await page.goto(`${B}/conversations`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const convSearch = await page.getByRole('searchbox').count() + await page.getByPlaceholder(/buscar|pesquisar/i).count();
check('F7', 'Conversas com busca', convSearch > 0, `${convSearch} campo(s)`);

// ───────── F8: equipe + executar projeto
await page.goto(`${B}/orchestrator`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const orchText = await page.locator('body').innerText();
check('F8', 'Identidade fixa da Bruna', /Bruna Magalh/i.test(orchText), (orchText.match(/Bruna[^\n]{0,60}/) ?? [''])[0]);
check('F8', 'Sem heartbeat/lease/diagnóstico no Negócio', !/heartbeat|lease|fencing|diagnóstic/i.test(orchText));
check('F8', 'Ações renomeadas (Pausar trabalhos / Transferir liderança)', /Pausar trabalhos|Transferir liderança/i.test(orchText));

await page.goto(`${B}/run-project`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const runText = await page.locator('body').innerText();
check('F8', 'Executar Projeto: botão "Abrir <projeto>"', /Abrir /i.test(runText), (runText.match(/Abrir [^\n]{0,40}/) ?? [''])[0]);
check('F8', 'Sem lista técnica de serviços no Negócio', !/vite|npm|dotnet|porta \d{4}/i.test(runText));

// ───────── F9: documentos unificados
await page.goto(`${B}/documents`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const docText = await page.locator('body').innerText();
check('F9', 'Aba "Aguardando sua aprovação"', /Aguardando sua aprovação|aprovação/i.test(docText));
await page.goto(`${B}/approvals`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);
check('F9', 'Aprovações redireciona para Documentos', page.url().includes('/documents'), page.url());

// ───────── F5: central de entregas
await page.goto(`${B}/delivery`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1200);
const delText = await page.locator('body').innerText();
check('F5', 'Rastreamento: início e prazo', /Começou em|Prazo|sem prazo/i.test(delText));
check('F5', 'Botão de baixar documentos (ZIP)', /baixar|download/i.test(delText));

// ───────── F2: chat
await page.goto(`${B}/chat`, { waitUntil: 'networkidle' });
await page.waitForTimeout(2000);
const chatText = await page.locator('body').innerText();
check('F2', 'Sem "Equipe virtual" / "Chief"', !/Equipe virtual|Chief\b/i.test(chatText));
check('F2', 'Seletor "Modo de trabalho" (não "Modelo")', await page.getByRole('combobox', { name: /Modo de trabalho/i }).count() > 0
  || /Modo de trabalho/i.test(chatText), 'combobox');
check('F2', 'Sem seletor de Modelo no Negócio', await page.getByRole('combobox', { name: /^Modelo$/i }).count() === 0);

console.log('\n╔════════ QA DE REQUISITOS POR FASE — HOST REAL');
let fail = 0;
for (const r of out) {
  if (!r.ok) fail++;
  console.log(`${r.ok ? '✓' : '✘'} [${r.fase}] ${r.req}${r.detail ? '  — ' + r.detail : ''}`);
}
console.log(`\n${out.length - fail}/${out.length} asserções passaram; ${fail} falharam.`);
await browser.close();
