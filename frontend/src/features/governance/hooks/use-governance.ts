import { useQuery } from '@tanstack/react-query';

import { streams, type AuditEvent } from '@/api';
import { useApi } from '@/app/api-context';
import type { AuditCatalog } from '@/features/governance/lib/audit-derive';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da governança (trilha de auditoria + listas de correlação). */
export const governanceKeys = {
  audit: ['governance', 'audit-events'] as const,
  projects: ['governance', 'projects'] as const,
  tasks: ['governance', 'tasks'] as const,
  attempts: ['governance', 'attempts'] as const,
  approvals: ['governance', 'approvals'] as const,
  documents: ['governance', 'documents'] as const,
  demands: ['governance', 'demands'] as const,
  solicitations: ['governance', 'solicitations'] as const,
  workflows: ['governance', 'workflows'] as const,
  profiles: ['governance', 'profiles'] as const,
  agents: ['governance', 'agents'] as const,
  models: ['governance', 'models'] as const,
  tools: ['governance', 'tools'] as const,
};

const GOVERNANCE_PREFIX = ['governance'] as const;

/**
 * Trilha de auditoria completa + catálogo de listas para resolver atores,
 * alvos e popular os selects de filtro (projetos, tarefas, tentativas,
 * modelos, ferramentas, perfis e agentes).
 */
export function useGovernanceData() {
  const api = useApi();

  const auditQuery = useQuery({
    queryKey: governanceKeys.audit,
    queryFn: async (): Promise<AuditEvent[]> => (await api.list('audit-events')).items,
  });
  const projectsQuery = useQuery({
    queryKey: governanceKeys.projects,
    queryFn: async () => (await api.list('projects')).items,
  });
  const tasksQuery = useQuery({
    queryKey: governanceKeys.tasks,
    queryFn: async () => (await api.list('tasks')).items,
  });
  const attemptsQuery = useQuery({
    queryKey: governanceKeys.attempts,
    queryFn: async () => (await api.list('attempts')).items,
  });
  const approvalsQuery = useQuery({
    queryKey: governanceKeys.approvals,
    queryFn: async () => (await api.list('approvals')).items,
  });
  const documentsQuery = useQuery({
    queryKey: governanceKeys.documents,
    queryFn: async () => (await api.list('documents')).items,
  });
  const demandsQuery = useQuery({
    queryKey: governanceKeys.demands,
    queryFn: async () => (await api.list('demands')).items,
  });
  const solicitationsQuery = useQuery({
    queryKey: governanceKeys.solicitations,
    queryFn: async () => (await api.list('solicitations')).items,
  });
  const workflowsQuery = useQuery({
    queryKey: governanceKeys.workflows,
    queryFn: async () => (await api.list('workflows')).items,
  });
  const profilesQuery = useQuery({
    queryKey: governanceKeys.profiles,
    queryFn: async () => (await api.list('profiles')).items,
  });
  const agentsQuery = useQuery({
    queryKey: governanceKeys.agents,
    queryFn: async () => (await api.list('agents')).items,
  });
  const modelsQuery = useQuery({
    queryKey: governanceKeys.models,
    queryFn: async () => (await api.list('models')).items,
  });
  const toolsQuery = useQuery({
    queryKey: governanceKeys.tools,
    queryFn: async () => (await api.list('tools')).items,
  });

  const queries = [
    auditQuery,
    projectsQuery,
    tasksQuery,
    attemptsQuery,
    approvalsQuery,
    documentsQuery,
    demandsQuery,
    solicitationsQuery,
    workflowsQuery,
    profilesQuery,
    agentsQuery,
    modelsQuery,
    toolsQuery,
  ];

  const catalog: AuditCatalog = {
    projects: projectsQuery.data ?? [],
    tasks: tasksQuery.data ?? [],
    attempts: attemptsQuery.data ?? [],
    approvals: approvalsQuery.data ?? [],
    documents: documentsQuery.data ?? [],
    demands: demandsQuery.data ?? [],
    solicitations: solicitationsQuery.data ?? [],
    workflows: workflowsQuery.data ?? [],
    profiles: profilesQuery.data ?? [],
    agents: agentsQuery.data ?? [],
    models: modelsQuery.data ?? [],
    tools: toolsQuery.data ?? [],
  };

  return {
    events: auditQuery.data ?? [],
    catalog,
    isPending: queries.some((query) => query.isLoading),
    isError: queries.some((query) => query.isError),
    refetch: () => {
      for (const query of queries) void query.refetch();
    },
  };
}

/**
 * Tempo real: `audit.eventAppended` chega pelo stream global e derruba o
 * cache da trilha (invalidação por prefixo cobre as listas de correlação).
 */
export function useGovernanceRealtime() {
  useRealtimeStream(streams.global(), {
    types: ['audit.eventAppended'],
    invalidate: [GOVERNANCE_PREFIX],
  });
}
