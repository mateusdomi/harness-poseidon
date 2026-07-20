import { useQuery } from '@tanstack/react-query';

import {
  streams,
  type Agent,
  type AgentDefinition,
  type Attempt,
  type AuditEvent,
  type Model,
  type Skill,
  type Task,
  type Tool,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da tela de agentes. */
export const agentKeys = {
  all: ['agents', 'all'] as const,
  definitions: ['agents', 'definitions'] as const,
  skills: ['agents', 'skills'] as const,
  tools: ['agents', 'tools'] as const,
  models: ['agents', 'models'] as const,
  tasks: ['agents', 'tasks'] as const,
  attempts: ['agents', 'attempts'] as const,
  auditEvents: ['agents', 'audit-events'] as const,
};

const AGENTS_PREFIX = ['agents'] as const;

/**
 * Dados da equipe: instâncias e definições de agentes + entidades de
 * contexto (skills, ferramentas, modelos, tarefas, attempts e auditoria)
 * usadas nos cards, métricas derivadas e no detalhe.
 */
export function useAgentsData() {
  const api = useApi();

  const agentsQuery = useQuery({
    queryKey: agentKeys.all,
    queryFn: async (): Promise<Agent[]> => (await api.list('agents')).items,
  });

  const definitionsQuery = useQuery({
    queryKey: agentKeys.definitions,
    queryFn: async (): Promise<AgentDefinition[]> =>
      (await api.list('agent-definitions', { filter: { includeArchived: true } })).items,
  });

  const skillsQuery = useQuery({
    queryKey: agentKeys.skills,
    queryFn: async (): Promise<Skill[]> => (await api.list('skills')).items,
  });

  const toolsQuery = useQuery({
    queryKey: agentKeys.tools,
    queryFn: async (): Promise<Tool[]> => (await api.list('tools')).items,
  });

  const modelsQuery = useQuery({
    queryKey: agentKeys.models,
    queryFn: async (): Promise<Model[]> => (await api.list('models')).items,
  });

  const tasksQuery = useQuery({
    queryKey: agentKeys.tasks,
    queryFn: async (): Promise<Task[]> => (await api.list('tasks')).items,
  });

  const attemptsQuery = useQuery({
    queryKey: agentKeys.attempts,
    queryFn: async (): Promise<Attempt[]> => (await api.list('attempts')).items,
  });

  const auditEventsQuery = useQuery({
    queryKey: agentKeys.auditEvents,
    queryFn: async (): Promise<AuditEvent[]> => (await api.list('audit-events')).items,
  });

  const queries = [
    agentsQuery,
    definitionsQuery,
    skillsQuery,
    toolsQuery,
    modelsQuery,
    tasksQuery,
    attemptsQuery,
    auditEventsQuery,
  ];

  return {
    agents: agentsQuery.data ?? [],
    definitions: definitionsQuery.data ?? [],
    skills: skillsQuery.data ?? [],
    tools: toolsQuery.data ?? [],
    models: modelsQuery.data ?? [],
    tasks: tasksQuery.data ?? [],
    attempts: attemptsQuery.data ?? [],
    auditEvents: auditEventsQuery.data ?? [],
    isPending: queries.some((query) => query.isLoading),
    isError: queries.some((query) => query.isError),
    refetch: () => {
      for (const query of queries) void query.refetch();
    },
  };
}

/**
 * Tempo real: `agent.statusChanged` chega pelo stream global e invalida
 * as queries da tela (badges de estado e métricas se atualizam sozinhos).
 */
export function useAgentsRealtime() {
  useRealtimeStream(streams.global(), {
    types: ['agent.statusChanged'],
    invalidate: [AGENTS_PREFIX],
  });
}
