import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { CreateInputMap, Project, Ulid, UpdateInputMap } from '@/api';
import { useApi } from '@/app/api-context';

export const projectKeys = {
  all: ['projects'] as const,
};

/** Lista de projetos. Busca e filtros são client-side (texto/enums). */
export function useProjects() {
  const api = useApi();
  return useQuery({
    queryKey: projectKeys.all,
    queryFn: async () => (await api.list('projects')).items,
  });
}

export function useCreateProject() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInputMap['projects']): Promise<Project> =>
      api.create('projects', input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: projectKeys.all });
    },
  });
}

export function useUpdateProject() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: Ulid; input: UpdateInputMap['projects'] }) =>
      api.update('projects', id, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: projectKeys.all });
    },
  });
}
