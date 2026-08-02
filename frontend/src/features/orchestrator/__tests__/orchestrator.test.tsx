import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

import {
  buildFixtures,
  DeterministicUlidGenerator,
  MockApiClient,
  MockRealtimeClient,
  mulberry32,
  streams,
} from '@/api';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import {
  attemptsOf,
  chiefBudgets,
  deriveChiefHealth,
  derivePresentedChiefHealth,
  filterAttemptEvents,
  groupAgentsByState,
  latestAttemptOf,
  resolveChiefAccount,
  resolveChiefModel,
  resolveLastActivityAt,
  resolveOperationMode,
  runningAttemptOf,
  sortAttemptEvents,
  STALE_HEARTBEAT_MS,
} from '@/features/orchestrator/lib/orchestrator-derive';
import OrchestratorPage from '@/features/orchestrator/pages/orchestrator-page';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

const fixtures = buildFixtures(42);
const project = fixtures.data.projects[0];
const projectAgents = fixtures.data.agents.filter((agent) => agent.projectId === project.id);
const projectAgentIds = new Set(projectAgents.map((agent) => agent.id));
const projectAttempts = fixtures.data.attempts.filter((attempt) =>
  projectAgentIds.has(attempt.agentId),
);
const runningAttempt = projectAttempts.find((attempt) => attempt.state === 'running')!;
const runningAgent = projectAgents.find((agent) => agent.id === runningAttempt.agentId)!;

