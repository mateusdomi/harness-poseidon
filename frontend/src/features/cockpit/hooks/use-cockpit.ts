import { useQuery } from '@tanstack/react-query';

import { streams, type Phase, type Task, type Ulid } from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys do cockpit — escopadas por projeto onde aplicável. */
export const cockpitKeys = {
  tasks: (projectId: Ulid) => ['cockpit', 'tasks', projectId] as const,
  approvals: (projectId: Ulid) => ['cockpit', 'approvals', projectId] as const,
  agents: (projectId: Ulid) => ['cockpit', 'agents', projectId] as const,
  budgets: ['cockpit', 'budgets'] as const,
  workflows: (projectId: Ulid) => ['cockpit', 'workflows', projectId] as const,
  runs: (workflowId: Ulid) => ['cockpit', 'runs', workflowId] as const,
  phases: (runId: Ulid) => ['cockpit', 'phases', runId] as const,
  gates: (runId: Ulid) => ['cockpit', 'gates', runId] as const,
  audit: ['cockpit', 'audit-events'] as const,
};

/** Eventos do stream do projeto/global que derrubam o cache do cockpit. */
const COCKPIT_EVENT_TYPES = [
  'task.created',
  'task.stateChanged',
  'progress.updated',
  'approval.requested',
  'approval.resolved',
  'gate.changed',
  'notification.created',
  'agent.statusChanged',
  'quota.updated',
  'audit.eventAppended',
] as const;

export function useCockpitTasks(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: cockpitKeys.tasks(projectId ?? 'none'),
    queryFn: async (): Promise<Task[]> =>
      (await api.list('tasks', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

export function useCockpitApprovals(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: cockpitKeys.approvals(projectId ?? 'none'),
    queryFn: async () => (await api.list('approvals', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

export function useCockpitAgents(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: cockpitKeys.agents(projectId ?? 'none'),
    queryFn: async () => (await api.list('agents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Budgets de todos os escopos (global, projeto, conta) — cotas críticas. */
export function useCockpitBudgets() {
  const api = useApi();
  return useQuery({
    queryKey: cockpitKeys.budgets,
    queryFn: async () => (await api.list('budgets')).items,
  });
}

/** Workflow ativo do projeto → run corrente → fases e gates. */
export function useCockpitWorkflow(projectId: Ulid | null) {
  const api = useApi();

  const workflowsQuery = useQuery({
    queryKey: cockpitKeys.workflows(projectId ?? 'none'),
    queryFn: async () => (await api.list('workflows', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
  const workflow = workflowsQuery.data?.[0] ?? null;

  const runsQuery = useQuery({
    queryKey: cockpitKeys.runs(workflow?.id ?? 'none'),
    queryFn: async () => (await api.list('workflow-runs', { filter: { workflowId: workflow!.id } })).items,
    enabled: workflow !== null,
  });
  const run = runsQuery.data?.find((r) => r.state === 'running') ?? runsQuery.data?.[0] ?? null;

  const phasesQuery = useQuery({
    queryKey: cockpitKeys.phases(run?.id ?? 'none'),
    queryFn: async (): Promise<Phase[]> =>
      (await api.list('phases', { filter: { runId: run!.id } })).items,
    enabled: run !== null,
  });

  const gatesQuery = useQuery({
    queryKey: cockpitKeys.gates(run?.id ?? 'none'),
    queryFn: async () => (await api.list('gates', { filter: { runId: run!.id } })).items,
    enabled: run !== null,
  });

  return {
    workflow,
    run,
    phases: phasesQuery.data ?? [],
    gates: gatesQuery.data ?? [],
    isPending:
      workflowsQuery.isLoading ||
      (workflow !== null && runsQuery.isLoading) ||
      (run !== null && (phasesQuery.isLoading || gatesQuery.isLoading)),
    isError:
      workflowsQuery.isError || runsQuery.isError || phasesQuery.isError || gatesQuery.isError,
    refetch: () => {
      void workflowsQuery.refetch();
      void runsQuery.refetch();
      void phasesQuery.refetch();
      void gatesQuery.refetch();
    },
  };
}

/**
 * Timeline de auditoria COMPLETA (mais recentes primeiro). O recorte por
 * período e o carregamento incremental ficam no ActivityFeed (D-070):
 * filtrar/fatiar no cliente mantém o feed realtime (evento novo entra no
 * topo sem refetch de página).
 */
export function useCockpitActivity() {
  const api = useApi();
  return useQuery({
    queryKey: cockpitKeys.audit,
    queryFn: async () =>
      (await api.list('audit-events')).items.sort((a, b) =>
        b.occurredAt.localeCompare(a.occurredAt),
      ),
  });
}

/**
 * Tempo real do cockpit: assina o stream do projeto + global e invalida
 * as queries afetadas. Dedupe/lacuna ficam no useRealtimeStream.
 */
export function useCockpitRealtime(projectId: Ulid | null) {
  useRealtimeStream(projectId === null ? null : [streams.project(projectId), streams.global()], {
    types: COCKPIT_EVENT_TYPES,
    // Prefixos: invalidateQueries casa por prefixo (cobre phases/gates por runId).
    invalidate: projectId
      ? [
          cockpitKeys.tasks(projectId),
          cockpitKeys.approvals(projectId),
          cockpitKeys.agents(projectId),
          cockpitKeys.budgets,
          cockpitKeys.workflows(projectId),
          ['cockpit', 'phases'],
          ['cockpit', 'gates'],
          cockpitKeys.audit,
        ]
      : [],
  });
}
