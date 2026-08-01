import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';

import { navTo } from './journeys';

/**
 * Gate visual do controle de seleção compartilhado (Checkbox do design
 * system). Reproduz o defeito P2 de homologação: no estado marcado o
 * checkmark precisa ficar VISÍVEL, não apenas `checked=true` no DOM.
 *
 * O check e o traço (indeterminate) são SVGs reais sobrepostos, revelados
 * por opacidade (`peer-checked` / `peer-data-[indeterminate]`). Aqui
 * validamos a opacidade COMPUTADA no navegador real — a única prova de que
 * o indicador aparece — na tela Notificações (a reportada) nos 2 viewports
 * do projeto (mobile-360 e desktop-1440, ver playwright.config.ts).
 *
 * Navegação: no mock o perfil ativo é in-memory; um `goto` cheio o perde.
 * Por isso selecionamos o perfil e navegamos SEMPRE pelo shell (SPA).
 */


/** Onboarding pela UI → perfil seed "Mateus" → Notificações (via SPA). */
async function gotoNotifications(page: Page) {
  await page.goto('/');
  await page.getByRole('button', { name: /Mateus/ }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
  await navTo(page, 'Notificações');
  await expect(page).toHaveURL(/\/notifications$/);
}

/** Localiza o check (lucide) sobreposto ao input nativo. */
function checkIconFor(checkbox: Locator) {
  return checkbox.locator('xpath=..').locator('.lucide-check');
}

test.describe('Feedback visual do Checkbox compartilhado', () => {
  test('Notificações: o checkmark do estado marcado fica visível (opacidade 1)', async ({
    page,
  }) => {
    const consoleErrors: string[] = [];
    page.on('console', (m) => m.type() === 'error' && consoleErrors.push(m.text()));
    page.on('pageerror', (e) => consoleErrors.push(e.message));

    await gotoNotifications(page);

    // Toggle global: marcado por padrão no mock.
    const global = page.getByRole('checkbox').first();
    await expect(global).toHaveAccessibleName(/^Receber notificações/);
    await expect(global).toBeChecked();
    // Prova do defeito corrigido: o check está renderizado E revelado.
    await expect(checkIconFor(global)).toHaveCSS('opacity', '1');

    // Categoria "Sistema": ativa por padrão; desmarcar oculta o indicador.
    const system = page.getByRole('checkbox', {
      name: 'Receber notificações de Sistema',
      exact: true,
    });
    await expect(system).toBeChecked();
    await expect(checkIconFor(system)).toHaveCSS('opacity', '1');
    await system.click();
    await expect(system).not.toBeChecked();
    await expect(checkIconFor(system)).toHaveCSS('opacity', '0');

    // Sem violações críticas/sérias de acessibilidade na tela.
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();
    const blocking = results.violations.filter(
      (v) => v.impact === 'critical' || v.impact === 'serious',
    );
    expect(blocking, blocking.map((v) => `${v.id}: ${v.nodes.length}`).join(', ')).toEqual([]);

    expect(consoleErrors, consoleErrors.join('\n')).toEqual([]);
  });

  test('Teclado: Space marca o controle e revela o checkmark', async ({ page }) => {
    await gotoNotifications(page);

    const system = page.getByRole('checkbox', {
      name: 'Receber notificações de Sistema',
      exact: true,
    });
    await expect(system).toBeChecked();
    await system.focus();
    await page.keyboard.press(' ');
    await expect(system).not.toBeChecked();
    await expect(checkIconFor(system)).toHaveCSS('opacity', '0');
    await system.focus();
    await page.keyboard.press(' ');
    await expect(system).toBeChecked();
    await expect(checkIconFor(system)).toHaveCSS('opacity', '1');
  });
});