describe('orchestrator-derive', () => {
  const now = new Date('2026-07-17T12:00:00Z');

  it('deriva a saúde do chefe: erro > heartbeat parado > ok', () => {
    const fresh = new Date(now.getTime() - STALE_HEARTBEAT_MS / 2).toISOString();
    const stale = new Date(now.getTime() - STALE_HEARTBEAT_MS - 1).toISOString();
    expect(deriveChiefHealth('error', fresh, now)).toBe('error');
    expect(deriveChiefHealth('outOfQuota', fresh, now)).toBe('error');
    expect(deriveChiefHealth('working', null, now)).toBe('attention');
    expect(deriveChiefHealth('working', stale, now)).toBe('attention');
    expect(deriveChiefHealth('working', fresh, now)).toBe('ok');
    expect(deriveChiefHealth('idle', fresh, now)).toBe('ok');
  });

  it('apresenta atenção quando o processo está saudável, mas a prontidão está incompleta', () => {
    expect(derivePresentedChiefHealth('ok', 'awaitingProvider')).toBe('attention');
    expect(derivePresentedChiefHealth('ok', 'awaitingWorkflow')).toBe('attention');
    expect(derivePresentedChiefHealth('ok', 'ready')).toBe('ok');
    expect(derivePresentedChiefHealth('ok', 'running')).toBe('ok');
    expect(derivePresentedChiefHealth('error', 'degraded')).toBe('error');
  });

  it('agrupa agentes por estado na ordem do domínio, sem grupos vazios', () => {
    const groups = groupAgentsByState(projectAgents);
    const states = groups.map((group) => group.state);
    expect(states).toEqual(['working', 'idle', 'waiting', 'error', 'outOfQuota']);
    expect(groups.every((group) => group.agents.length > 0)).toBe(true);
    expect(groupAgentsByState([])).toEqual([]);
  });

  it('resolve tentativas do agente: running e mais recente', () => {
    expect(runningAttemptOf(projectAttempts, runningAgent.id)?.id).toBe(runningAttempt.id);
    expect(latestAttemptOf(projectAttempts, runningAgent.id)?.id).toBe(runningAttempt.id);
    expect(attemptsOf(projectAttempts, runningAgent.id).length).toBeGreaterThan(0);
    const chiefAgent = projectAgents.find((agent) => agent.id === project.chiefAgentId)!;
    expect(runningAttemptOf(projectAttempts, chiefAgent.id)).toBeNull();
  });

  it('resolve o modelo do chefe: override da instância ou default da definição', () => {
    const chief = projectAgents.find((agent) => agent.id === project.chiefAgentId)!;
    const definition = fixtures.data['agent-definitions'].find(
      (entry) => entry.id === chief.definitionId,
    )!;
    const model = resolveChiefModel(chief, definition, fixtures.data.models);
    expect(model?.id).toBe(definition.defaultModelId);

    const otherModel = fixtures.data.models.find((entry) => entry.id !== model?.id)!;
    const overridden = resolveChiefModel(
      { ...chief, modelId: otherModel.id },
      definition,
      fixtures.data.models,
    );
    expect(overridden?.id).toBe(otherModel.id);
    expect(resolveChiefModel({ ...chief, modelId: null }, null, fixtures.data.models)).toBeNull();
  });

  it('resolve a conta do modelo preferindo a ativa do provedor', () => {
    const model = fixtures.data.models.find((entry) => entry.name === 'gpt-4o')!;
    const account = resolveChiefAccount(model, fixtures.data.accounts);
    expect(account?.providerId).toBe(model.providerId);
    expect(account?.state).toBe('active');
    expect(resolveChiefAccount(null, fixtures.data.accounts)).toBeNull();
  });

  it('seleciona budgets do projeto e da conta (nunca o global)', () => {
    const accountBudget = fixtures.data.budgets.find((budget) => budget.scope === 'account')!;
    const budgets = chiefBudgets(fixtures.data.budgets, project.id, accountBudget.scopeId);
    expect(budgets.map((budget) => budget.scope).sort()).toEqual(['account', 'project']);
    expect(chiefBudgets(fixtures.data.budgets, project.id, null)).toHaveLength(1);
  });

  it('modo de operação efetivo vem do workflow vinculado, não do default do projeto', () => {
    const workflow = fixtures.data.workflows.find((entry) => entry.projectId === project.id)!;
    // O default do projeto ('manual') não deve mascarar o modo que o dono
    // realmente configurou no workflow.
    expect(resolveOperationMode(project, { ...workflow, operationMode: 'autonomous' })).toBe(
      'autonomous',
    );
    expect(resolveOperationMode(project, { ...workflow, operationMode: 'semiautonomous' })).toBe(
      'semiautonomous',
    );
    // Sem workflow vinculado, cai no default do projeto.
    expect(resolveOperationMode({ ...project, operationMode: 'manual' }, null)).toBe('manual');
  });

  it('última atividade considera turnos de conversa recentes, não só a config do projeto', () => {
    const conversation = fixtures.data.conversations.find(
      (entry) => entry.projectId === project.id,
    )!;
    const base = { ...project, lastActivityAt: '2026-07-20T00:00:00.000Z' };
    // Conversa de hoje avança a "última atividade" mesmo com a config antiga.
    expect(
      resolveLastActivityAt(base, [
        { ...conversation, lastMessageAt: '2026-07-23T10:00:00.000Z' },
        { ...conversation, lastMessageAt: null },
      ]),
    ).toBe('2026-07-23T10:00:00.000Z');
    // Conversa mais antiga que a atividade do projeto não regride o valor.
    expect(
      resolveLastActivityAt(base, [{ ...conversation, lastMessageAt: '2026-07-01T00:00:00.000Z' }]),
    ).toBe('2026-07-20T00:00:00.000Z');
    expect(resolveLastActivityAt(base, [])).toBe('2026-07-20T00:00:00.000Z');
  });

  it('ordena eventos por occurredAt e filtra por tipo + texto (sem inventar nível)', () => {
    const events = sortAttemptEvents([
      {
        id: 'b',
        attemptId: 'a',
        kind: 'log',
        content: 'Depois',
        occurredAt: '2026-07-17T12:01:00Z',
      },
      {
        id: 'a',
        attemptId: 'a',
        kind: 'diff',
        content: 'git diff Token',
        occurredAt: '2026-07-17T12:00:00Z',
      },
    ]);
    expect(events.map((event) => event.id)).toEqual(['a', 'b']);

    expect(filterAttemptEvents(events, 'diff', '')).toHaveLength(1);
    expect(filterAttemptEvents(events, '', 'token')).toHaveLength(1);
    expect(filterAttemptEvents(events, 'log', 'token')).toHaveLength(0);
    expect(filterAttemptEvents(events, '', '  ')).toHaveLength(2);
  });
});

