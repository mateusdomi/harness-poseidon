import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { buildFixtures } from '@/api';
import CockpitPage from '@/features/cockpit/pages/cockpit-page';
import {
  aggregateProgress,
  budgetSeverity,
  countTasksByState,
  currentPhase,
  recommendNextAction,
  tasksOfPhase,
} from '@/features/cockpit/lib/cockpit-derive';
import { renderWithApi } from '@/test/render-with-providers';

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

function renderCockpit() {
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
  );
}

describe('CockpitPage', () => {
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
