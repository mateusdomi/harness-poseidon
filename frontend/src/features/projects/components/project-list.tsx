import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Organization, Priority, Project, ProjectState } from '@/api';
import { PRIORITIES, PROJECT_STATES } from '@/api';
import { Button, Card, CardContent, Input, Select } from '@/design-system';
import { ProjectCard } from '@/features/projects/components/project-card';
import {
  EMPTY_FILTERS,
  filterProjects,
  type ProjectListFilters,
} from '@/features/projects/components/project-filters';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';

export interface ProjectListProps {
  projects: Project[];
  organizations: Organization[];
  onSelect: (project: Project) => void;
  onCreateNew: () => void;
}

/**
 * Lista de projetos com busca e filtros (organização, criticidade, estado).
 * Cards empilhados no mobile, grid no desktop.
 */
export function ProjectList({ projects, organizations, onSelect, onCreateNew }: ProjectListProps) {
  const { t } = useTranslation();
  const [filters, setFilters] = useState<ProjectListFilters>(EMPTY_FILTERS);

  const organizationNames = useMemo(
    () => new Map(organizations.map((org) => [org.id, org.name])),
    [organizations],
  );
  const filtered = useMemo(() => filterProjects(projects, filters), [projects, filters]);

  // Paginação client-side; volta para a página 1 ao mudar busca/filtros.
  const pagination = usePagination(filtered.length, { resetKey: filters });

  function patch(partial: Partial<ProjectListFilters>) {
    setFilters((current) => ({ ...current, ...partial }));
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="grid gap-3 md:grid-cols-[1fr_auto_auto_auto_auto]">
        <div>
          <label htmlFor="project-search" className="sr-only">
            {t('projects.search')}
          </label>
          <Input
            id="project-search"
            type="search"
            value={filters.query}
            onChange={(event) => patch({ query: event.target.value })}
            placeholder={t('projects.search')}
          />
        </div>
        <div>
          <label htmlFor="project-filter-org" className="sr-only">
            {t('projects.filters.organization')}
          </label>
          <Select
            id="project-filter-org"
            aria-label={t('projects.filters.organization')}
            value={filters.organizationId}
            onChange={(event) => patch({ organizationId: event.target.value })}
          >
            <option value="">{t('projects.filters.allOrganizations')}</option>
            {organizations.map((org) => (
              <option key={org.id} value={org.id}>
                {org.name}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <label htmlFor="project-filter-criticality" className="sr-only">
            {t('projects.filters.criticality')}
          </label>
          <Select
            id="project-filter-criticality"
            aria-label={t('projects.filters.criticality')}
            value={filters.criticality}
            onChange={(event) => patch({ criticality: event.target.value })}
          >
            <option value="">{t('projects.filters.allCriticalities')}</option>
            {PRIORITIES.map((priority: Priority) => (
              <option key={priority} value={priority}>
                {t(`status.priority.${priority}`)}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <label htmlFor="project-filter-state" className="sr-only">
            {t('projects.filters.state')}
          </label>
          <Select
            id="project-filter-state"
            aria-label={t('projects.filters.state')}
            value={filters.state}
            onChange={(event) => patch({ state: event.target.value })}
          >
            <option value="">{t('projects.filters.allStates')}</option>
            {PROJECT_STATES.map((state: ProjectState) => (
              <option key={state} value={state}>
                {t(`status.projectState.${state}`)}
              </option>
            ))}
          </Select>
        </div>
        <Button type="button" onClick={onCreateNew}>
          {t('projects.create')}
        </Button>
      </div>

      {projects.length === 0 ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="font-medium">{t('projects.empty.title')}</p>
            <p className="text-sm text-foreground-muted">{t('projects.empty.body')}</p>
            <Button type="button" onClick={onCreateNew}>
              {t('projects.empty.cta')}
            </Button>
          </CardContent>
        </Card>
      ) : filtered.length === 0 ? (
        <p className="text-sm text-foreground-muted">{t('projects.emptySearch')}</p>
      ) : (
        <>
          <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {pagination.paginate(filtered).map((project) => (
              <li key={project.id}>
                <ProjectCard
                  project={project}
                  organizationName={organizationNames.get(project.organizationId)}
                  onSelect={onSelect}
                />
              </li>
            ))}
          </ul>
          <PaginationBar pagination={pagination} />
        </>
      )}
    </div>
  );
}