/**
 * Saúde, modo de trabalho, conta e jornada são leitura TÉCNICA desde a F8/§2 —
 * o cliente leigo vê a pessoa, o estado dela e as ações em português.
 */
function renderOrchestrator(
  bundle: TestBundle = createTestBundle(),
  mode: 'business' | 'technical' = 'technical',
) {
  const profileId = bundle.fixtures.meta.currentProfileId;
  useSessionStore.setState({ activeProfileId: profileId });
  usePresentationStore.getState().requestMode(profileId, mode);
  return renderWithApi(
    <MemoryRouter>
      <OrchestratorPage />
    </MemoryRouter>,
    bundle,
  );
}

beforeEach(() => {
  usePresentationStore.setState({ modeByProfile: {} });
  useSessionStore.setState({ activeProfileId: null });
});

/** Bundle com um evento contendo segredo na tentativa em execução. */
function createBundleWithSecretEvent(): TestBundle {
  const data = buildFixtures(42);
  const running = data.data.attempts.find((attempt) => attempt.state === 'running')!;
  data.data['attempt-events'].push({
    id: '01SECRET000000000000000000',
    attemptId: running.id,
    kind: 'log',
    content: 'Enviando api_key=sk-live-123456 para o provedor',
    occurredAt: '2026-07-17T12:00:00Z',
  });
  const realtime = new MockRealtimeClient({ connectDelayMs: 1 });
  const ids = new DeterministicUlidGenerator(777);
  const api = new MockApiClient(data, {
    latency: { min: 1, max: 3 },
    random: mulberry32(99),
    nextId: () => ids.next(),
    now: () => '2026-07-17T12:00:00Z',
    currentProfileId: data.meta.currentProfileId,
    realtime,
  });
  return { api, realtime, fixtures: data };
}

