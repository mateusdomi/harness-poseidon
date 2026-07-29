import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { streams } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import BoardPage from '@/features/board/pages/board-page';
import {
  assigneeAgents,
  groupTasksByState,
  parseTaskStateParam,
  shortTaskId,
} from '@/features/board/lib/board-derive';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';

const fixtures = createTestBundle().fixtures.data;
const { tasks, projects, agents } = fixtures;
const firstProject = projects[0];
const projectTasks = tasks.filter((task) => task.projectId === firstProject.id);

describe('board-derive', () => {
  it('agrupa tarefas nas 8 colunas, sempre presentes, por última atividade', () => {
    const grouped = groupTasksByState(projectTasks);
    expect(Object.keys(grouped)).toHaveLength(8);
    expect(grouped.backlog.length).toBeGreaterThan(0);
    expect(grouped.blocked).toHaveLength(3);
    for (const column of Object.values(grouped)) {
      const sorted = [...column].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
      expect(column).toEqual(sorted);
    }
  });

  it('valida o query param ?state= contra o enum de colunas', () => {
    expect(parseTaskStateParam('blocked')).toBe('blocked');
    expect(parseTaskStateParam('testsGates')).toBe('testsGates');
    expect(parseTaskStateParam('invalid')).toBeNull();
    expect(parseTaskStateParam(null)).toBeNull();
  });

  it('shortTaskId: sufixo de 6 chars em caixa alta, contido no ID completo', () => {
    const id = tasks[0].id;
    const short = shortTaskId(id);
    expect(short).toHaveLength(6);
    expect(short).toBe(short.toUpperCase());
    // O que o usuário vê no card é sufixo do ID → a busca por ID casa.
    expect(id.toUpperCase().endsWith(short)).toBe(true);
    expect(shortTaskId('01hzzzzzzzabcdef')).toBe('ABCDEF');
  });

  it('assigneeAgents lista só responsáveis reais, ordenados e sem a liderança', () => {
    const result = assigneeAgents(tasks, agents);
    const assignedIds = new Set(
      tasks.map((task) => task.assigneeAgentId).filter((id): id is string => id !== null),
    );
    expect(result.length).toBeGreaterThan(0);
    // Todo item filtra de verdade: é responsável real de ao menos um card.
    expect(result.every((agent) => assignedIds.has(agent.id))).toBe(true);
    // Sem opções mortas: a liderança orquestra, não recebe cards.
    expect(result.some((agent) => agent.name.startsWith('Bruna Magalhães'))).toBe(false);
    // Nomes legíveis, ordenados alfabeticamente.
    const names = result.map((agent) => agent.name);
    expect(names).toEqual([...names].sort((a, b) => a.localeCompare(b)));
    // Cada responsável real aparece no máximo uma vez.
    expect(new Set(result.map((agent) => agent.id)).size).toBe(result.length);
  });
});

