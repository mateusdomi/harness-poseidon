import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { ProjectList } from '@/features/projects/components/project-list';
import {
  EMPTY_FILTERS,
  filterProjects,
} from '@/features/projects/components/project-filters';
import { buildFixtures } from '@/api';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = buildFixtures(42);
const projects = fixtures.data.projects;
const organizations = fixtures.data.organizations;

describe('filterProjects', () => {
  it('filtra por texto em nome, sigla e descrição', () => {
    const result = filterProjects(projects, { ...EMPTY_FILTERS, query: 'pagamentos' });
    expect(result).toHaveLength(1);
    expect(result[0].name).toBe('API de Pagamentos');
  });

  it('filtra por organização, criticidade e estado', () => {
    const byOrg = filterProjects(projects, {
      ...EMPTY_FILTERS,
      organizationId: organizations[0].id,
    });
    expect(byOrg.every((p) => p.organizationId === organizations[0].id)).toBe(true);

    const byCriticality = filterProjects(projects, { ...EMPTY_FILTERS, criticality: 'critical' });
    expect(byCriticality.every((p) => p.criticality === 'critical')).toBe(true);
    expect(byCriticality.length).toBeGreaterThan(0);

    const byState = filterProjects(projects, { ...EMPTY_FILTERS, state: 'paused' });
    expect(byState.every((p) => p.state === 'paused')).toBe(true);
    expect(byState.length).toBeGreaterThan(0);
  });

  it('combina busca e filtros', () => {
    const result = filterProjects(projects, {
      ...EMPTY_FILTERS,
      query: 'poseidon',
      state: 'paused',
    });
    expect(result).toHaveLength(0);
  });
});

describe('ProjectList', () => {
  function renderList() {
    return renderWithApi(
      <ProjectList
        projects={projects}
        organizations={organizations}
        onSelect={() => {}}
        onCreateNew={() => {}}
      />,
    );
  }

  it('lista todos os projetos e filtra pela busca', async () => {
    const user = userEvent.setup();
    renderList();

    expect(screen.getByText('Poseidon Frontend')).toBeInTheDocument();
    expect(screen.getByText('API de Pagamentos')).toBeInTheDocument();

    await user.type(screen.getByLabelText(/buscar projetos/i), 'pagamentos');

    expect(screen.queryByText('Poseidon Frontend')).not.toBeInTheDocument();
    expect(screen.getByText('API de Pagamentos')).toBeInTheDocument();
  });

  it('filtra por estado e mostra mensagem quando nada corresponde', async () => {
    const user = userEvent.setup();
    renderList();

    await user.selectOptions(screen.getByLabelText(/estado/i), 'archived');
    expect(screen.getByText(/nenhum projeto encontrado/i)).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText(/estado/i), 'paused');
    expect(screen.getByText('API de Pagamentos')).toBeInTheDocument();
    expect(screen.queryByText('Poseidon Frontend')).not.toBeInTheDocument();
  });

  it('exibe estado, criticidade e última atividade nos cards', () => {
    renderList();

    expect(screen.getAllByText(/última atividade/i).length).toBe(projects.length);
    // "Pausado"/"Crítica" também aparecem como <option> dos filtros
    expect(screen.getAllByText('Pausado').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Crítica').length).toBeGreaterThan(0);
  });
});

describe('FR-4 — filtro de arquivados', () => {
  const archivedProject = { ...projects[0], id: '01JLEGACY0000000000000000', name: 'Projeto Legado', state: 'archived' as const };
  const mixed = [...projects, archivedProject];

  it('filterProjects: ativos, arquivados ou todos', () => {
    expect(filterProjects(mixed, { ...EMPTY_FILTERS, archive: '' })).toHaveLength(4);
    const active = filterProjects(mixed, { ...EMPTY_FILTERS, archive: 'active' });
    expect(active).toHaveLength(3);
    expect(active.every((p) => p.state !== 'archived')).toBe(true);
    const archived = filterProjects(mixed, { ...EMPTY_FILTERS, archive: 'archived' });
    expect(archived).toEqual([archivedProject]);
  });

  it('lista mostra projetos arquivados pelo filtro', async () => {
    const user = userEvent.setup();
    renderWithApi(
      <ProjectList
        projects={mixed}
        organizations={organizations}
        onSelect={() => {}}
        onCreateNew={() => {}}
      />,
    );

    // Padrão (todos): os 3 projetos aparecem, incluindo o arquivado.
    expect(screen.getByText('Projeto Legado')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText(/arquivamento/i), 'active');
    expect(screen.queryByText('Projeto Legado')).not.toBeInTheDocument();
    expect(screen.getByText('API de Pagamentos')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText(/arquivamento/i), 'archived');
    expect(screen.getByText('Projeto Legado')).toBeInTheDocument();
    expect(screen.queryByText('API de Pagamentos')).not.toBeInTheDocument();
  });
});
