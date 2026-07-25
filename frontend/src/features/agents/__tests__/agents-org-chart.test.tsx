import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import type { Agent } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import { AgentDetail } from '@/features/agents/components/agent-detail';
import { AgentOrgChart } from '@/features/agents/components/agent-org-chart';
import {
  agentModelRoute,
  distinctTeams,
  filterSpecialists,
  groupSpecialistsByTeam,
  teamOfProject,
} from '@/features/agents/lib/agents-derive';
import AgentsPage from '@/features/agents/pages/agents-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderAgents() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/agents']}>
      <Routes>
        <Route path="/agents" element={<AgentsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

const fixtures = createTestBundle().fixtures.data;
const projetoPoseidon = fixtures.projects[0];
const definicoes = fixtures['agent-definitions'];
const equipe = teamOfProject(projetoPoseidon, fixtures.agents);

describe('groupSpecialistsByTeam', () => {
  it('agrupa pelo time da definição, em ordem alfabética', () => {
    const groups = groupSpecialistsByTeam(equipe.specialists, definicoes);

    expect(groups.map((group) => group.team)).toEqual(['Design', 'Engenharia', 'Qualidade']);
    expect(groups[0].agents.map((agent) => agent.name)).toEqual(['Nina (Protótipos)']);
    expect(groups[1].agents.map((agent) => agent.name)).toEqual([
      'Iara (Backend)',
      'Otávio (Frontend)',
    ]);
    expect(groups[2].agents.map((agent) => agent.name)).toEqual(['Lia (Testes)', 'Rui (Revisor)']);
  });

  it('definição sem time forma o grupo "geral" (null), sempre por último', () => {
    const defsSemTime = definicoes.map((definition) =>
      definition.key === 'prototype-designer' ? { ...definition, team: null } : definition,
    );
    const groups = groupSpecialistsByTeam(equipe.specialists, defsSemTime);

    expect(groups.map((group) => group.team)).toEqual(['Engenharia', 'Qualidade', null]);
    expect(groups.at(-1)?.agents.map((agent) => agent.name)).toEqual(['Nina (Protótipos)']);
  });
});

describe('distinctTeams', () => {
  it('lista times distintos em ordem alfabética e sinaliza o grupo "geral"', () => {
    expect(distinctTeams(equipe.specialists, definicoes)).toEqual({
      teams: ['Design', 'Engenharia', 'Qualidade'],
      hasGeneral: false,
    });

    const defsSemTime = definicoes.map((definition) =>
      definition.key === 'prototype-designer' ? { ...definition, team: null } : definition,
    );
    expect(distinctTeams(equipe.specialists, defsSemTime)).toEqual({
      teams: ['Engenharia', 'Qualidade'],
      hasGeneral: true,
    });
  });
});

describe('filterSpecialists', () => {
  it('filtra por estado da instância', () => {
    expect(
      filterSpecialists(equipe.specialists, definicoes, 'working', 'all').map(
        (agent) => agent.name,
      ),
    ).toEqual(['Iara (Backend)']);
  });

  it('filtra por time da definição e combina com estado', () => {
    expect(
      filterSpecialists(equipe.specialists, definicoes, 'all', 'Qualidade').map(
        (agent) => agent.name,
      ),
    ).toEqual(['Lia (Testes)', 'Rui (Revisor)']);
    expect(filterSpecialists(equipe.specialists, definicoes, 'working', 'Qualidade')).toEqual([]);
  });

  it('team "none" filtra o grupo "geral" (definição sem time)', () => {
    const defsSemTime = definicoes.map((definition) =>
      definition.key === 'prototype-designer' ? { ...definition, team: null } : definition,
    );
    expect(
      filterSpecialists(equipe.specialists, defsSemTime, 'all', 'none').map(
        (agent) => agent.name,
      ),
    ).toEqual(['Nina (Protótipos)']);
  });
});

describe('agentModelRoute', () => {
  const iara = fixtures.agents.find((agent) => agent.name === 'Iara (Backend)')!;
  const defBackend = definicoes.find((definition) => definition.id === iara.definitionId)!;

  it('modelId nulo: modelo em uso é o padrão da definição (roteamento automático)', () => {
    const route = agentModelRoute(iara, defBackend, fixtures.models);

    expect(route.source).toBe('default');
    expect(route.current?.displayName).toBe('Claude Sonnet 4');
    expect(route.defaultModel?.displayName).toBe('Claude Sonnet 4');
    expect(route.fallbacks.map((model) => model.displayName)).toEqual(['GPT-4o']);
  });

  it('modelId preenchido: override humano, modelo em uso é o override', () => {
    const gpt4o = fixtures.models.find((model) => model.displayName === 'GPT-4o')!;
    const route = agentModelRoute({ ...iara, modelId: gpt4o.id }, defBackend, fixtures.models);

    expect(route.source).toBe('override');
    expect(route.current?.displayName).toBe('GPT-4o');
    expect(route.defaultModel?.displayName).toBe('Claude Sonnet 4');
  });
});

function renderDetail(agent: Agent) {
  const bundle = createTestBundle();
  const data = bundle.fixtures.data;
  return renderWithApi(
    <MemoryRouter>
      <AgentDetail
        agent={agent}
        definition={data['agent-definitions'].find((d) => d.id === agent.definitionId) ?? null}
        skills={data.skills}
        tools={data.tools}
        models={data.models}
        tasks={data.tasks}
        attempts={data.attempts}
        auditEvents={data['audit-events']}
        now={new Date('2026-07-17T12:00:00Z')}
        onClose={() => {}}
      />
    </MemoryRouter>,
    bundle,
  );
}

describe('AgentDetail — modelo e rota', () => {
  const iara = fixtures.agents.find((agent) => agent.name === 'Iara (Backend)')!;

  it('sem override: origem "padrão da definição (roteamento automático)" + fallbacks', async () => {
    renderDetail(iara);
    const dialog = await screen.findByRole('dialog', { name: 'Iara (Backend)' });

    expect(within(dialog).getByText('Modelo e rota')).toBeInTheDocument();
    expect(within(dialog).getByText('Modelo em uso')).toBeInTheDocument();
    expect(
      within(dialog).getByText('Padrão da definição (roteamento automático)'),
    ).toBeInTheDocument();
    expect(within(dialog).queryByText('Override humano')).not.toBeInTheDocument();
    expect(within(dialog).getByText('Modelo padrão da definição')).toBeInTheDocument();
    expect(within(dialog).getByText('Fallbacks configurados')).toBeInTheDocument();
    expect(within(dialog).getByText('Esforço')).toBeInTheDocument();
    expect(within(dialog).getByText('Alto')).toBeInTheDocument();
    expect(within(dialog).getByText(/valor enviado ao provedor: high/)).toBeInTheDocument();
    expect(within(dialog).getByText('Impacto estimado')).toBeInTheDocument();
    expect(within(dialog).getByText(/Tarifa-base por 1k tokens/)).toBeInTheDocument();
    // Fallback da definição backend: GPT-4o (também listado em modelos compatíveis).
    expect(within(dialog).getAllByText('GPT-4o').length).toBeGreaterThanOrEqual(2);
  });

  it('com override (modelId preenchido): badge "Override humano" e modelo em uso é o override', async () => {
    const gpt4o = fixtures.models.find((model) => model.displayName === 'GPT-4o')!;
    renderDetail({ ...iara, modelId: gpt4o.id });
    const dialog = await screen.findByRole('dialog', { name: 'Iara (Backend)' });

    expect(within(dialog).getByText('Override humano')).toBeInTheDocument();
    expect(
      within(dialog).queryByText('Padrão da definição (roteamento automático)'),
    ).not.toBeInTheDocument();
    // Modelo em uso (GPT-4o) ao lado do badge de override.
    const emUso = within(dialog).getByText('Modelo em uso').parentElement!;
    expect(within(emUso).getByText('GPT-4o')).toBeInTheDocument();
  });
});

describe('AgentOrgChart — grupos por time', () => {
  it('renderiza o grupo "Geral" para especialista cuja definição não tem time', () => {
    const nina = fixtures.agents.find((agent) => agent.name === 'Nina (Protótipos)')!;
    const defsSemTime = definicoes.map((definition) =>
      definition.key === 'prototype-designer' ? { ...definition, team: null } : definition,
    );

    renderWithApi(
      <MemoryRouter>
        <AgentOrgChart
          team={{ chief: null, specialists: [nina] }}
          definitions={defsSemTime}
          skills={fixtures.skills}
          tasks={fixtures.tasks}
          attempts={fixtures.attempts}
          onSelect={() => {}}
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole('heading', { name: 'Geral' })).toBeInTheDocument();
    expect(screen.getByText('Nina (Protótipos)')).toBeInTheDocument();
  });
});

describe('AgentsPage — organograma por time e filtros', () => {
  it('exibe títulos de grupo por time (Engenharia, Qualidade, Design)', async () => {
    renderAgents();
    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });

    for (const teamName of ['Engenharia', 'Qualidade', 'Design']) {
      expect(within(tree).getByRole('heading', { name: teamName })).toBeInTheDocument();
    }
  });

  it('filtra especialistas por estado e o chefe permanece', async () => {
    const user = userEvent.setup();
    renderAgents();
    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });

    await user.selectOptions(screen.getByLabelText('Estado'), 'working');

    expect(within(tree).getByText('Iara (Backend)')).toBeInTheDocument();
    expect(within(tree).getByText('Bruna Magalhães — Poseidon Frontend')).toBeInTheDocument();
    expect(within(tree).queryByText('Otávio (Frontend)')).not.toBeInTheDocument();
    expect(within(tree).queryByText('Lia (Testes)')).not.toBeInTheDocument();
    expect(within(tree).queryByText('Rui (Revisor)')).not.toBeInTheDocument();
    expect(within(tree).queryByText('Nina (Protótipos)')).not.toBeInTheDocument();
  });

  it('filtra especialistas por time', async () => {
    const user = userEvent.setup();
    renderAgents();
    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });

    await user.selectOptions(screen.getByLabelText('Time'), 'Qualidade');

    expect(within(tree).getByText('Lia (Testes)')).toBeInTheDocument();
    expect(within(tree).getByText('Rui (Revisor)')).toBeInTheDocument();
    expect(within(tree).queryByText('Iara (Backend)')).not.toBeInTheDocument();
    expect(within(tree).queryByRole('heading', { name: 'Engenharia' })).not.toBeInTheDocument();
    expect(within(tree).getByRole('heading', { name: 'Qualidade' })).toBeInTheDocument();
  });

  it('filtro sem correspondência mostra estado vazio i18n e o chefe permanece', async () => {
    const user = userEvent.setup();
    renderAgents();
    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });

    await user.selectOptions(screen.getByLabelText('Time'), 'Qualidade');
    await user.selectOptions(screen.getByLabelText('Estado'), 'working');

    expect(screen.getByText('Nenhum especialista encontrado')).toBeInTheDocument();
    expect(within(tree).getByText('Bruna Magalhães — Poseidon Frontend')).toBeInTheDocument();
    expect(within(tree).queryByText('Iara (Backend)')).not.toBeInTheDocument();
  });

  it('card tem link "Configurar definição" apontando para o orquestrador', async () => {
    renderAgents();
    const tree = await screen.findByRole('group', {
      name: 'Organograma da equipe do projeto',
    });

    const card = within(tree).getByText('Iara (Backend)').closest('li')!;
    const link = within(card as HTMLElement).getByRole('link', {
      name: 'Configurar definição',
    });
    const defBackend = definicoes.find((definition) => definition.key === 'backend-engineer')!;
    expect(link).toHaveAttribute(
      'href',
      `/orchestrator?tab=definitions&definition=${defBackend.id}`,
    );
  });
});
