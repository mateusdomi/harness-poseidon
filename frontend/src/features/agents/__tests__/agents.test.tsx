import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  agentHistory,
  deriveAgentMetrics,
  effectiveModelId,
  teamOfProject,
} from '@/features/agents/lib/agents-derive';
import AgentsPage from '@/features/agents/pages/agents-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderAgents() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/agents']}>
      <Routes>
        <Route path="/agents" element={<AgentsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

const fixtures = createTestBundle().fixtures.data;
const projetoPoseidon = fixtures.projects[0];

describe('agents-derive', () => {
  it('monta a equipe: chefe pelo chiefAgentId e especialistas ordenados por nome', () => {
    const team = teamOfProject(projetoPoseidon, fixtures.agents);
    expect(team.chief?.id).toBe(projetoPoseidon.chiefAgentId);
    expect(team.specialists).toHaveLength(5);
    expect(team.specialists.map((agent) => agent.id)).not.toContain(team.chief?.id);
    const nomes = team.specialists.map((agent) => agent.name);
    expect(nomes).toEqual([...nomes].sort((a, b) => a.localeCompare(b)));
  });

  it('deriva métricas: concluídas do contrato, aprovadas e retrabalho de tasks/attempts', () => {
    const iara = fixtures.agents.find((agent) => agent.name === 'Iara (Backend)')!;
    const metrics = deriveAgentMetrics(iara, fixtures.tasks, fixtures.attempts);

    expect(metrics.tasksCompleted).toBe(iara.metrics.tasksCompleted);
    expect(metrics.approvedInReview).toBe(
      fixtures.tasks.filter(
        (task) => task.assigneeAgentId === iara.id && task.state === 'done',
      ).length,
    );
    expect(metrics.rework).toBe(
      fixtures.tasks.filter(
        (task) => task.assigneeAgentId === iara.id && task.state === 'corrections',
      ).length +
        fixtures.attempts.filter(
          (attempt) => attempt.agentId === iara.id && attempt.state === 'failed',
        ).length,
    );
  });

  it('histórico une auditoria (ator ou alvo) e attempts, do mais recente ao mais antigo', () => {
    const lia = fixtures.agents.find((agent) => agent.name === 'Lia (Testes)')!;
    const history = agentHistory(lia.id, fixtures['audit-events'], fixtures.attempts);

    // Auditoria onde Lia é ator e alvo (agent.error da suíte E2E).
    expect(
      history.some((entry) => entry.kind === 'audit' && entry.event.action === 'agent.error'),
    ).toBe(true);
    for (let index = 1; index < history.length; index += 1) {
      const anterior = history[index - 1];
      const atual = history[index];
      const dataAnterior =
        anterior.kind === 'audit' ? anterior.event.occurredAt : anterior.attempt.startedAt;
      const dataAtual = atual.kind === 'audit' ? atual.event.occurredAt : atual.attempt.startedAt;
      expect(Date.parse(dataAnterior)).toBeGreaterThanOrEqual(Date.parse(dataAtual));
    }
  });

  it('modelo efetivo: override da instância ou padrão da definição', () => {
    const iara = fixtures.agents.find((agent) => agent.name === 'Iara (Backend)')!;
    const definicao = fixtures['agent-definitions'].find(
      (definition) => definition.id === iara.definitionId,
    )!;
    expect(effectiveModelId(iara, definicao)).toBe(definicao.defaultModelId);
    expect(effectiveModelId({ ...iara, modelId: '01JOVERRIDE000000000000000' }, definicao)).toBe(
      '01JOVERRIDE000000000000000',
    );
  });
});

describe('AgentsPage', () => {
  it('exibe o organograma da equipe do projeto ativo com chefe e especialistas', async () => {
    renderAgents();

    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });
    expect(within(tree).getByText('Chefe — Poseidon Frontend')).toBeInTheDocument();
    expect(within(tree).getByText('Iara (Backend)')).toBeInTheDocument();
    expect(within(tree).getByText('Nina (Protótipos)')).toBeInTheDocument();

    // Badge de estado vem do i18n de status (agente em erro do fixture).
    expect(within(tree).getByText('Em erro')).toBeInTheDocument();
    expect(within(tree).getByText('Sem cota')).toBeInTheDocument();

    // Métricas com rótulos e ajuda (definição visível via title/aria-label).
    expect(
      within(tree).getAllByText('Tarefas concluídas').length,
    ).toBeGreaterThan(0);
    expect(
      within(tree).getAllByLabelText(/Total acumulado de tarefas concluídas/).length,
    ).toBeGreaterThan(0);
  });

  it('abre o detalhe do agente com definição, skills, ferramentas, modelos e histórico', async () => {
    const user = userEvent.setup();
    renderAgents();

    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });
    const card = within(tree).getByText('Lia (Testes)').closest('li')!;
    await user.click(within(card as HTMLElement).getByRole('button', { name: 'Ver detalhes' }));

    const dialog = await screen.findByRole('dialog', { name: 'Lia (Testes)' });
    expect(within(dialog).getByText('Executa suítes de teste e reporta evidências.'))
      .toBeInTheDocument();
    expect(within(dialog).getByText('Testes')).toBeInTheDocument();
    expect(within(dialog).getByText('Terminal')).toBeInTheDocument();
    // 'GPT-4o mini' aparece na lista de modelos compatíveis E na seção
    // "Modelo e rota" (modelo em uso da instância).
    expect(within(dialog).getAllByText('GPT-4o mini').length).toBeGreaterThanOrEqual(2);
    expect(within(dialog).getByText('Padrão')).toBeInTheDocument();
    // Histórico: auditoria em que Lia é ator/alvo.
    expect(within(dialog).getByText('agent.error')).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Cancelar' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Lia (Testes)' })).not.toBeInTheDocument();
    });
  });

  it('agent.statusChanged no stream global atualiza o estado do chefe em tempo real', async () => {
    const { bundle } = renderAgents();

    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });
    // Chefe do projeto Poseidon começa "waiting" (o revisor Rui também).
    expect(within(tree).getAllByText('Aguardando')).toHaveLength(2);

    // Retomar a orquestração emite agent.statusChanged (waiting → idle).
    await act(async () => {
      await bundle.api.resumeChief(projetoPoseidon.id);
    });

    await waitFor(() => {
      expect(within(tree).getAllByText('Aguardando')).toHaveLength(1);
    });
  });

  // Por último: a seleção do projeto ativo é persistida no store da sessão.
  it('troca o projeto ativo e mostra a equipe correspondente', async () => {
    const user = userEvent.setup();
    renderAgents();

    await screen.findByRole('group', { name: 'Organograma da equipe do projeto' });

    await user.selectOptions(
      screen.getByLabelText('Projeto ativo'),
      fixtures.projects[1].id,
    );

    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });
    expect(within(tree).getByText('Chefe — API de Pagamentos')).toBeInTheDocument();
    expect(within(tree).queryByText('Iara (Backend)')).not.toBeInTheDocument();
  });
});
