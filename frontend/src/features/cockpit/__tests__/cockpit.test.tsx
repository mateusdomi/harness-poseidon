import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router-dom';
import { vi } from 'vitest';

import { buildFixtures, type Agent, type AuditEvent } from '@/api';
import { ActivityFeed } from '@/features/cockpit/components/activity-feed';
import { ProgressTracks } from '@/features/cockpit/components/progress-tracks';
import CockpitPage from '@/features/cockpit/pages/cockpit-page';
import {
  aggregateProgress,
  budgetSeverity,
  countTasksByState,
  currentPhase,
  factoryAgentMetrics,
  filterActivityByPeriod,
  progressEvidence,
  recommendNextAction,
  tasksOfPhase,
} from '@/features/cockpit/lib/cockpit-derive';
import { workflowPhaseKey } from '@/features/cockpit/hooks/use-cockpit';
import { isMeaningfulActivity } from '@/features/cockpit/lib/activity-humanize';
import { summarizeTeamActivity } from '@/features/cockpit/lib/dashboard-presentation';
import { renderWithApi } from '@/test/render-with-providers';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import { usePresentationStore } from '@/stores/presentation-store';

const fixtures = buildFixtures(42);
const tasks = fixtures.data.tasks;
const phases = fixtures.data.phases;
const budgets = fixtures.data.budgets;

