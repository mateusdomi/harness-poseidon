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
  filterAttemptEvents,
  groupAgentsByState,
  latestAttemptOf,
  resolveChiefAccount,
  resolveChiefModel,
  runningAttemptOf,
  sortAttemptEvents,
  STALE_HEARTBEAT_MS,
} from '@/features/orchestrator/lib/orchestrator-derive';
import OrchestratorPage from '@/features/orchestrator/pages/orchestrator-page';
import { renderWithApi } from '@/test/render-with-providers';

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

  it('ordena eventos por occurredAt e filtra por tipo + texto (sem inventar nível)', () => {
    const events = sortAttemptEvents([
      { id: 'b', attemptId: 'a', kind: 'log', content: 'Depois', occurredAt: '2026-07-17T12:01:00Z' },
      { id: 'a', attemptId: 'a', kind: 'diff', content: 'git diff Token', occurredAt: '2026-07-17T12:00:00Z' },
    ]);
    expect(events.map((event) => event.id)).toEqual(['a', 'b']);

    expect(filterAttemptEvents(events, 'diff', '')).toHaveLength(1);
    expect(filterAttemptEvents(events, '', 'token')).toHaveLength(1);
    expect(filterAttemptEvents(events, 'log', 'token')).toHaveLength(0);
    expect(filterAttemptEvents(events, '', '  ')).toHaveLength(2);
  });
});

