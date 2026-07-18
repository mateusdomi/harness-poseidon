import type { SolicitationAnalysis } from '@/api';

/** Estado de curadoria humana de um item da análise. */
export type CurationStatus = 'active' | 'resolved' | 'discarded';

export interface CuratedItem {
  id: string;
  text: string;
  status: CurationStatus;
}

export interface AnalysisPanel {
  key: 'requirements' | 'ambiguities' | 'contradictions' | 'questions' | 'acceptanceCriteria';
  items: CuratedItem[];
}

export const ANALYSIS_PANEL_KEYS = [
  'requirements',
  'ambiguities',
  'contradictions',
  'questions',
  'acceptanceCriteria',
] as const;

/** Converte o resultado do mock em painéis curáveis (todos ativos). */
export function toCuratedPanels(analysis: SolicitationAnalysis): AnalysisPanel[] {
  return ANALYSIS_PANEL_KEYS.map((key) => ({
    key,
    items: analysis[key].map((item) => ({ id: item.id, text: item.text, status: 'active' })),
  }));
}

/** Itens que entram na demanda: só os ativos (resolvidos/descartados ficam de fora). */
export function activeItems(panel: AnalysisPanel): CuratedItem[] {
  return panel.items.filter((item) => item.status === 'active');
}

/**
 * Monta a demanda estruturada a partir da curadoria: título = primeiro
 * requisito ativo (truncado) e descrição em markdown com uma seção por
 * painel (somente itens ativos; painéis vazios são omitidos).
 */
export function buildDemandFromPanels(input: {
  panels: AnalysisPanel[];
  panelTitles: Record<AnalysisPanel['key'], string>;
  fallbackTitle: string;
}): { title: string; description: string } {
  const requirements = activeItems(input.panels.find((p) => p.key === 'requirements')!);
  const first = requirements[0]?.text ?? input.fallbackTitle;
  const title = first.length > 80 ? `${first.slice(0, 77)}…` : first;

  const sections = input.panels
    .map((panel) => {
      const items = activeItems(panel);
      if (items.length === 0) return null;
      const list = items.map((item) => `- ${item.text}`).join('\n');
      return `## ${input.panelTitles[panel.key]}\n\n${list}`;
    })
    .filter((section): section is string => section !== null);

  return { title, description: sections.join('\n\n') };
}