describe('cockpit-derive', () => {
  it('agrega progresso em três trilhas separadas (média, sem somar trilhas)', () => {
    const progress = aggregateProgress(tasks.slice(0, 10));
    expect(progress.executed).toBeGreaterThanOrEqual(0);
    expect(progress.validated).toBeGreaterThanOrEqual(0);
    expect(progress.approved).toBeGreaterThanOrEqual(0);
    // Nunca soma trilhas: cada uma é uma média independente 0–100.
    for (const value of Object.values(progress)) {
      expect(value).toBeLessThanOrEqual(100);
    }
    expect(aggregateProgress([])).toEqual({ executed: 0, validated: 0, approved: 0 });
  });

  it('expõe numerador, denominador, pendências e atualização do progresso', () => {
    const sample = tasks.slice(0, 3);
    const evidence = progressEvidence(sample);
    expect(evidence.tracks.executed.denominator).toBe(300);
    expect(evidence.tracks.executed.numerator).toBe(
      sample.reduce((sum, task) => sum + task.progress.executed, 0),
    );
    expect(evidence.tracks.approved.pendingItems).toBe(
      sample.filter((task) => task.progress.approved < 100).length,
    );
    expect(evidence.updatedAt).toBeTruthy();
  });

  it('mantém fontes distintas para as três trilhas técnicas', () => {
    const sample = [
      { ...tasks[0], progress: { executed: 90, validated: 60, approved: 30 } },
      { ...tasks[1], progress: { executed: 70, validated: 40, approved: 10 } },
    ];

    expect(aggregateProgress(sample)).toEqual({
      executed: 80,
      validated: 50,
      approved: 20,
    });
    expect(progressEvidence(sample).tracks).toMatchObject({
      executed: { numerator: 160, denominator: 200 },
      validated: { numerator: 100, denominator: 200 },
      approved: { numerator: 40, denominator: 200 },
    });
  });

  it('conta tarefas por estado com todas as 8 colunas presentes', () => {
    const counts = countTasksByState(tasks);
    expect(Object.keys(counts)).toHaveLength(8);
    expect(counts.blocked).toBe(3);
    expect(counts.done).toBeGreaterThan(0);
  });

  it('identifica a fase atual e agrega as colunas do domínio dela', () => {
    const phase = currentPhase(phases);
    expect(phase?.name).toBe('Validação');
    const phaseTasks = tasksOfPhase(tasks, phase);
    expect(phaseTasks.every((t) => ['review', 'corrections', 'testsGates'].includes(t.state))).toBe(
      true,
    );
  });

  it('mapeia a ordem persistida para a chave do plano de obrigações', async () => {
    const phase = phases[2];
    expect(workflowPhaseKey(phase)).toBe(`phase-${phase.order}`);

    const bundle = createTestBundle();
    const progress = await bundle.api.getPhaseObligationProgress(phase.runId, workflowPhaseKey(phase));
    expect(progress.runId).toBe(phase.runId);
    expect(progress.percentage).toBe(phase.progress.percent);
    expect(progress.requiredAccepted).toBe(phase.progress.completed);
    expect(progress.pending).toBe(phase.progress.total - phase.progress.completed);
  });

  it('recomenda ação por prioridade: aprovações > bloqueios > agentes > quotas', () => {
    expect(
      recommendNextAction({
        pendingApprovals: 2,
        blockedTasks: 3,
        errorAgents: 1,
        criticalBudgets: 1,
      }),
    ).toBe('resolveApprovals');
    expect(
      recommendNextAction({
        pendingApprovals: 0,
        blockedTasks: 3,
        errorAgents: 1,
        criticalBudgets: 0,
      }),
    ).toBe('unblockTasks');
    expect(
      recommendNextAction({
        pendingApprovals: 0,
        blockedTasks: 0,
        errorAgents: 2,
        criticalBudgets: 0,
      }),
    ).toBe('recoverAgents');
    expect(
      recommendNextAction({
        pendingApprovals: 0,
        blockedTasks: 0,
        errorAgents: 0,
        criticalBudgets: 1,
      }),
    ).toBe('reviewQuotas');
    expect(
      recommendNextAction({
        pendingApprovals: 0,
        blockedTasks: 0,
        errorAgents: 0,
        criticalBudgets: 0,
      }),
    ).toBe('reviewPhase');
  });

  it('métricas de fábrica: online, entregas, fila e capacidade parada', () => {
    const base = {
      definitionId: 'd',
      projectId: 'p',
      currentTaskId: null,
      modelId: null,
      lease: null,
      lastHeartbeatAt: null,
    } as const;
    const mk = (id: string, state: Agent['state'], done: number): Agent => ({
      ...base,
      id,
      name: `Agente ${id}`,
      state,
      metrics: { tasksCompleted: done, tokensInput: 0, tokensOutput: 0, costUsd: 0, uptimeMs: 0 },
    });
    const agents = [
      mk('a', 'working', 5),
      mk('b', 'idle', 3),
      mk('c', 'error', 2),
      mk('d', 'outOfQuota', 0),
    ];
    const taskCounts = { ...countTasksByState([]), backlog: 4, development: 2, done: 6 };
    const m = factoryAgentMetrics(agents, taskCounts);
    expect(m.online).toBe(2); // working + idle (erro/sem cota fora)
    expect(m.problems).toBe(2);
    expect(m.tasksDone).toBe(10); // 5+3+2+0
    expect(m.tasksTodo).toBe(6); // total(12) - done(6)
    expect(m.working.map((a) => a.id)).toEqual(['a']);
    expect(m.attention.map((a) => a.id)).toEqual(['c', 'd']);
  });

  it('resume equipes com mais de 30 agentes por núcleo e mantém uma única chefe', () => {
    const chief = fixtures.data.agents.find(
      (agent) => agent.id === fixtures.data.projects[0].chiefAgentId,
    )!;
    const specialistDefinitions = new Set(
      fixtures.data['agent-definitions']
        .filter((definition) => definition.role === 'specialist')
        .map((definition) => definition.id),
    );
    const specialist = fixtures.data.agents.find((agent) =>
      specialistDefinitions.has(agent.definitionId),
    )!;
    const expanded = [
      chief,
      ...Array.from({ length: 36 }, (_, index) => ({
        ...specialist,
        id: `specialist-${index}`,
        name: `Especialista ${index}`,
        state: index < 12 ? ('working' as const) : ('idle' as const),
      })),
    ];

    const summary = summarizeTeamActivity(
      expanded,
      fixtures.data['agent-definitions'],
      chief.id,
    );

    expect(summary.chief?.id).toBe(chief.id);
    expect(summary.nuclei.reduce((total, nucleus) => total + nucleus.total, 0)).toBe(36);
    expect(summary.working).toHaveLength(4);
    expect(summary.hiddenWorking).toBe(8);
  });

  it('classifica severidade de budget pelo limiar de alerta', () => {
    const account = budgets.find((b) => b.scope === 'account')!; // 80% com limiar 75%
    expect(budgetSeverity(account)).toBe('warning');
    const global = budgets.find((b) => b.scope === 'global')!; // 43% com limiar 80%
    expect(budgetSeverity(global)).toBe('ok');
    expect(budgetSeverity({ ...account, spentUsd: 60 })).toBe('critical');
  });
});

