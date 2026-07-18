import type { Project } from '@/api';

export interface ProjectListFilters {
  query: string;
  organizationId: string;
  criticality: string;
  state: string;
}

export const EMPTY_FILTERS: ProjectListFilters = {
  query: '',
  organizationId: '',
  criticality: '',
  state: '',
};

/** Filtro client-side puro: busca em nome/sigla/descrição + filtros exatos. */
export function filterProjects(projects: Project[], filters: ProjectListFilters): Project[] {
  const normalized = filters.query.trim().toLocaleLowerCase();
  return projects.filter((project) => {
    if (filters.organizationId && project.organizationId !== filters.organizationId) return false;
    if (filters.criticality && project.criticality !== filters.criticality) return false;
    if (filters.state && project.state !== filters.state) return false;
    if (normalized) {
      const haystack = `${project.name} ${project.key} ${project.description}`.toLocaleLowerCase();
      if (!haystack.includes(normalized)) return false;
    }
    return true;
  });
}
