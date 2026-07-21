import { screen } from '@testing-library/react';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router-dom';
import { vi } from 'vitest';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { buildFixtures, type AuditEvent } from '@/api';
import { ActivityFeed } from '@/features/cockpit/components/activity-feed';
import CockpitPage from '@/features/cockpit/pages/cockpit-page';
import {
  aggregateProgress,
  budgetSeverity,
  countTasksByState,
  currentPhase,
  filterActivityByPeriod,
  recommendNextAction,
  tasksOfPhase,
} from '@/features/cockpit/lib/cockpit-derive';
import { renderWithApi } from '@/test/render-with-providers';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';

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

  it('recomenda ação por prioridade: aprovações > bloqueios > agentes > quotas', () => {
    expect(
      recommendNextAction({ pendingApprovals: 2, blockedTasks: 3, errorAgents: 1, criticalBudgets: 1 }),
    ).toBe('resolveApprovals');
    expect(
      recommendNextAction({ pendingApprovals: 0, blockedTasks: 3, errorAgents: 1, criticalBudgets: 0 }),
    ).toBe('unblockTasks');
    expect(
      recommendNextAction({ pendingApprovals: 0, blockedTasks: 0, errorAgents: 2, criticalBudgets: 0 }),
    ).toBe('recoverAgents');
    expect(
      recommendNextAction({ pendingApprovals: 0, blockedTasks: 0, errorAgents: 0, criticalBudgets: 1 }),
    ).toBe('reviewQuotas');
    expect(
      recommendNextAction({ pendingApprovals: 0, blockedTasks: 0, errorAgents: 0, criticalBudgets: 0 }),
    ).toBe('reviewPhase');
  });

  it('classifica severidade de budget pelo limiar de alerta', () => {
    const account = budgets.find((b) => b.scope === 'account')!; // 80% com limiar 75%
    expect(budgetSeverity(account)).toBe('warning');
    const global = budgets.find((b) => b.scope === 'global')!; // 43% com limiar 80%
    expect(budgetSeverity(global)).toBe('ok');
    expect(budgetSeverity({ ...account, spentUsd: 60 })).toBe('critical');
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
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/cockpit']}>
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
  it('encerra o loading e orienta criar projeto quando a base está vazia', async () => {
    const bundle = createTestBundle();
    const originalList = bundle.api.list.bind(bundle.api);
    vi.spyOn(bundle.api, 'list').mockImplementation((resource, query) => {
      if (resource === 'projects') return Promise.resolve({ items: [], nextCursor: null });
      return originalList(resource, query);
    });
    renderCockpit(bundle);

    expect(await screen.findByText(/crie o primeiro projeto/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/carregando/i)).not.toBeInTheDocument();
  });

  it('renderiza as três trilhas separadas na fase e no global', async () => {
    renderCockpit();

    // Fase atual (Validação) + progresso global: 2 barras por trilha.
    expect(await screen.findAllByRole('progressbar', { name: 'Executado' })).toHaveLength(2);
    expect(screen.getAllByRole('progressbar', { name: 'Validado' })).toHaveLength(2);
    expect(screen.getAllByRole('progressbar', { name: 'Aprovado' })).toHaveLength(2);

    // Fase atual com gate associado.
    expect(screen.getByText('Validação')).toBeInTheDocument();
    expect(screen.getByText('Gate de Qualidade')).toBeInTheDocument();
  });

  it('renderiza contadores por estado e navega para o quadro filtrado ao clicar', async () => {
    const user = userEvent.setup();
    renderCockpit();

    const blockedButton = await screen.findByRole('button', { name: /bloqueada/i });
    expect(blockedButton).toHaveTextContent('3');

    await user.click(blockedButton);
    expect(await screen.findByText('BOARD state=blocked')).toBeInTheDocument();
  });

  it('recomenda resolver aprovações e o CTA leva ao chat', async () => {
    const user = userEvent.setup();
    renderCockpit();

    expect(
      await screen.findByText(/aprovações aguardando sua decisão/i),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /executar no chat/i }));
    expect(await screen.findByText('CHAT')).toBeInTheDocument();
  });

  it('mostra seletor de projeto, bloqueios, aprovações, agentes e atividade', async () => {
    renderCockpit();

    // Aguarda o carregamento completo (cards de bloqueio só existem no fim).
    expect(await screen.findByText('Deploy em staging (sem credencial)')).toBeInTheDocument();
    expect(screen.getByLabelText(/projeto ativo/i)).toBeInTheDocument();
    expect(screen.getByText('Aprovar Gate de Qualidade')).toBeInTheDocument();
    expect(screen.getByText('Saúde dos agentes')).toBeInTheDocument();
    expect(screen.getByText('Cotas críticas')).toBeInTheDocument();
    expect(screen.getByText('Atividade recente')).toBeInTheDocument();
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
  function renderFeed(events: AuditEvent[]) {
    const sorted = [...events].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
    return render(
      <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS}>
        <ActivityFeed events={sorted} />
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
    renderFeed([makeEvent('e1', hoursAgo(50), 'Evento antigo')]);

    expect(screen.getByText(/nenhuma atividade nas últimas 24 horas/i)).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: /ver histórico completo/i }),
    ).toHaveAttribute('href', '/governance');
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
    renderFeed([makeEvent('e1', hoursAgo(1), 'Projeto Poseidon')]);

    // Rótulo humano da ação conhecida `task.created`…
    expect(screen.getByText('Tarefa criada')).toBeInTheDocument();
    // …com o objeto afetado ainda visível (humanizar não esconde o alvo).
    expect(screen.getByText('Projeto Poseidon')).toBeInTheDocument();
    // O código cru NÃO aparece na leitura normal.
    expect(screen.queryByText('task.created')).not.toBeInTheDocument();
  });

  it('expõe o código técnico apenas no disclosure "Ver detalhes"', async () => {
    const user = userEvent.setup();
    renderFeed([makeEvent('e1', hoursAgo(1), 'Projeto Poseidon')]);

    const toggle = screen.getByRole('button', { name: 'Ver detalhes' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');

    await user.click(toggle);
    expect(screen.getByText('task.created')).toBeInTheDocument();
    expect(screen.getByText('task')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ocultar detalhes' })).toBeInTheDocument();
  });

  it('ação desconhecida degrada para o detalhe do servidor, sem inventar texto', () => {
    const unknown = { ...makeEvent('e1', hoursAgo(1), 'Detalhe do servidor'), action: 'x.naoMapeado' };
    renderFeed([unknown]);

    expect(screen.getByText('Detalhe do servidor')).toBeInTheDocument();
    expect(screen.queryByText('x.naoMapeado')).not.toBeInTheDocument();
  });

  it('oferece link para o objeto quando existe tela correspondente', () => {
    renderFeed([makeEvent('e1', hoursAgo(1), 'Tarefa X')]);
    expect(screen.getByRole('link', { name: 'Abrir' })).toHaveAttribute('href', '/board');
  });
});