describe('isMeaningfulActivity', () => {
  it('mantém eventos de negócio e esconde vaivém técnico interno', () => {
    const ev = (action: string, targetType: string): AuditEvent => ({
      id: action,
      actorKind: 'system',
      actorId: null,
      action,
      targetType,
      targetId: null,
      detail: null,
      occurredAt: new Date().toISOString(),
    });
    // Significativos para o dono.
    expect(isMeaningfulActivity(ev('task.stateChanged', 'task'))).toBe(true);
    expect(isMeaningfulActivity(ev('document.approved', 'document'))).toBe(true);
    expect(isMeaningfulActivity(ev('conversation.created', 'conversation'))).toBe(true);
    // Ruído técnico interno — escondido.
    expect(isMeaningfulActivity(ev('chief.turnStateChanged', 'chiefTurn'))).toBe(false);
    expect(isMeaningfulActivity(ev('message.appended', 'message'))).toBe(false);
    expect(isMeaningfulActivity(ev('agent.heartbeat', 'heartbeat'))).toBe(false);
    expect(isMeaningfulActivity(ev('progress.updated', 'task'))).toBe(false);
  });
});

function BoardMarker() {
  const [params] = useSearchParams();
  return <p>BOARD state={params.get('state')}</p>;
}

function ChatMarker() {
  return <p>CHAT</p>;
}

function renderCockpit(bundle?: TestBundle) {
  return renderWithApi(
    <MemoryRouter initialEntries={['/cockpit']}>
      <Routes>
        <Route path="/cockpit" element={<CockpitPage />} />
        <Route path="/board" element={<BoardMarker />} />
        <Route path="/chat" element={<ChatMarker />} />
        <Route path="/approvals" element={<p>APPROVALS</p>} />
        <Route path="/projects" element={<p>PROJECTS</p>} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('CockpitPage', () => {
  beforeEach(() => {
    usePresentationStore.setState({ modeByProfile: {} });
  });

  it('encerra o loading e orienta criar projeto quando a base está vazia', async () => {
    const bundle = createTestBundle();
    const originalList = bundle.api.list.bind(bundle.api);
    vi.spyOn(bundle.api, 'list').mockImplementation((resource, query) => {
      if (resource === 'projects') return Promise.resolve({ items: [], nextCursor: null });
      return originalList(resource, query);
    });
    renderCockpit(bundle);

    expect(await screen.findByText(/crie o primeiro projeto/i)).toBeInTheDocument();
  });

  it('exibe só o progresso aceito e permite consultar as etapas no modo Negócio', async () => {
    const user = userEvent.setup();
    renderCockpit();

    expect(await screen.findAllByRole('progressbar', { name: 'Trabalho aceito' })).toHaveLength(1);
    expect(screen.getByRole('progressbar', { name: 'Trabalho aceito' })).toHaveAttribute(
      'aria-valuenow',
      '0',
    );
    expect(screen.queryByRole('progressbar', { name: 'Executado' })).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar', { name: 'Validado' })).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar', { name: 'Aprovado' })).not.toBeInTheDocument();

    expect(screen.getByRole('button', { name: 'Ver detalhes da etapa Validação' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByText('Qualidade')).toBeInTheDocument();
    expect(screen.getByText('Relatório de testes')).toBeInTheDocument();
    expect(screen.getByText('Evidências')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ver detalhes da etapa Planejamento' }));
    expect(screen.getByText('Plano de testes')).toBeInTheDocument();
  });

  it('mantém as três fontes separadas na visão técnica', () => {
    const progress = { executed: 80, validated: 50, approved: 20 };
    const evidence = progressEvidence([
      { ...tasks[0], progress: { executed: 80, validated: 50, approved: 20 } },
    ]);
    render(<ProgressTracks progress={progress} evidence={evidence} mode="technical" />);

    expect(screen.getByRole('progressbar', { name: 'Executado' })).toHaveAttribute(
      'aria-valuenow',
      '80',
    );
    expect(screen.getByRole('progressbar', { name: 'Validado' })).toHaveAttribute(
      'aria-valuenow',
      '50',
    );
    expect(screen.getByRole('progressbar', { name: 'Aprovado' })).toHaveAttribute(
      'aria-valuenow',
      '20',
    );
  });

  it('abre no quadro uma tarefa que está bloqueada', async () => {
    const user = userEvent.setup();
    renderCockpit();

    await user.click(
      await screen.findByRole('link', { name: /publicação em preparação/i }),
    );
    expect(await screen.findByText('BOARD state=')).toBeInTheDocument();
  });

  it('recomenda resolver aprovações e o CTA leva ao chat', async () => {
    const user = userEvent.setup();
    renderCockpit();

    expect(await screen.findByText(/documentos aguardando sua decisão/i)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /executar no chat/i }));
    expect(await screen.findByText('CHAT')).toBeInTheDocument();
  });

  it('mostra o dashboard de negócio sem vocabulário técnico', async () => {
    renderCockpit();

    // Aguarda o carregamento completo (cards de bloqueio só existem no fim).
    expect(await screen.findByText('publicação em preparação (sem acesso)')).toBeInTheDocument();
    expect(screen.queryByLabelText(/projeto ativo/i)).not.toBeInTheDocument();
    expect(screen.getByText('Aprovar verificação de Qualidade')).toBeInTheDocument();
    expect(screen.getByText('Quadro da Equipe')).toBeInTheDocument();
    expect(screen.getByText('Disponíveis').tagName).toBe('DT');
    expect(screen.getByText('58').tagName).toBe('DD');
    expect((await screen.findAllByText('Em admissão')).length).toBeGreaterThan(0);
    expect(screen.getByText('Produtividade da equipe')).toBeInTheDocument();
    expect(screen.getByText('Capacidade da equipe')).toBeInTheDocument();
    expect(screen.getByText('Atividade recente')).toBeInTheDocument();

    expect(document.body.textContent).not.toMatch(
      /\b(fleet|provedor|provider|modelo|model|token|tenant|slug|worktree|lease|fencing|heartbeat|gate|cota|deploy|staging|plugin)\b/iu,
    );
  });

  it('revela diagnósticos e as três trilhas apenas no modo técnico', async () => {
    const bundle = createTestBundle();
    usePresentationStore
      .getState()
      .requestMode(bundle.fixtures.meta.currentProfileId, 'technical');

    renderCockpit(bundle);

    expect((await screen.findAllByText('Claude · Anthropic')).length).toBeGreaterThan(0);
    expect(screen.getByRole('progressbar', { name: 'Executado' })).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Validado' })).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Aprovado' })).toBeInTheDocument();
  });
});

