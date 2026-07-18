import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { CreateInputMap, Organization, Ulid, UpdateInputMap } from '@/api';
import { useApi } from '@/app/api-context';

export const organizationKeys = {
  all: ['organizations'] as const,
  detail: (id: Ulid) => ['organizations', id] as const,
  projects: (id: Ulid) => ['organizations', id, 'projects'] as const,
  workflowTemplates: ['workflow-templates'] as const,
};

/** Lista de organizações. Busca é client-side (filtro de texto). */
export function useOrganizations() {
  const api = useApi();
  return useQuery({
    queryKey: organizationKeys.all,
    queryFn: async () => (await api.list('organizations')).items,
  });
}

/** Projetos associados a uma organização. */
export function useOrganizationProjects(organizationId: Ulid) {
  const api = useApi();
  return useQuery({
    queryKey: organizationKeys.projects(organizationId),
    queryFn: async () =>
      (await api.list('projects', { filter: { organizationId } })).items,
  });
}

/** Contagem de projetos por organização (para os cards da lista). */
export function useProjectCountsByOrganization() {
  const api = useApi();
  return useQuery({
    queryKey: ['projects', 'count-by-organization'],
    queryFn: async () => {
      const projects = (await api.list('projects')).items;
      const counts = new Map<string, number>();
      for (const project of projects) {
        counts.set(project.organizationId, (counts.get(project.organizationId) ?? 0) + 1);
      }
      return counts;
    },
  });
}

/** Templates de workflow (para resolver os padrões da organização). */
export function useWorkflowTemplates() {
  const api = useApi();
  return useQuery({
    queryKey: organizationKeys.workflowTemplates,
    queryFn: async () => (await api.list('workflow-templates')).items,
  });
}

export function useCreateOrganization() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInputMap['organizations']): Promise<Organization> =>
      api.create('organizations', input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: organizationKeys.all });
    },
  });
}

export function useUpdateOrganization() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: Ulid; input: UpdateInputMap['organizations'] }) =>
      api.update('organizations', id, input),
    onSuccess: (organization) => {
      void queryClient.invalidateQueries({ queryKey: organizationKeys.all });
      void queryClient.invalidateQueries({
        queryKey: organizationKeys.detail(organization.id),
      });
    },
  });
}