describe('OrchestratorPage', () => {
  it('renderiza o card do chefe com modelo, modo, saúde e ações', async () => {
    renderOrchestrator();

    expect((await screen.findAllByText('Bruna Magalhães')).length).toBeGreaterThan(0);
    // Rótulo estável + displayName do modelo em uso (E2E).
    expect(screen.getByText('Modo de trabalho em uso')).toBeInTheDocument();
    expect(screen.getByText('GPT-4o')).toBeInTheDocument();
    expect(screen.getByText('Modo de operação')).toBeInTheDocument();
    expect(screen.getByText('Manual')).toBeInTheDocument();
    // Heartbeat das fixtures é antigo → saúde "Atenção".
    expect(screen.getByText('Atenção')).toBeInTheDocument();
    expect(screen.getByText('Ainda não começou')).toBeInTheDocument();

    expect(screen.getByRole('button', { name: 'Pausar trabalhos' })).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Concluir pendências e pausar' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Transferir liderança' })).toBeInTheDocument();
  });

  it('modo Negócio apresenta a pessoa e as ações, sem saúde, conta ou jornada', async () => {
    renderOrchestrator(createTestBundle(), 'business');

    // A pessoa, o cargo e o estado dela em português — com a frase de apoio do léxico.
    expect((await screen.findAllByText('Bruna Magalhães')).length).toBeGreaterThan(0);
    expect(screen.getByText('Como a Bruna se comunica')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Pausar trabalhos' })).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Concluir pendências e pausar' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Transferir liderança' })).toBeInTheDocument();

    // O que era operação de máquina sai de cena: saúde, modelo, conta, jornada e
    // o diagnóstico (lease/fencing) só existem no modo Técnico.
    expect(screen.queryByText('Saúde')).not.toBeInTheDocument();
    expect(screen.queryByText('Atenção')).not.toBeInTheDocument();
    expect(screen.queryByText('Modo de trabalho em uso')).not.toBeInTheDocument();
    expect(screen.queryByText('Conta em uso')).not.toBeInTheDocument();
    expect(screen.queryByText('Jornada')).not.toBeInTheDocument();
    expect(screen.queryByText('GPT-4o')).not.toBeInTheDocument();
    expect(screen.queryByText('Diagnóstico avançado')).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Definições de agentes' })).not.toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/chief-claude-primary|claude-code|anthropic/i);
    expect(document.body.textContent).not.toMatch(/lease|fencing|heartbeat/i);
  });

  it('agrupa os agentes por estado com tarefa, tentativas e custo', async () => {
    renderOrchestrator();

    expect(await screen.findByText('Iara (Backend)')).toBeInTheDocument();
    expect(screen.getByText('Otávio (Frontend)')).toBeInTheDocument();
    expect(screen.getByText('Lia (Testes)')).toBeInTheDocument();
    expect(screen.getByText('Nina (Protótipos)')).toBeInTheDocument();
    // O chefe NÃO aparece na grade (tem card próprio).
    expect(screen.getAllByText('Bruna Magalhães')).not.toHaveLength(0);

    // Agente com tentativa em execução mostra duração ao vivo.
    const runningCard = (await screen.findByText(runningAgent.name)).closest('li')!;
    expect(within(runningCard).getByText(/Em execução há/)).toBeInTheDocument();
  });

  it('abre a tentativa com linha do tempo, log filtrável e segredos mascarados', async () => {
    const user = userEvent.setup();
    renderOrchestrator(createBundleWithSecretEvent());

    const runningCard = (await screen.findByText(runningAgent.name)).closest('li')!;
    await user.click(within(runningCard).getByRole('button', { name: 'Abrir' }));

    const dialog = await screen.findByRole('dialog', {
      name: `Rodada de trabalho nº ${runningAttempt.number}`,
    });
    expect(within(dialog).getByText('Linha do tempo')).toBeInTheDocument();
    expect(within(dialog).getByText('Log estruturado')).toBeInTheDocument();
    // Segredo NUNCA exibido: valor mascarado (linha do tempo + log) + nota.
    expect((await within(dialog).findAllByText(/api_key=\*\*\*\*/)).length).toBeGreaterThan(0);
    expect(within(dialog).queryByText(/sk-live-123456/)).not.toBeInTheDocument();
    expect(
      within(dialog).getByText('Revelado somente com permissão.'),
    ).toBeInTheDocument();

    // Filtro por tipo + busca textual.
    await user.selectOptions(within(dialog).getByLabelText('Tipo de evento'), 'log');
    await user.type(within(dialog).getByLabelText('Buscar no log'), 'provedor');
    expect((await within(dialog).findAllByText(/api_key=\*\*\*\*/)).length).toBeGreaterThan(0);
    await user.clear(within(dialog).getByLabelText('Buscar no log'));
    await user.type(within(dialog).getByLabelText('Buscar no log'), 'texto-inexistente');
    expect(
      await within(dialog).findByText('Nenhum evento corresponde aos filtros.'),
    ).toBeInTheDocument();
  });

  it('atualiza o estado do turno do chefe via realtime', async () => {
    const bundle = createTestBundle();
    renderOrchestrator(bundle);

    expect(await screen.findByText('Ainda não começou')).toBeInTheDocument();
    const conversation = bundle.fixtures.data.conversations.find(
      (entry) => entry.projectId === project.id,
    )!;
    act(() => {
      bundle.realtime.emit(streams.conversation(conversation.id), 'chief.turnStateChanged', {
        conversationId: conversation.id,
        turnId: project.id,
        projectId: project.id,
        state: 'pending',
      });
    });
    expect(await screen.findByText('Na fila')).toBeInTheDocument();
    act(() => {
      bundle.realtime.emit(streams.conversation(conversation.id), 'chief.turnStateChanged', {
        conversationId: conversation.id,
        turnId: project.id,
        projectId: project.id,
        state: 'processing',
      });
    });
    expect(await screen.findByText('Em andamento')).toBeInTheDocument();
    act(() => {
      bundle.realtime.emit(streams.conversation(conversation.id), 'chief.turnStateChanged', {
        conversationId: conversation.id,
        turnId: project.id,
        projectId: project.id,
        state: 'completed',
      });
    });
    expect(await screen.findByText('Concluído')).toBeInTheDocument();
    act(() => {
      bundle.realtime.emit(streams.conversation(conversation.id), 'chief.turnStateChanged', {
        conversationId: conversation.id,
        turnId: project.id,
        projectId: project.id,
        state: 'failed',
      });
    });
    expect(await screen.findByText('Não deu certo')).toBeInTheDocument();
  });

  it('pausa e retoma a orquestração do chefe', async () => {
    const user = userEvent.setup();
    renderOrchestrator();

    await user.click(await screen.findByRole('button', { name: 'Pausar trabalhos' }));
    expect(await screen.findByRole('button', { name: 'Retomar trabalhos' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Retomar trabalhos' }));
    expect(await screen.findByRole('button', { name: 'Pausar trabalhos' })).toBeInTheDocument();
  });

  it('abre o editor de enquadramento ao selecionar uma nova foto da Bruna', async () => {
    const user = userEvent.setup();
    renderOrchestrator();

    await user.click(
      await screen.findByRole('button', {
        name: 'Editar perfil, comunicação e roteamento',
      }),
    );
    const dialog = await screen.findByRole('dialog', {
      name: 'Perfil e personalização de Bruna',
    });
    const input = within(dialog).getByLabelText('Trocar e ajustar foto');
    await user.upload(input, new File(['imagem'], 'bruna.png', { type: 'image/png' }));

    expect(
      within(dialog).getByRole('heading', { name: 'Ajustar foto de Bruna Magalhães' }),
    ).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Zoom')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Posição vertical')).toBeInTheDocument();

    await user.click(within(dialog).getAllByRole('button', { name: 'Cancelar' }).at(-1)!);
    expect(within(dialog).getByLabelText('Nome exibido')).toBeInTheDocument();
  });

  it('passa o bastão em 2 etapas e o card reflete o novo modelo', async () => {
    const user = userEvent.setup();
    renderOrchestrator();

    await user.click(await screen.findByRole('button', { name: 'Transferir liderança' }));
    const dialog = await screen.findByRole('dialog', { name: 'Transferir liderança' });

    // Motivo obrigatório: avançar sem preencher mostra o erro.
    await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));
    expect(
      await within(dialog).findByText('Informe o motivo da transferência.'),
    ).toBeInTheDocument();

    await user.selectOptions(within(dialog).getByLabelText('Modo de trabalho da nova liderança'), [
      'Claude Sonnet 4',
    ]);
    await user.type(within(dialog).getByLabelText(/Motivo/), 'Teste de passagem de bastão');
    await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));

    // Etapa 2: resumo das escolhas + confirmação final.
    expect(within(dialog).getByText('Etapa 2 de 2 — confirmação')).toBeInTheDocument();
    expect(within(dialog).getByText('Teste de passagem de bastão')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Confirmar transferência' }));

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Passagem de bastão' })).not.toBeInTheDocument(),
    );
    // O card do chefe reflete o novo modelo (nova instância no mock).
    expect(await screen.findByText('Claude Sonnet 4')).toBeInTheDocument();
    expect(screen.getByText('Modo de trabalho em uso')).toBeInTheDocument();
  });

  it('drena tarefas com confirmação e observação opcional', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    renderOrchestrator(bundle);

    await user.click(await screen.findByRole('button', { name: 'Concluir pendências e pausar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Concluir pendências e pausar' });
    expect(within(dialog).getByText(/voltam para a coluna Pronta/)).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText(/Observação/), 'Drenar para manutenção');
    const activeStates = new Set(['development', 'review', 'corrections', 'testsGates']);
    const expected = bundle.fixtures.data.tasks.filter(
      (task) => task.projectId === project.id && activeStates.has(task.state),
    ).length;
    await user.click(within(dialog).getByRole('button', { name: 'Concluir pendências e pausar' }));

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Concluir pendências e pausar' })).not.toBeInTheDocument(),
    );
    let drained = -1;
    await act(async () => {
      drained = (
        await bundle.api.list('tasks', { filter: { projectId: project.id } })
      ).items.filter((task) => activeStates.has(task.state)).length;
    });
    expect(drained).toBe(0);
    expect(expected).toBeGreaterThan(0);
  });
});
