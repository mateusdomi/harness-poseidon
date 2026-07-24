import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * Gate de acessibilidade da FE-4: axe-core em TODAS as rotas das 21 features
 * (mais onboarding), nos 2 viewports do projeto (mobile-360/desktop-1440) e
 * nos 2 temas (dark padrão + light via localStorage persistido).
 *
 * Falha em qualquer violação `critical` ou `serious`. O onboarding é
 * completado pela UI (um clique) antes de visitar as rotas protegidas.
 *
 * Regra de desligamento pontual (ex.: contraste de cor de elemento de marca)
 * deve ser feita com `disableRules` explícito na rota afetada — nunca
 * abaixando o impacto globalmente.
 */

const ROUTES: Array<{ key: string; path: string }> = [
  { key: 'cockpit', path: '/cockpit' },
  { key: 'projects', path: '/projects' },
  { key: 'delivery', path: '/delivery' },
  { key: 'chat', path: '/chat' },
  { key: 'conversations', path: '/conversations' },
  { key: 'board', path: '/board' },
  { key: 'workflows', path: '/workflows' },
  { key: 'documents', path: '/documents' },
  { key: 'prototypes', path: '/prototypes' },
  { key: 'architecture', path: '/architecture' },
  { key: 'approvals', path: '/approvals' },
  { key: 'orchestrator', path: '/orchestrator' },
  { key: 'agents', path: '/agents' },
  { key: 'tools', path: '/tools' },
  { key: 'run-project', path: '/run-project' },
  { key: 'organizations', path: '/organizations' },
  { key: 'providers', path: '/providers' },
  { key: 'po-assistant', path: '/po-assistant' },
  { key: 'governance', path: '/governance' },
  { key: 'licenses', path: '/licenses' },
  { key: 'notifications', path: '/notifications' },
  { key: 'settings', path: '/settings' },
];

const THEME_STORAGE_KEY = 'poseidon-theme';

/** Garante perfil ativo: se cair no onboarding, escolhe o perfil seed "Mateus". */
async function ensureProfile(page: Page) {
  if (/\/onboarding$/.test(page.url())) {
    await page.getByRole('button', { name: /Mateus/ }).click();
    await expect(page).toHaveURL(/\/cockpit$/);
  }
}

/** Aplica tema light persistido antes do carregamento da página. */
async function useLightTheme(page: Page) {
  await page.addInitScript(
    (key) => {
      window.localStorage.setItem(key, JSON.stringify({ state: { preference: 'light' }, version: 0 }));
    },
    THEME_STORAGE_KEY,
  );
}

async function scan(page: Page, context: string) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  const blocking = results.violations.filter(
    (v) => v.impact === 'critical' || v.impact === 'serious',
  );
  expect(
    blocking,
    `${context}: ${blocking
      .map((v) => `${v.id} (${v.impact}) → ${v.nodes.length} nó(s): ${v.nodes
        .slice(0, 3)
        .map((n) => n.target.join(' '))
        .join(' | ')}`)
      .join('\n')}`,
  ).toEqual([]);
}

/** Gate transversal: nenhum erro da aplicação e nenhum asset estático quebrado. */
function watchRuntime(page: Page) {
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
  return (context: string) => expect(issues, `${context}: erros de runtime/assets`).toEqual([]);
}

test.describe('A11y (axe-core) — todas as rotas, 2 temas', () => {
  test('onboarding (fora do shell)', async ({ page }) => {
    const assertRuntime = watchRuntime(page);
    await page.goto('/onboarding');
    await expect(page.getByRole('button', { name: /Mateus/ })).toBeVisible();
    await scan(page, 'onboarding dark');
    await useLightTheme(page);
    await page.reload();
    await expect(page.getByRole('button', { name: /Mateus/ })).toBeVisible();
    await scan(page, 'onboarding light');
    assertRuntime('onboarding');
  });

  for (const route of ROUTES) {
    test(`${route.key} (${route.path})`, async ({ page }) => {
      const assertRuntime = watchRuntime(page);
      await page.goto('/');
      await ensureProfile(page);

      await page.goto(route.path);
      // Aguarda o conteúdo real substituir o skeleton de rota.
      await expect(page.getByRole('main')).toBeVisible();
      await page.waitForTimeout(500);
      await scan(page, `${route.key} dark`);

      await useLightTheme(page);
      await page.reload();
      await expect(page.getByRole('main')).toBeVisible();
      await page.waitForTimeout(500);
      await scan(page, `${route.key} light`);
      assertRuntime(route.key);
    });
  }
});