function renderOrchestrator(bundle: TestBundle = createTestBundle()) {
  return renderWithApi(
    <MemoryRouter>
      <OrchestratorPage />
    </MemoryRouter>,
    bundle,
  );
}

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

    expect(await screen.findByText('Chefe — Poseidon Frontend')).toBeInTheDocument();
    // Rótulo estável + displayName do modelo em uso (E2E).
    expect(screen.getByText('Modelo em uso')).toBeInTheDocument();
    expect(screen.getByText('GPT-4o')).toBeInTheDocument();
    expect(screen.getByText('Modo de operação')).toBeInTheDocument();
    expect(screen.getByText('Manual')).toBeInTheDocument();
    // Heartbeat das fixtures é antigo → saúde "Atenção".
    expect(screen.getByText('Atenção')).toBeInTheDocument();
    expect(screen.getByText('Não iniciado')).toBeInTheDocument();

    expect(screen.getByRole('button', { name: 'Pausar' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Drenar tarefas' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Passar bastão' })).toBeInTheDocument();
  });

  it('agrupa os agentes por estado com tarefa, tentativas e custo', async () => {
    renderOrchestrator();

    expect(await screen.findByText('Iara (Backend)')).toBeInTheDocument();
    expect(screen.getByText('Otávio (Frontend)')).toBeInTheDocument();
    expect(screen.getByText('Lia (Testes)')).toBeInTheDocument();
    expect(screen.getByText('Nina (Protótipos)')).toBeInTheDocument();
    // O chefe NÃO aparece na grade (tem card próprio).
    expect(screen.getAllByText('Chefe — Poseidon Frontend')).toHaveLength(1);

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
      name: `Tentativa nº ${runningAttempt.number}`,
    });
    expect(within(dialog).getByText('Linha do tempo')).toBeInTheDocument();
    expect(within(dialog).getByText('Log estruturado')).toBeInTheDocument();
    // Segredo NUNCA exibido: valor mascarado (linha do tempo + log) + nota.
    expect(
      (await within(dialog).findAllByText(/api_key=\*\*\*\*/)).length,
    ).toBeGreaterThan(0);
    expect(within(dialog).queryByText(/sk-live-123456/)).not.toBeInTheDocument();
    expect(
      within(dialog).getByText('Revelado no backend somente com permissão.'),
    ).toBeInTheDocument();

    // Filtro por tipo + busca textual.
    await user.selectOptions(within(dialog).getByLabelText('Tipo de evento'), 'log');
    await user.type(within(dialog).getByLabelText('Buscar no log'), 'provedor');
    expect(
      (await within(dialog).findAllByText(/api_key=\*\*\*\*/)).length,
    ).toBeGreaterThan(0);
    await user.clear(within(dialog).getByLabelText('Buscar no log'));
    await user.type(within(dialog).getByLabelText('Buscar no log'), 'texto-inexistente');
    expect(
      await within(dialog).findByText('Nenhum evento corresponde aos filtros.'),
    ).toBeInTheDocument();
  });

  it('atualiza o estado do turno do chefe via realtime', async () => {
    const bundle = createTestBundle();
    renderOrchestrator(bundle);

    expect(await screen.findByText('Não iniciado')).toBeInTheDocument();
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
    expect(await screen.findByText('Pendente')).toBeInTheDocument();
    act(() => {
      bundle.realtime.emit(streams.conversation(conversation.id), 'chief.turnStateChanged', {
        conversationId: conversation.id,
        turnId: project.id,
        projectId: project.id,
        state: 'processing',
      });
    });
    expect(await screen.findByText('Processando')).toBeInTheDocument();
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
    expect(await screen.findByText('Falhou')).toBeInTheDocument();
  });

  it('pausa e retoma a orquestração do chefe', async () => {
    const user = userEvent.setup();
    renderOrchestrator();

    await user.click(await screen.findByRole('button', { name: 'Pausar' }));
    expect(await screen.findByRole('button', { name: 'Retomar' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Retomar' }));
    expect(await screen.findByRole('button', { name: 'Pausar' })).toBeInTheDocument();
  });

  it('passa o bastão em 2 etapas e o card reflete o novo modelo', async () => {
    const user = userEvent.setup();
    renderOrchestrator();

    await user.click(await screen.findByRole('button', { name: 'Passar bastão' }));
    const dialog = await screen.findByRole('dialog', { name: 'Passagem de bastão' });

    // Motivo obrigatório: avançar sem preencher mostra o erro.
    await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));
    expect(
      await within(dialog).findByText('Informe o motivo da passagem de bastão.'),
    ).toBeInTheDocument();

    await user.selectOptions(within(dialog).getByLabelText('Modelo do novo chefe'), [
      'Claude Sonnet 4',
    ]);
    await user.type(within(dialog).getByLabelText(/Motivo/), 'Teste de passagem de bastão');
    await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));

    // Etapa 2: resumo das escolhas + confirmação final.
    expect(within(dialog).getByText('Etapa 2 de 2 — confirmação')).toBeInTheDocument();
    expect(within(dialog).getByText('Teste de passagem de bastão')).toBeInTheDocument();
    await user.click(
      within(dialog).getByRole('button', { name: 'Confirmar passagem de bastão' }),
    );

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Passagem de bastão' })).not.toBeInTheDocument(),
    );
    // O card do chefe reflete o novo modelo (nova instância no mock).
    expect(await screen.findByText('Claude Sonnet 4')).toBeInTheDocument();
    expect(screen.getByText('Modelo em uso')).toBeInTheDocument();
  });

  it('drena tarefas com confirmação e observação opcional', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    renderOrchestrator(bundle);

    await user.click(await screen.findByRole('button', { name: 'Drenar tarefas' }));
    const dialog = await screen.findByRole('dialog', { name: 'Drenar tarefas' });
    expect(within(dialog).getByText(/voltam para a coluna Pronta/)).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText(/Observação/), 'Drenar para manutenção');
    const activeStates = new Set(['development', 'review', 'corrections', 'testsGates']);
    const expected = bundle.fixtures.data.tasks.filter(
      (task) => task.projectId === project.id && activeStates.has(task.state),
    ).length;
    await user.click(within(dialog).getByRole('button', { name: 'Drenar tarefas' }));

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Drenar tarefas' })).not.toBeInTheDocument(),
    );
    let drained = -1;
    await act(async () => {
      drained = (await bundle.api.list('tasks', { filter: { projectId: project.id } })).items.filter(
        (task) => activeStates.has(task.state),
      ).length;
    });
    expect(drained).toBe(0);
    expect(expected).toBeGreaterThan(0);
  });
});
