import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

import { buildFixtures } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import { MarkdownContent } from '@/features/chat/components/markdown-content';
import {
  deriveQuickActions,
  extractReferences,
  IDLE_TURN,
  reduceChatTurn,
} from '@/features/chat/lib/chat-derive';
import ChatPage from '@/features/chat/pages/chat-page';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = buildFixtures(42);
const project = fixtures.data.projects[0];
const tasks = fixtures.data.tasks.filter((t) => t.projectId === project.id);
const documents = fixtures.data.documents.filter((d) => d.projectId === project.id);

function envelope(type: string, payload: unknown, sequence = 1) {
  return { stream: 'conversation:x', sequence, type, occurredAt: '2026-07-17T12:00:00Z', payload } as never;
}

describe('chat-derive', () => {
  it('acumula chunks do turno e reseta ao concluir', () => {
    let turn = reduceChatTurn(
      IDLE_TURN,
      envelope('chat.turnStarted', { conversationId: 'c', turnId: 't1', agentId: 'a' }),
    );
    expect(turn.turnId).toBe('t1');

    turn = reduceChatTurn(
      turn,
      envelope('chat.turnChunk', { conversationId: 'c', turnId: 't1', index: 0, text: 'Olá, ' }, 2),
    );
    turn = reduceChatTurn(
      turn,
      envelope('chat.turnChunk', { conversationId: 'c', turnId: 't1', index: 1, text: 'mundo' }, 3),
    );
    expect(turn.text).toBe('Olá, mundo');

    // Chunk de outro turno é ignorado.
    const other = reduceChatTurn(
      turn,
      envelope('chat.turnChunk', { conversationId: 'c', turnId: 't2', index: 0, text: '???' }, 4),
    );
    expect(other.text).toBe('Olá, mundo');

    turn = reduceChatTurn(
      turn,
      envelope('chat.turnCompleted', { conversationId: 'c', turnId: 't1', messageId: 'm', finishReason: 'stop' }, 5),
    );
    expect(turn).toEqual(IDLE_TURN);
  });

  it('extrai referências cruzadas de títulos citados na mensagem', () => {
    const references = extractReferences(
      'Iniciando a tarefa "Exportação CSV do quadro" e atualizando a "Spec da API v1".',
      tasks,
      documents,
    );
    expect(references).toContainEqual(
      expect.objectContaining({ kind: 'task', title: 'Exportação CSV do quadro' }),
    );
    expect(references).toContainEqual(
      expect.objectContaining({ kind: 'document', title: 'Spec da API v1' }),
    );
    expect(extractReferences('Sem citações aqui.', tasks, documents)).toEqual([]);
  });

  it('deriva ações rápidas do contexto do projeto', () => {
    expect(deriveQuickActions({ blockedTasks: 0, pendingApprovals: 0 })).toEqual([
      'summarizeProgress',
      'planNewDemand',
    ]);
    expect(deriveQuickActions({ blockedTasks: 2, pendingApprovals: 1 })).toEqual([
      'summarizeProgress',
      'blockedStatus',
      'approvalStatus',
      'planNewDemand',
    ]);
  });
});

describe('MarkdownContent', () => {
  it('renderiza bloco de código e código inline', () => {
    const { container } = render(
      <MarkdownContent content={'Use `npm test`:\n\n```ts\nconst x = 1;\n```'} />,
    );
    expect(container.querySelector('pre code')).not.toBeNull();
    expect(container.querySelector('pre code')?.textContent).toContain('const x = 1;');
    const inline = container.querySelector('p code');
    expect(inline?.textContent).toBe('npm test');
  });
});

describe('ChatPage', () => {
  function renderChat() {
    const bundle = createTestBundle({ chatChunkDelayMs: 5 });
    const utils = renderWithApi(
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>,
      bundle,
    );
    return utils;
  }

  it('renderiza mensagens da conversa e o seletor de conversa', async () => {
    const user = userEvent.setup();
    renderChat();

    // A conversa padrão é a mais recente; troca para a da sprint.
    const selector = await screen.findByLabelText('Conversa');
    await user.selectOptions(selector, screen.getByRole('option', { name: 'Planejamento da sprint 12' }));

    expect(
      await screen.findByText('Chefe, preciso exportar o quadro em CSV até sexta.'),
    ).toBeInTheDocument();
    // Chip de referência cruzada para a tarefa citada pelo agente.
    expect(
      screen.getByRole('link', { name: /exportação csv do quadro/i }),
    ).toHaveAttribute('href', expect.stringContaining('/board?task='));
  });

  it('envia mensagem e renderiza os chunks do turno do chefe', async () => {
    const user = userEvent.setup();
    renderChat();

    const input = await screen.findByLabelText(/mensagem para o chefe/i);
    await user.type(input, 'Como está o gate de qualidade?');
    await user.click(screen.getByRole('button', { name: /enviar mensagem/i }));

    // Mensagem do usuário entra na lista.
    expect(await screen.findByText('Como está o gate de qualidade?')).toBeInTheDocument();

    // Resposta do chefe completa após o streaming por chunks — a asserção
    // fica no waitFor para não capturar a bolha parcial do streaming.
    await waitFor(
      () => {
        expect(
          screen.getByText(/Entendi o contexto\. Vou quebrar isso em tarefas/),
        ).toBeInTheDocument();
        // Turno encerrado: indicador some.
        expect(screen.queryByText(/chefe está coordenando/i)).not.toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });

  it('mostra ações rápidas que enviam mensagem estruturada', async () => {
    const user = userEvent.setup();
    renderChat();

    const action = await screen.findByRole('button', { name: 'Ver bloqueios' });
    await user.click(action);

    expect(
      await screen.findByText(/quais tarefas estão bloqueadas/i, { selector: 'article p, article div' }),
    ).toBeInTheDocument();
  });
});
