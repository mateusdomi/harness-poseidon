import { render, screen } from '@testing-library/react';

import '@/i18n';

import type { Agent } from '@/api';

import { AgentsUtilizationChart } from '../components/agents-utilization-chart';

/** Agente mínimo: o gráfico só lê id/name/metrics.tasksCompleted. */
function agent(id: string, name: string, tasksCompleted: number): Agent {
  return {
    id,
    name,
    metrics: { tasksCompleted, tokensInput: 0, tokensOutput: 0, costUsd: 0, uptimeMs: 0 },
  } as unknown as Agent;
}

describe('AgentsUtilizationChart (G-CHARTS)', () => {
  it('grafica tarefas concluídas por agente, ordenado por produção', () => {
    render(
      <AgentsUtilizationChart
        agents={[agent('a1', 'Ana', 3), agent('a2', 'Bruno', 8), agent('a3', 'Chief', 0)]}
      />,
    );

    expect(screen.getByText('Bruno')).toBeInTheDocument();
    expect(screen.getByText('Ana')).toBeInTheDocument();
    expect(screen.getByText('8')).toBeInTheDocument();
    // Rótulo direto acessível por agente.
    expect(
      screen.getByLabelText('Bruno: 8 tarefas concluídas'),
    ).toBeInTheDocument();
  });

  it('mostra empty-state honesto quando a equipe não concluiu tarefas', () => {
    render(<AgentsUtilizationChart agents={[agent('a1', 'Ana', 0), agent('a2', 'Bruno', 0)]} />);
    expect(screen.getByText(/ainda não concluiu nenhuma tarefa/i)).toBeInTheDocument();
  });
});