/* ---- Atividade recente: período + carregamento incremental (D-070) ---- */

function makeEvent(id: string, occurredAt: string, detail: string): AuditEvent {
  return {
    id,
    actorKind: 'user',
    actorId: null,
    action: 'task.created',
    targetType: 'task',
    targetId: null,
    detail,
    occurredAt,
  };
}

const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();

describe('filterActivityByPeriod', () => {
  const now = new Date('2026-07-19T12:00:00Z');
  const events = [
    makeEvent('e1', '2026-07-19T10:00:00Z', 'recente'),
    makeEvent('e2', '2026-07-17T11:00:00Z', 'dois dias'),
    makeEvent('e3', '2026-07-15T12:00:00Z', 'quatro dias'),
  ];

  it('recorta pela janela móvel: 2 dias fora de 24h, dentro de 3d', () => {
    expect(filterActivityByPeriod(events, '24h', now).map((e) => e.id)).toEqual(['e1']);
    expect(filterActivityByPeriod(events, '3d', now).map((e) => e.id)).toEqual(['e1', 'e2']);
    expect(filterActivityByPeriod(events, '7d', now)).toHaveLength(3);
  });

  it('fronteira da janela é inclusiva', () => {
    const edge = makeEvent('edge', '2026-07-18T12:00:00Z', 'exatamente 24h');
    expect(filterActivityByPeriod([edge], '24h', now)).toHaveLength(1);
  });
});

