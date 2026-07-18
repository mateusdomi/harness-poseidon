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
