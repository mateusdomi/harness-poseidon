import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

import { buildFixtures, streams, type Message } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import { MarkdownContent } from '@/features/chat/components/markdown-content';
import { MessageBubble } from '@/features/chat/components/message-bubble';
import { publicLeadershipContent } from '@/features/chat/lib/public-leadership';
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

describe('MessageBubble', () => {
  it('humaniza referências históricas à liderança sem alterar o dado persistido', () => {
    const persisted = 'Olá! Chief operacional e pronto. O Chefe acompanhará o fluxo.';
    expect(publicLeadershipContent(persisted)).toBe(
      'Olá! Bruna Magalhães está pronta. Bruna Magalhães acompanhará o fluxo.',
    );
    expect(persisted).toContain('Chief');
  });

  it('copia o conteúdo e mostra feedback i18n', async () => {
    const writeText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });
    const message: Message = {
      id: 'm1',
      conversationId: 'c1',
      authorRole: 'chief',
      authorProfileId: null,
      authorAgentId: null,
      content: 'Resposta do chefe.',
      tokenCount: 10,
      createdAt: '2026-07-17T12:00:00Z',
    };

    render(
      <MemoryRouter>
        <MessageBubble message={message} authorName="Iara" tasks={[]} documents={[]} />
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Copiar mensagem' }));
    expect(writeText).toHaveBeenCalledWith('Resposta de Bruna Magalhães.');
    expect(await screen.findByText('Mensagem copiada')).toBeInTheDocument();
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

    const input = await screen.findByLabelText(/mensagem para bruna/i);
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
        expect(screen.queryByText(/bruna está coordenando/i)).not.toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });

  it('mantém o banner do handle bloqueado após o evento terminal do SignalR', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    const conversation = bundle.fixtures.data.conversations
      .filter((item) => item.projectId === project.id)
      .sort((a, b) => (b.lastMessageAt ?? b.createdAt).localeCompare(a.lastMessageAt ?? a.createdAt))[0];
    vi.spyOn(bundle.api, 'startChatTurn').mockResolvedValue({
      turnId: 'blocked-turn',
      conversationId: conversation.id,
      state: 'blocked',
      correlationId: 'blocked-correlation',
      readiness: { overallState: 'Unconfigured', executionState: 'Blocked' },
      blockers: [{ code: 'workflow.unbound', relatedIds: [] }],
      nextActions: [{ code: 'workflow.bind', route: '/workflows', resourceId: null }],
      links: {
        readiness: `/api/v1/projects/${project.id}/readiness`,
        conversation: `/api/v1/conversations/${conversation.id}`,
      },
    });
    renderWithApi(
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>,
      bundle,
    );

    const input = await screen.findByLabelText(/mensagem para bruna/i);
    await user.type(input, 'Execute sem configuração.');
    await user.click(screen.getByRole('button', { name: /enviar mensagem/i }));
    expect(await screen.findByText('Turno registrado, execução bloqueada')).toBeInTheDocument();

    act(() => {
      bundle.realtime.emit(streams.conversation(conversation.id), 'chief.turnStateChanged', {
        turnId: 'blocked-turn',
        conversationId: conversation.id,
        projectId: project.id,
        state: 'blocked',
      });
    });

    expect(screen.getByText('Turno registrado, execução bloqueada')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Vincular workflow' })).toHaveAttribute(
      'href',
      '/workflows',
    );
  });

  it('abre o perfil acessível de Bruna pela foto e fecha com Escape', async () => {
    const user = userEvent.setup();
    renderChat();

    const triggers = await screen.findAllByRole('button', {
      name: 'Abrir perfil de Bruna Magalhães',
    });
    await user.click(triggers[0]);

    const dialog = await screen.findByRole('dialog', { name: 'Perfil de Bruna Magalhães' });
    expect(dialog).toHaveTextContent('Diretora de Engenharia e Operações de IA');
    expect(within(dialog).getByAltText('Foto de Bruna Magalhães')).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Perfil de Bruna Magalhães' })).toBeNull();
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
