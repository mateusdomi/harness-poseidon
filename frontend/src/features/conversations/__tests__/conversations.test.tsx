import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  EMPTY_CONVERSATION_FILTERS,
  filterConversations,
  periodStart,
  sortByRecentActivity,
} from '@/features/conversations/lib/conversations-derive';
import ConversationsPage from '@/features/conversations/pages/conversations-page';
import { renderWithApi } from '@/test/render-with-providers';

const NOW = new Date('2026-07-17T12:00:00Z');
const bundle = createTestBundle();
const fixtures = bundle.fixtures.data;
const conversaAtiva = fixtures.conversations.find((c) => c.state === 'active')!;
const conversaArquivada = fixtures.conversations.find((c) => c.state === 'archived')!;

function renderPage() {
  const testBundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/conversations']}>
      <Routes>
        <Route path="/conversations" element={<ConversationsPage />} />
        <Route path="/chat" element={<p>chat destino</p>} />
      </Routes>
    </MemoryRouter>,
    testBundle,
  );
}

describe('conversations-derive', () => {
  it('lista padrão mostra só ativas; showArchived mostra só arquivadas', () => {
    const ativas = filterConversations(fixtures.conversations, EMPTY_CONVERSATION_FILTERS, NOW);
    expect(ativas.every((c) => c.state === 'active')).toBe(true);

    const arquivadas = filterConversations(
      fixtures.conversations,
      { ...EMPTY_CONVERSATION_FILTERS, showArchived: true },
      NOW,
    );
    expect(arquivadas.map((c) => c.id)).toEqual([conversaArquivada.id]);
  });

  it('filtra por projeto, autor e busca textual (case-insensitive)', () => {
    const porProjeto = filterConversations(
      fixtures.conversations,
      { ...EMPTY_CONVERSATION_FILTERS, projectId: conversaArquivada.projectId, showArchived: true },
      NOW,
    );
    expect(porProjeto).toHaveLength(1);

    const porAutor = filterConversations(
      fixtures.conversations,
      { ...EMPTY_CONVERSATION_FILTERS, authorId: '01JAVAZADOQUENAODEAUTOR' },
      NOW,
    );
    expect(porAutor).toHaveLength(0);

    const busca = filterConversations(
      fixtures.conversations,
      { ...EMPTY_CONVERSATION_FILTERS, search: 'SPRINT' },
      NOW,
    );
    expect(busca.map((c) => c.id)).toEqual([conversaAtiva.id]);
  });

  it('períodos: hoje/7d/30d contam dias completos a partir de agora', () => {
    expect(periodStart('today', NOW)).toBe(new Date(2026, 6, 17).getTime());
    expect(periodStart('7d', NOW)).toBe(new Date(2026, 6, 11).getTime());
    expect(periodStart('30d', NOW)).toBe(new Date(2026, 5, 18).getTime());
    expect(periodStart('', NOW)).toBeNull();
  });

  it('intervalo personalizado é inclusivo nas duas pontas', () => {
    const referencia = conversaAtiva.lastMessageAt ?? conversaAtiva.createdAt;
    const dia = referencia.slice(0, 10);
    const resultado = filterConversations(
      fixtures.conversations,
      { ...EMPTY_CONVERSATION_FILTERS, period: 'custom', from: dia, to: dia },
      NOW,
    );
    expect(resultado.map((c) => c.id)).toContain(conversaAtiva.id);

    const fora = filterConversations(
      fixtures.conversations,
      { ...EMPTY_CONVERSATION_FILTERS, period: 'custom', from: '2020-01-01', to: '2020-01-02' },
      NOW,
    );
    expect(fora).toHaveLength(0);
  });

  it('ordena pela atividade mais recente', () => {
    const ordenadas = sortByRecentActivity(fixtures.conversations);
    for (let index = 1; index < ordenadas.length; index += 1) {
      const anterior = ordenadas[index - 1].lastMessageAt ?? ordenadas[index - 1].createdAt;
      const atual = ordenadas[index].lastMessageAt ?? ordenadas[index].createdAt;
      expect(Date.parse(anterior)).toBeGreaterThanOrEqual(Date.parse(atual));
    }
  });
});

describe('ConversationsPage', () => {
  it('lista conversas ativas e renomeia pelo dialog', async () => {
    const user = userEvent.setup();
    const { bundle: pageBundle } = renderPage();

    const titulo = await screen.findByText(conversaAtiva.title);
    expect(titulo).toBeInTheDocument();
    // Arquivadas ficam fora da lista padrão.
    expect(screen.queryByText(conversaArquivada.title)).not.toBeInTheDocument();

    const item = titulo.closest('li')!;
    await user.click(within(item).getByRole('button', { name: 'Renomear' }));
    const dialog = await screen.findByRole('dialog', { name: 'Renomear conversa' });
    const input = within(dialog).getByLabelText('Título da conversa');
    await user.clear(input);
    await user.type(input, 'Sprint 12 — replanejamento');
    await user.click(within(dialog).getByRole('button', { name: 'Salvar' }));

    await screen.findByText('Sprint 12 — replanejamento');
    const atualizada = await pageBundle.api.get('conversations', conversaAtiva.id);
    expect(atualizada.title).toBe('Sprint 12 — replanejamento');
  });

  it('arquiva pela lista e exibe no filtro de arquivadas', async () => {
    const user = userEvent.setup();
    renderPage();

    const titulo = await screen.findByText(conversaAtiva.title);
    await user.click(within(titulo.closest('li')!).getByRole('button', { name: 'Arquivar' }));

    await waitFor(() =>
      expect(screen.queryByText(conversaAtiva.title)).not.toBeInTheDocument(),
    );

    await user.click(screen.getByRole('checkbox', { name: 'Mostrar arquivadas' }));
    await screen.findByText(conversaAtiva.title);
    expect(screen.getByText(conversaArquivada.title)).toBeInTheDocument();
  });
});