function renderBoard(
  initialEntry = '/board',
  presentationMode: 'business' | 'technical' = 'business',
) {
  // Bundle novo por teste: o store do mock é mutável (aprovações, prioridade).
  const bundle = createTestBundle();
  usePresentationStore.setState({ modeByProfile: {} });
  if (presentationMode === 'technical') {
    usePresentationStore.getState().requestMode(bundle.fixtures.meta.currentProfileId, 'technical');
  }
  return renderWithApi(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/board" element={<BoardPage />} />
        <Route path="/chat" element={<p>CHAT</p>} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

function stubMatchMedia(matches: boolean) {
  window.matchMedia = ((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

afterEach(() => {
  // jsdom não tem matchMedia: remove o stub entre testes.
  delete (window as { matchMedia?: unknown }).matchMedia;
});

describe('BoardPage', () => {
  it('renderiza as 8 colunas como regiões com heading e os cards na coluna certa', async () => {
    renderBoard();

    for (const name of [
      'Planejado',
      'Pronto para começar',
      'Em andamento',
      'Em revisão',
      'Em correção',
      'Em validação',
      'Precisa de atenção',
      'Concluído',
    ]) {
      expect(await screen.findByRole('region', { name: new RegExp(name) })).toBeInTheDocument();
    }

    const backlogColumn = screen.getByRole('region', { name: /Planejado/ });
    expect(
      within(backlogColumn).getByRole('button', { name: /Mapear endpoints de billing/ }),
    ).toBeInTheDocument();

    const blockedColumn = screen.getByRole('region', { name: /Precisa de atenção/ });
    expect(
      within(blockedColumn).getByRole('button', { name: /Deploy em staging/ }),
    ).toBeInTheDocument();
    expect(within(blockedColumn).getAllByText(/Impedimento:/).length).toBeGreaterThan(0);

    // Não existe botão "nova tarefa" — humano não cria tarefa técnica.
    expect(screen.queryByRole('button', { name: /nova tarefa/i })).not.toBeInTheDocument();
    expect(screen.getByText(/quadro organiza o trabalho do projeto por etapa/i)).toBeInTheDocument();
    expect(screen.queryByText(/cadeia solicitação → demanda → tarefa/i)).not.toBeInTheDocument();
  });

  it('oculta ID e filtros internos no modo de negócio', async () => {
    renderBoard();

    const task = projectTasks.find((entry) => entry.title === 'Mapear endpoints de billing')!;
    const plannedColumn = await screen.findByRole('region', { name: /Planejado/ });
    expect(within(plannedColumn).queryByText(`#${shortTaskId(task.id)}`)).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Buscar')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Responsável')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Tipo')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Prioridade')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Coluna')).toBeInTheDocument();
    expect(screen.getByLabelText('Fase')).toBeInTheDocument();
    expect(screen.getByLabelText('Última atividade')).toBeInTheDocument();
    expect(screen.getByLabelText('Arquivamento')).toBeInTheDocument();
    expect(screen.queryByText('Tarefa de agente')).not.toBeInTheDocument();
  });

  it('preserva ID e filtros avançados no modo técnico autorizado', async () => {
    const user = userEvent.setup();
    renderBoard('/board', 'technical');

    const task = projectTasks.find((entry) => entry.title === 'Mapear endpoints de billing')!;
    const backlogColumn = await screen.findByRole('region', { name: /Backlog/ });
    // O sufixo do ULID aparece no card, com o ID completo no tooltip nativo.
    const idChip = within(backlogColumn).getByText(`#${shortTaskId(task.id)}`);
    expect(idChip).toHaveAttribute('title', `ID da tarefa: ${task.id}`);

    // Buscar pelo sufixo visível encontra a tarefa (busca por ID casa).
    await user.type(await screen.findByLabelText('Buscar'), shortTaskId(task.id));
    expect(
      await within(backlogColumn).findByRole('button', { name: /Mapear endpoints de billing/ }),
    ).toBeInTheDocument();
  });

  it('expõe o ID completo copiável no detalhe da tarefa', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const task = projectTasks.find((entry) => entry.title === 'Mapear endpoints de billing')!;
    renderBoard(`/board?task=${task.id}`, 'technical');

    expect(await screen.findByText(task.id)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Copiar ID' }));
    expect(writeText).toHaveBeenCalledWith(task.id);
    expect(await screen.findByRole('button', { name: 'ID copiado' })).toBeInTheDocument();
  });

  it('o filtro "Responsável" abre em "Todos os responsáveis" e lista responsáveis reais', async () => {
    renderBoard('/board', 'technical');

    const assigneeSelect = await screen.findByLabelText('Responsável');
    // Opção neutra clara (não mais um "Todas" ambíguo).
    expect(
      within(assigneeSelect).getByRole('option', { name: 'Todos os responsáveis' }),
    ).toBeInTheDocument();
    // Sem opções mortas: nenhum chefe entra na lista de responsáveis.
    expect(
      within(assigneeSelect).queryByRole('option', { name: /^Chefe/ }),
    ).not.toBeInTheDocument();
    // Só entra quem tem card: cada opção corresponde a um assignee real.
    const assignedIds = new Set(
      projectTasks.map((entry) => entry.assigneeAgentId).filter((id): id is string => id !== null),
    );
    const options = within(assigneeSelect)
      .getAllByRole('option')
      .map((option) => (option as HTMLOptionElement).value)
      .filter((value) => value !== '');
    expect(options.length).toBeGreaterThan(0);
    expect(options.every((value) => assignedIds.has(value))).toBe(true);
  });

  it('move o card de coluna ao receber task.stateChanged no stream do projeto', async () => {
    const { bundle } = renderBoard();

    const backlogColumn = await screen.findByRole('region', { name: /Planejado/ });
    const cardName = /Mapear endpoints de billing/;
    expect(within(backlogColumn).getByRole('button', { name: cardName })).toBeInTheDocument();

    const task = projectTasks.find((entry) => entry.title === 'Mapear endpoints de billing')!;
    act(() => {
      bundle.realtime.emit(streams.project(firstProject.id), 'task.stateChanged', {
        taskId: task.id,
        from: 'backlog',
        to: 'ready',
        changedByKind: 'chief',
        note: null,
      });
    });

    const readyColumn = screen.getByRole('region', { name: /Pronto para começar/ });
    expect(await within(readyColumn).findByRole('button', { name: cardName })).toBeInTheDocument();
    expect(within(backlogColumn).queryByRole('button', { name: cardName })).not.toBeInTheDocument();
  });

  it('destaca a coluna filtrada por ?state= (link do cockpit)', async () => {
    renderBoard('/board?state=blocked');

    const blockedColumn = await screen.findByRole('region', { name: /Precisa de atenção/ });
    expect(blockedColumn).toHaveAttribute('data-highlighted', 'true');
    const backlogColumn = screen.getByRole('region', { name: /Planejado/ });
    expect(backlogColumn).not.toHaveAttribute('data-highlighted');
  });

  it('abre o detalhe como página dedicada no mobile e volta ao quadro', async () => {
    const user = userEvent.setup();
    const task = projectTasks.find((entry) => entry.title === 'Mapear endpoints de billing')!;
    renderBoard();

    const backlogColumn = await screen.findByRole('region', { name: /Planejado/ });
    await user.click(
      within(backlogColumn).getByRole('button', { name: /Mapear endpoints de billing/ }),
    );

    // Detalhe substitui o quadro e prioriza a leitura de negócio.
    expect(await screen.findByText('Objetivo')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Mapear endpoints de billing' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Trabalho realizado' })).toBeInTheDocument();
    expect(screen.queryByText('Instrução enviada ao agente')).not.toBeInTheDocument();
    expect(screen.queryByText('Claims e escopo autorizado')).not.toBeInTheDocument();
    expect(screen.queryByText(task.id)).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: /Planejado/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Voltar ao quadro/ }));
    expect(await screen.findByRole('region', { name: /Planejado/ })).toBeInTheDocument();
  });

  it('abre o detalhe como drawer no desktop (lg+) e fecha com Esc', async () => {
    stubMatchMedia(true);
    const user = userEvent.setup();
    renderBoard('/board?task=' + projectTasks[0].id);

    const dialog = await screen.findByRole('dialog', { name: 'Detalhes da tarefa' });
    expect(dialog).toBeInTheDocument();
    expect(dialog).toHaveClass('top-16', 'bottom-0', 'overflow-y-auto');
    expect(await within(dialog).findByRole('heading', { name: projectTasks[0].title })).toHaveClass(
      'break-words',
    );
    expect(within(dialog).getAllByText(/Planejado|Pronto para começar|Em andamento/).length).toBeGreaterThan(
      0,
    );
    expect(within(dialog).queryByText(projectTasks[0].id)).not.toBeInTheDocument();
    // O quadro continua visível atrás do drawer.
    expect(await screen.findByRole('region', { name: /Planejado/ })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('arrasta o fundo do quadro para navegar horizontalmente sem capturar cards', async () => {
    const originalPointerEvent = window.PointerEvent;
    class TestPointerEvent extends MouseEvent {
      readonly pointerId: number;

      constructor(type: string, init: PointerEventInit = {}) {
        super(type, init);
        this.pointerId = init.pointerId ?? 0;
      }
    }
    Object.defineProperty(window, 'PointerEvent', {
      value: TestPointerEvent,
      configurable: true,
    });
    renderBoard();
    const board = await screen.findByRole('region', { name: 'Quadro Kanban horizontal' });
    Object.defineProperties(board, {
      setPointerCapture: { value: vi.fn(), configurable: true },
      hasPointerCapture: { value: vi.fn(() => true), configurable: true },
      releasePointerCapture: { value: vi.fn(), configurable: true },
    });
    board.scrollLeft = 120;

    fireEvent.pointerDown(board, { button: 0, pointerId: 7, clientX: 300 });
    fireEvent.pointerMove(board, { pointerId: 7, clientX: 220 });
    expect(board.scrollLeft).toBe(200);
    fireEvent.pointerUp(board, { pointerId: 7, clientX: 220 });
    expect(board).not.toHaveAttribute('data-panning');

    const card = within(screen.getByRole('region', { name: /Planejado/ })).getByRole('button', {
      name: /Mapear endpoints de billing/,
    });
    fireEvent.pointerDown(card, { button: 0, pointerId: 8, clientX: 300 });
    fireEvent.pointerMove(board, { pointerId: 8, clientX: 100 });
    expect(board.scrollLeft).toBe(200);
    Object.defineProperty(window, 'PointerEvent', {
      value: originalPointerEvent,
      configurable: true,
    });
  });

  it('reprovação de gate exige observação; com observação, resolve', async () => {
    const user = userEvent.setup();
    const task = projectTasks.find((entry) => entry.title === 'Suíte E2E do fluxo de aprovação')!;
    renderBoard(`/board?task=${task.id}`);

    // Aprovação pendente ligada à tarefa (fixture).
    expect(await screen.findByText('Aprovar publicação da suíte E2E')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reprovar' }));
    await user.click(screen.getByRole('button', { name: 'Confirmar reprovação' }));
    expect(
      await screen.findByText('A observação é obrigatória para reprovar.'),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText(/Observação/), 'Cobertura insuficiente no fluxo feliz.');
    await user.click(screen.getByRole('button', { name: 'Confirmar reprovação' }));
    expect(await screen.findByText('Reprovada')).toBeInTheDocument();
  });

  it('aprova o gate pendente pelo detalhe da tarefa', async () => {
    const user = userEvent.setup();
    const task = projectTasks.find((entry) => entry.title === 'Suíte E2E do fluxo de aprovação')!;
    renderBoard(`/board?task=${task.id}`);

    expect(await screen.findByText('Aprovar publicação da suíte E2E')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Aprovar' }));
    expect(await screen.findByText('Aprovada')).toBeInTheDocument();
  });

  it('altera a prioridade da tarefa pelo detalhe', async () => {
    const user = userEvent.setup();
    const task = projectTasks.find((entry) => entry.title === 'Mapear endpoints de billing')!;
    renderBoard(`/board?task=${task.id}`);

    const prioritySelect = await screen.findByLabelText('Alterar prioridade');
    await user.selectOptions(prioritySelect, 'critical');
    await waitFor(() => expect(prioritySelect).toHaveValue('critical'));
  });

  it('filtra por busca (título) com contagem de resultados e limpa os filtros', async () => {
    const user = userEvent.setup();
    renderBoard('/board', 'technical');

    const search = await screen.findByLabelText('Buscar');
    expect(
      screen.getByText(`${projectTasks.length - 1} de ${projectTasks.length} tarefas`),
    ).toBeInTheDocument();

    await user.type(search, 'Mapear endpoints');
    expect(await screen.findByText(`1 de ${projectTasks.length} tarefas`)).toBeInTheDocument();
    const backlogColumn = screen.getByRole('region', { name: /Backlog/ });
    expect(
      within(backlogColumn).getByRole('button', { name: /Mapear endpoints de billing/ }),
    ).toBeInTheDocument();
    expect(
      within(backlogColumn).queryByRole('button', { name: /Definir tokens de espaçamento/ }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Limpar filtros' }));
    expect(
      await screen.findByText(`${projectTasks.length - 1} de ${projectTasks.length} tarefas`),
    ).toBeInTheDocument();
  });

  it('filtra por coluna na barra mantendo as demais visíveis com contador 0', async () => {
    const user = userEvent.setup();
    renderBoard();

    await user.selectOptions(await screen.findByLabelText('Coluna'), 'blocked');

    const blockedColumn = await screen.findByRole('region', { name: /Precisa de atenção/ });
    expect(
      within(blockedColumn).getByRole('button', { name: /Deploy em staging/ }),
    ).toBeInTheDocument();
    // Estrutura preservada: coluna vazia continua visível com contador 0.
    const backlogColumn = screen.getByRole('region', { name: /Planejado/ });
    expect(within(backlogColumn).getByText('0')).toBeInTheDocument();
    expect(within(backlogColumn).getByText('Sem tarefas nesta coluna.')).toBeInTheDocument();
  });

  it('esconde arquivadas por padrão e as mostra (com badge) no filtro "Arquivadas"', async () => {
    const user = userEvent.setup();
    renderBoard();

    const doneColumn = await screen.findByRole('region', { name: /Concluído/ });
    expect(
      within(doneColumn).queryByRole('button', { name: /Setup do Vite/ }),
    ).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Arquivamento'), 'archived');
    expect(await screen.findByText(`1 de ${projectTasks.length} tarefas`)).toBeInTheDocument();
    expect(within(doneColumn).getByRole('button', { name: /Setup do Vite/ })).toBeInTheDocument();
    expect(within(doneColumn).getByText('Arquivada')).toBeInTheDocument();
  });

  it('arquiva todas as concluídas em lote, com diálogo de confirmação', async () => {
    const user = userEvent.setup();
    renderBoard();

    // 6 concluídas no projeto 1, 1 já arquivada na fixture → 5 elegíveis.
    const batchButton = await screen.findByRole('button', { name: /Arquivar concluídas \(5\)/ });
    await user.click(batchButton);

    const dialog = await screen.findByRole('dialog', { name: 'Arquivar tarefas concluídas' });
    await user.click(within(dialog).getByRole('button', { name: /Arquivar 5 tarefa/ }));

    // Após arquivar, não restam elegíveis: botão zera e desabilita.
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Arquivar concluídas \(0\)/ })).toBeDisabled();
    });
    // A coluna Concluída fica vazia no filtro padrão (ativas).
    const doneColumn = screen.getByRole('region', { name: /Concluído/ });
    expect(within(doneColumn).getByText('0')).toBeInTheDocument();
  });

  it('desarquiva pelo detalhe e o card volta ao quadro padrão', async () => {
    const user = userEvent.setup();
    const archived = projectTasks.find((entry) => entry.archivedAt !== null)!;
    renderBoard(`/board?task=${archived.id}&archive=all`);

    const unarchive = await screen.findByRole('button', { name: 'Desarquivar' });
    await user.click(unarchive);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Arquivar' })).toBeEnabled();
    });
  });

  it('não permite arquivar tarefa não concluída (botão desabilitado no detalhe)', async () => {
    const backlogTask = projectTasks.find((entry) => entry.state === 'backlog')!;
    renderBoard(`/board?task=${backlogTask.id}`);

    const archive = await screen.findByRole('button', { name: 'Arquivar' });
    expect(archive).toBeDisabled();
  });

  it('abre "Como o trabalho flui" com a ordem das colunas e Bloqueada transversal', async () => {
    const user = userEvent.setup();
    renderBoard();

    await user.click(await screen.findByRole('button', { name: 'Como o trabalho flui' }));
    const dialog = await screen.findByRole('dialog', { name: 'Como o trabalho flui' });

    // Stepper linear (7 colunas) — o impedimento fica fora da lista ordenada.
    const steps = within(dialog).getAllByRole('list')[0];
    for (const name of [
      'Planejado',
      'Pronto para começar',
      'Em andamento',
      'Em revisão',
      'Em correção',
      'Em validação',
      'Concluído',
    ]) {
      expect(within(steps).getByText(name)).toBeInTheDocument();
    }
    expect(within(steps).queryByText('Precisa de atenção')).not.toBeInTheDocument();
    expect(within(dialog).getByText(/pode surgir em qualquer etapa/i)).toBeInTheDocument();
    expect(within(dialog).getByText('Suas decisões')).toBeInTheDocument();
    expect(within(dialog).getByText('Como a equipe conduz o trabalho')).toBeInTheDocument();
    expect(within(dialog).queryByText(/gate|card|Bruna|agente/i)).not.toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });
});
