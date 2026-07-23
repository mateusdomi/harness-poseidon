import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import { PO_ANALYSIS_QUESTION_DEADLINE } from '@/api';
import {
  activeItems,
  buildDemandFromPanels,
  toCuratedPanels,
  type AnalysisPanel,
} from '@/features/po-assistant/lib/po-assistant-derive';
import PoAssistantPage from '@/features/po-assistant/pages/po-assistant-page';
import { renderWithApi } from '@/test/render-with-providers';

const PANEL_TITLES: Record<AnalysisPanel['key'], string> = {
  requirements: 'Requisitos extraídos',
  ambiguities: 'Ambiguidades',
  contradictions: 'Contradições',
  questions: 'Perguntas geradas',
  acceptanceCriteria: 'Critérios de aceite',
};

function panel(
  key: AnalysisPanel['key'],
  items: { text: string; status?: 'active' | 'resolved' | 'discarded' }[],
): AnalysisPanel {
  return {
    key,
    items: items.map((item, index) => ({
      id: `${key}-${index}`,
      text: item.text,
      status: item.status ?? 'active',
    })),
  };
}

describe('po-assistant-derive', () => {
  it('toCuratedPanels cobre os 5 painéis com itens ativos', () => {
    const panels = toCuratedPanels({
      solicitationId: 'sol-1',
      requirements: [{ id: 'r1', text: 'Exportar CSV' }],
      ambiguities: [],
      contradictions: [],
      questions: [{ id: 'q1', text: 'Prazo?' }],
      acceptanceCriteria: [],
    });
    expect(panels).toHaveLength(5);
    expect(panels[0].items[0]).toEqual({ id: 'r1', text: 'Exportar CSV', status: 'active' });
  });

  it('demanda usa só itens ativos e omite painéis vazios', () => {
    const panels = [
      panel('requirements', [
        { text: 'Exportar o quadro em CSV até sexta' },
        { text: 'Item resolvido', status: 'resolved' },
      ]),
      panel('questions', [{ text: 'Qual o prazo?' }]),
      panel('contradictions', []),
      panel('ambiguities', []),
      panel('acceptanceCriteria', [{ text: 'Validar com o solicitante' }]),
    ];
    const { title, description } = buildDemandFromPanels({
      panels,
      panelTitles: PANEL_TITLES,
      fallbackTitle: 'Fallback',
    });
    expect(title).toBe('Exportar o quadro em CSV até sexta');
    expect(description).toContain('## Requisitos extraídos');
    expect(description).toContain('- Exportar o quadro em CSV até sexta');
    expect(description).not.toContain('Item resolvido');
    expect(description).not.toContain('## Contradições');
    expect(description).toContain('## Perguntas geradas');
    expect(activeItems(panels[0])).toHaveLength(1);
  });

  it('sem requisitos ativos, usa o título de fallback', () => {
    const panels = [panel('requirements', [{ text: 'Descartado', status: 'discarded' }])];
    const { title } = buildDemandFromPanels({
      panels: [
        ...panels,
        panel('ambiguities', []),
        panel('contradictions', []),
        panel('questions', []),
        panel('acceptanceCriteria', []),
      ],
      panelTitles: PANEL_TITLES,
      fallbackTitle: 'Demanda estruturada pelo assistente de PO',
    });
    expect(title).toBe('Demanda estruturada pelo assistente de PO');
  });
});

describe('PoAssistantPage', () => {
  it('analisa texto, mostra painéis e cria demanda estruturada', async () => {
    const user = userEvent.setup();
    const { bundle } = renderWithApi(
      <MemoryRouter initialEntries={['/po-assistant']}>
        <Routes>
          <Route path="/po-assistant" element={<PoAssistantPage />} />
          <Route path="/board" element={<p>quadro</p>} />
        </Routes>
      </MemoryRouter>,
      createTestBundle(),
    );

    await user.type(
      await screen.findByLabelText('Pedido em texto livre'),
      'Precisamos exportar o quadro em CSV. O time comercial vai usar no Excel.',
    );
    await user.click(screen.getByRole('button', { name: 'Analisar pedido' }));

    // Painéis com o conteúdo determinístico do mock.
    expect(await screen.findByText('Requisitos extraídos')).toBeInTheDocument();
    expect(screen.getByText(PO_ANALYSIS_QUESTION_DEADLINE)).toBeInTheDocument();
    expect(screen.getByText('Nenhuma contradição encontrada.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Criar demanda estruturada' }));

    expect(await screen.findByText(/Demanda estruturada criada/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ver no quadro' })).toBeInTheDocument();

    // A demanda existe no mock, vinculada à solicitação da análise.
    const demands = (await bundle.api.list('demands')).items;
    const created = demands.find((d) => d.title.includes('exportar o quadro em CSV'));
    expect(created).toBeDefined();
    expect(created!.solicitationId).not.toBeNull();
  });
});