describe('ActivityFeed', () => {
  function renderFeed(events: AuditEvent[], mode: 'business' | 'technical' = 'business') {
    const sorted = [...events].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
    return render(
      <MemoryRouter>
        <ActivityFeed events={sorted} mode={mode} />
      </MemoryRouter>,
    );
  }

  it('filtra pelo período: evento de 2 dias some em 24h e aparece em 3 dias', async () => {
    const user = userEvent.setup();
    renderFeed([
      makeEvent('e1', hoursAgo(1), 'Evento recente'),
      makeEvent('e2', hoursAgo(50), 'Evento de dois dias'),
    ]);

    // Padrão: últimas 24 horas.
    expect(screen.getByRole('button', { name: 'Últimas 24 horas' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByText('Evento recente')).toBeInTheDocument();
    expect(screen.queryByText('Evento de dois dias')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Últimos 3 dias' }));
    expect(screen.getByText('Evento de dois dias')).toBeInTheDocument();
  });

  it('"carregar mais" revela o próximo lote de 8', async () => {
    const user = userEvent.setup();
    const events = Array.from({ length: 10 }, (_, i) =>
      makeEvent(`e${i}`, hoursAgo(i + 1), `Evento ${i + 1}`),
    );
    renderFeed(events);

    expect(screen.getByText('Evento 8')).toBeInTheDocument();
    expect(screen.queryByText('Evento 9')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /carregar mais 2/i }));
    expect(screen.getByText('Evento 10')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /carregar mais/i })).not.toBeInTheDocument();
    expect(screen.getByText('Exibindo 10 de 10')).toBeInTheDocument();
  });

  it('estado vazio do período orienta ampliar ou ir à Governança', () => {
    renderFeed([makeEvent('e1', hoursAgo(50), 'Evento antigo')], 'technical');

    expect(screen.getByText(/nenhuma atividade nas últimas 24 horas/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /ver histórico completo/i })).toHaveAttribute(
      'href',
      '/governance',
    );
  });

  it('trocar o período reinicia o carregamento incremental', async () => {
    const user = userEvent.setup();
    const events = Array.from({ length: 12 }, (_, i) =>
      makeEvent(`e${i}`, hoursAgo(i + 1), `Evento ${i + 1}`),
    );
    renderFeed(events);

    await user.click(screen.getByRole('button', { name: /carregar mais 4/i }));
    expect(screen.getByText('Evento 12')).toBeInTheDocument();

    // Troca de período: volta ao primeiro lote.
    await user.click(screen.getByRole('button', { name: 'Últimos 7 dias' }));
    expect(screen.queryByText('Evento 12')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /carregar mais 4/i })).toBeInTheDocument();
  });
  /* ---- Humanização dos eventos (§12) ---- */

  it('mostra mensagem humana no lugar do código cru, mantendo o objeto', () => {
    renderFeed([makeEvent('e1', hoursAgo(1), 'Projeto Poseidon')], 'technical');

    // Rótulo humano da ação conhecida `task.created`…
    expect(screen.getByText('Tarefa criada')).toBeInTheDocument();
    // …com o objeto afetado ainda visível (humanizar não esconde o alvo).
    expect(screen.getByText('Projeto Poseidon')).toBeInTheDocument();
    // O código cru NÃO aparece na leitura normal.
    expect(screen.queryByText('task.created')).not.toBeInTheDocument();
  });

  it('expõe o código técnico apenas no disclosure "Ver detalhes"', async () => {
    const user = userEvent.setup();
    renderFeed([makeEvent('e1', hoursAgo(1), 'Projeto Poseidon')], 'technical');

    const toggle = screen.getByRole('button', { name: 'Ver detalhes' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');

    await user.click(toggle);
    expect(screen.getByText('task.created')).toBeInTheDocument();
    expect(screen.getByText('task')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ocultar detalhes' })).toBeInTheDocument();
  });

  it('ação desconhecida degrada para o detalhe do servidor, sem inventar texto', () => {
    const unknown = {
      ...makeEvent('e1', hoursAgo(1), 'Detalhe do servidor'),
      action: 'x.naoMapeado',
    };
    renderFeed([unknown]);

    expect(screen.getByText('Detalhe do servidor')).toBeInTheDocument();
    expect(screen.queryByText('x.naoMapeado')).not.toBeInTheDocument();
  });

  it('oferece link para o objeto quando existe tela correspondente', () => {
    renderFeed([makeEvent('e1', hoursAgo(1), 'Tarefa X')]);
    expect(screen.getByRole('link', { name: 'Abrir' })).toHaveAttribute('href', '/board');
  });
});
