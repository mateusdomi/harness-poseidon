import type { ComponentState, Plugin, Tool } from '@/api';

/**
 * Derivações do catálogo de ferramentas/skills/plugins/MCP: alternância
 * de estado, origem de ferramentas e resolução de ferramentas providas.
 * Funções puras, testadas sem render — NENHUM dado é inventado além do
 * contrato (o que o contrato não liga, a UI reporta como indisponível).
 */

/** Ação de alternância oferecida para o estado atual. */
export type ToggleAction = 'enable' | 'disable';

/**
 * Ação do botão de alternância: `enabled` oferece desabilitar; `disabled`
 * e `error` oferecem habilitar (state é enum de 3 valores, a ação só
 * alterna entre enabled/disabled).
 */
export function toggleAction(state: ComponentState): ToggleAction {
  return state === 'enabled' ? 'disable' : 'enable';
}

/** Estado destino da alternância (nunca `error` — erro é do backend). */
export function nextComponentState(state: ComponentState): 'enabled' | 'disabled' {
  return toggleAction(state) === 'enable' ? 'enabled' : 'disabled';
}

/**
 * Origem de uma ferramenta, derivada apenas do contrato:
 * - `builtin` → interna;
 * - `plugin` → plugin cujo `providesToolIds` contém a ferramenta
 *   (`null` quando nenhum plugin a referencia);
 * - `mcp` → o contrato NÃO liga ferramenta ↔ servidor MCP, então só o
 *   tipo é conhecido (a UI indica o vínculo como indisponível).
 */
export type ToolOrigin =
  | { type: 'builtin' }
  | { type: 'mcp' }
  | { type: 'plugin'; plugin: Plugin | null };

export function toolOrigin(tool: Tool, plugins: Plugin[]): ToolOrigin {
  switch (tool.kind) {
    case 'builtin':
      return { type: 'builtin' };
    case 'mcp':
      return { type: 'mcp' };
    case 'plugin':
      return {
        type: 'plugin',
        plugin: plugins.find((plugin) => plugin.providesToolIds.includes(tool.id)) ?? null,
      };
  }
}

/** Nomes das ferramentas providas por um plugin (resolve `providesToolIds`). */
export function providedToolNames(plugin: Plugin, tools: Tool[]): string[] {
  const nameById = new Map(tools.map((tool) => [tool.id, tool.name]));
  return plugin.providesToolIds
    .map((toolId) => nameById.get(toolId))
    .filter((name): name is string => name !== undefined);
}

/** Ordenação por nome (locale pt-BR, cópia — não muta a entrada). */
export function sortByName<T extends { name: string }>(items: readonly T[]): T[] {
  return [...items].sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
}

/** Identificadores das abas do catálogo. */
export type CatalogTabId = 'tools' | 'skills' | 'plugins' | 'mcp';

/** id do elemento `role=tab` de uma aba (liga `aria-controls`/`aria-labelledby`). */
export function catalogTabId(id: CatalogTabId): string {
  return `tools-tab-${id}`;
}

/** id do elemento `role=tabpanel` de uma aba. */
export function catalogPanelId(id: CatalogTabId): string {
  return `tools-panel-${id}`;
}
