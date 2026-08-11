import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type {
  Agent,
  CreateInputMap,
  Phase,
  Project,
  Task,
  Ulid,
  UpdateInputMap,
  Workflow,
  WorkflowRun,
  WorkflowTemplate,
  WorkflowVersion,
} from '@/api';
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

export function useProjectOperationalData() {
  const api = useApi();
  return useQuery({
    queryKey: [...projectKeys.all, 'operational'] as const,
    queryFn: async () => {
      const [tasks, agents, workflows, runs, phases] = await Promise.all([
        api.list('tasks', { limit: 200 }),
        api.list('agents', { limit: 200 }),
        api.list('workflows', { limit: 200 }),
        api.list('workflow-runs', { limit: 200 }),
        api.list('phases', { limit: 200 }),
      ]);
      return {
        tasks: tasks.items as Task[],
        agents: agents.items as Agent[],
        workflows: workflows.items as Workflow[],
        runs: runs.items as WorkflowRun[],
        phases: phases.items as Phase[],
      };
    },
  });
}

export function useProjectWorkflowCatalog() {
  const api = useApi();
  return useQuery({
    queryKey: [...projectKeys.all, 'workflow-catalog'] as const,
    queryFn: async (): Promise<{
      templates: WorkflowTemplate[];
      versions: WorkflowVersion[];
    }> => {
      const [templates, versions] = await Promise.all([
        api.list('workflow-templates', { limit: 200 }),
        api.list('workflow-versions', { limit: 200 }),
      ]);
      return { templates: templates.items, versions: versions.items };
    },
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

/**
 * Projeto INICIADO (FR-4): tem workflow vinculado com ao menos uma execução
 * (run em qualquer estado). Edição de campos operacionais em projeto
 * iniciado exige o painel de impacto antes de salvar.
 */
export function useProjectStarted(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: [...projectKeys.all, 'started', projectId ?? 'none'] as const,
    enabled: projectId !== null,
    queryFn: async (): Promise<boolean> => {
      const workflow = (await api.list('workflows', { filter: { projectId: projectId! } }))
        .items[0];
      if (!workflow) return false;
      const runs = (await api.list('workflow-runs', { filter: { workflowId: workflow.id } }))
        .items;
      return runs.length > 0;
    },
  });
}
