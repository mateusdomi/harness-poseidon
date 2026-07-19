import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type AgentDefinition,
  type Document,
  type Gate,
  type Phase,
  type PublishWorkflowVersionInput,
  type SetOperationModeInput,
  type Ulid,
  type Workflow,
  type WorkflowRun,
  type WorkflowTemplate,
  type WorkflowVersion,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da feature de workflows. */
export const workflowKeys = {
  workflow: (projectId: Ulid) => ['workflows', 'workflow', projectId] as const,
  runs: (workflowId: Ulid) => ['workflows', 'runs', workflowId] as const,
  phases: (runId: Ulid) => ['workflows', 'phases', runId] as const,
  gates: (runId: Ulid) => ['workflows', 'gates', runId] as const,
  templates: () => ['workflows', 'templates'] as const,
  versions: () => ['workflows', 'versions'] as const,
  documents: (projectId: Ulid) => ['workflows', 'documents', projectId] as const,
  agentDefinitions: () => ['workflows', 'agent-definitions'] as const,
};

/** Prefixo que cobre todas as queries da feature. */
export const WORKFLOWS_PREFIX = ['workflows'] as const;

/** Workflow vinculado ao projeto (versão ativa + modo de operação). */
export function useProjectWorkflow(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: workflowKeys.workflow(projectId ?? 'none'),
    queryFn: async (): Promise<Workflow | null> =>
      (await api.list('workflows', { filter: { projectId: projectId! } })).items[0] ?? null,
    enabled: projectId !== null,
  });
}

/** Run mais recente do workflow (em andamento ou a última encerrada). */
export function useActiveRun(workflowId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: workflowKeys.runs(workflowId ?? 'none'),
    queryFn: async (): Promise<WorkflowRun | null> => {
      const runs = (await api.list('workflow-runs', { filter: { workflowId: workflowId! } }))
        .items;
      return (
        runs.find((run) => run.state === 'running' || run.state === 'paused') ??
        runs.sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ??
        null
      );
    },
    enabled: workflowId !== null,
  });
}

/** Fases (ordenadas) e gates de um run. */
export function useRunDetails(runId: Ulid | null) {
  const api = useApi();

  const phasesQuery = useQuery({
    queryKey: workflowKeys.phases(runId ?? 'none'),
    queryFn: async (): Promise<Phase[]> =>
      (await api.list('phases', { filter: { runId: runId! } })).items.sort(
        (a, b) => a.order - b.order,
      ),
    enabled: runId !== null,
  });

  const gatesQuery = useQuery({
    queryKey: workflowKeys.gates(runId ?? 'none'),
    queryFn: async (): Promise<Gate[]> =>
      (await api.list('gates', { filter: { runId: runId! } })).items,
    enabled: runId !== null,
  });

  return {
    phases: phasesQuery.data ?? [],
    gates: gatesQuery.data ?? [],
    isPending: phasesQuery.isPending || gatesQuery.isPending,
    isError: phasesQuery.isError || gatesQuery.isError,
    refetch: () => {
      void phasesQuery.refetch();
      void gatesQuery.refetch();
    },
  };
}

/** Templates e versões (administração). */
export function useWorkflowTemplates() {
  const api = useApi();

  const templatesQuery = useQuery({
    queryKey: workflowKeys.templates(),
    queryFn: async (): Promise<WorkflowTemplate[]> => (await api.list('workflow-templates')).items,
  });

  const versionsQuery = useQuery({
    queryKey: workflowKeys.versions(),
    queryFn: async (): Promise<WorkflowVersion[]> => (await api.list('workflow-versions')).items,
  });

  return {
    templates: templatesQuery.data ?? [],
    versions: versionsQuery.data ?? [],
    isPending: templatesQuery.isPending || versionsQuery.isPending,
    isError: templatesQuery.isError || versionsQuery.isError,
    refetch: () => {
      void templatesQuery.refetch();
      void versionsQuery.refetch();
    },
  };
}

/** Documentos do projeto (exibidos por fase no stepper). */
export function useWorkflowDocuments(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: workflowKeys.documents(projectId ?? 'none'),
    queryFn: async (): Promise<Document[]> =>
      (await api.list('documents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Definições de agente (configuração de agentes permitidos por fase). */
export function useAgentDefinitions() {
  const api = useApi();
  return useQuery({
    queryKey: workflowKeys.agentDefinitions(),
    queryFn: async (): Promise<AgentDefinition[]> => (await api.list('agent-definitions')).items,
  });
}

/**
 * Tempo real: gates mudando, versões publicadas e documentos transicionando
 * invalidam as queries da feature (re-sync autoritativo).
 */
export function useWorkflowsRealtime(projectId: Ulid | null) {
  useRealtimeStream(
    projectId === null ? null : [streams.project(projectId), streams.global()],
    {
      types: ['gate.changed', 'workflow.versionPublished', 'document.stateChanged'],
      invalidate: [WORKFLOWS_PREFIX],
    },
  );
}

export function useSetOperationMode() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, input }: { workflowId: Ulid; input: SetOperationModeInput }) =>
      api.setWorkflowOperationMode(workflowId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: WORKFLOWS_PREFIX }),
  });
}

export function usePublishWorkflowVersion() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      templateId,
      input,
    }: {
      templateId: Ulid;
      input: PublishWorkflowVersionInput;
    }) => api.publishWorkflowVersion(templateId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: WORKFLOWS_PREFIX }),
  });
}
