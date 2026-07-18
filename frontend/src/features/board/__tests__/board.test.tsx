import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { streams } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import BoardPage from '@/features/board/pages/board-page';
import { groupTasksByState, parseTaskStateParam } from '@/features/board/lib/board-derive';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;
const { tasks, projects } = fixtures;
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
});

function renderBoard(initialEntry = '/board') {
  // Bundle novo por teste: o store do mock é mutável (aprovações, prioridade).
  const bundle = createTestBundle();
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
      'Backlog',
      'Pronta',
      'Em desenvolvimento',
      'Em revisão',
      'Em correção',
      'Testes e gates',
      'Bloqueada',
      'Concluída',
    ]) {
      expect(await screen.findByRole('region', { name: new RegExp(name) })).toBeInTheDocument();
    }

    const backlogColumn = screen.getByRole('region', { name: /Backlog/ });
    expect(
      within(backlogColumn).getByRole('button', { name: /Mapear endpoints de billing/ }),
    ).toBeInTheDocument();

    const blockedColumn = screen.getByRole('region', { name: /Bloqueada/ });
    expect(
      within(blockedColumn).getByRole('button', { name: /Deploy em staging/ }),
    ).toBeInTheDocument();
    expect(within(blockedColumn).getAllByText(/Bloqueada:/).length).toBeGreaterThan(0);

    // Não existe botão "nova tarefa" — humano não cria tarefa técnica.
    expect(screen.queryByRole('button', { name: /nova tarefa/i })).not.toBeInTheDocument();
    expect(screen.getByText(/cadeia solicitação → demanda → tarefa/)).toBeInTheDocument();
  });

  it('move o card de coluna ao receber task.stateChanged no stream do projeto', async () => {
    const { bundle } = renderBoard();

    const backlogColumn = await screen.findByRole('region', { name: /Backlog/ });
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

    const readyColumn = screen.getByRole('region', { name: /Pronta/ });
    expect(await within(readyColumn).findByRole('button', { name: cardName })).toBeInTheDocument();
    expect(within(backlogColumn).queryByRole('button', { name: cardName })).not.toBeInTheDocument();
  });

  it('destaca a coluna filtrada por ?state= (link do cockpit)', async () => {
    renderBoard('/board?state=blocked');

    const blockedColumn = await screen.findByRole('region', { name: /Bloqueada/ });
    expect(blockedColumn).toHaveAttribute('data-highlighted', 'true');
    const backlogColumn = screen.getByRole('region', { name: /Backlog/ });
    expect(backlogColumn).not.toHaveAttribute('data-highlighted');
  });

  it('abre o detalhe como página dedicada no mobile e volta ao quadro', async () => {
    const user = userEvent.setup();
    renderBoard();

    const backlogColumn = await screen.findByRole('region', { name: /Backlog/ });
    await user.click(within(backlogColumn).getByRole('button', { name: /Mapear endpoints de billing/ }));

    // Detalhe substitui o quadro (página mobile) — instrução imutável visível.
    expect(await screen.findByText('Instrução enviada ao agente')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Mapear endpoints de billing' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: /Backlog/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Voltar ao quadro/ }));
    expect(await screen.findByRole('region', { name: /Backlog/ })).toBeInTheDocument();
  });

  it('abre o detalhe como drawer no desktop (lg+) e fecha com Esc', async () => {
    stubMatchMedia(true);
    const user = userEvent.setup();
    renderBoard('/board?task=' + projectTasks[0].id);

    const dialog = await screen.findByRole('dialog', { name: 'Detalhes da tarefa' });
    expect(dialog).toBeInTheDocument();
    // O quadro continua visível atrás do drawer.
    expect(await screen.findByRole('region', { name: /Backlog/ })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
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
});
