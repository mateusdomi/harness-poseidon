import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type Account,
  type Agent,
  type AgentDefinition,
  type Attempt,
  type AttemptEvent,
  type Budget,
  type ChiefTurnState,
  type Conversation,
  type DrainChiefTasksInput,
  type HandoffChiefInput,
  type Model,
  type Task,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { projectKeys } from '@/features/projects/hooks/use-projects';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys do orquestrador — escopadas por projeto onde aplicável. */
export const orchestratorKeys = {
  agents: (projectId: Ulid) => ['orchestrator', 'agents', projectId] as const,
  tasks: (projectId: Ulid) => ['orchestrator', 'tasks', projectId] as const,
  attempts: ['orchestrator', 'attempts'] as const,
  attemptEvents: (attemptId: Ulid) => ['orchestrator', 'attempt-events', attemptId] as const,
  conversations: (projectId: Ulid) => ['orchestrator', 'conversations', projectId] as const,
  definitions: ['orchestrator', 'agent-definitions'] as const,
  models: ['orchestrator', 'models'] as const,
  accounts: ['orchestrator', 'accounts'] as const,
  budgets: ['orchestrator', 'budgets'] as const,
};

/** Prefixo para invalidação em lote (mutations e realtime). */
const ORCHESTRATOR_PREFIX = ['orchestrator'] as const;

/** Eventos que derrubam o cache da tela (agentes, attempts e cotas). */
const ORCHESTRATOR_EVENT_TYPES = [
  'agent.statusChanged',
  'attempt.started',
  'attempt.heartbeat',
  'attempt.completed',
  'attempt.failed',
  'quota.updated',
] as const;

/**
 * Dados consolidados do orquestrador para o projeto ativo: agentes,
 * tarefas, attempts, conversas e o catálogo de providers (definições,
 * modelos, contas, budgets) usado pelo card do chefe e pela grade.
 */
export function useOrchestratorData(projectId: Ulid | null) {
  const api = useApi();

  const agentsQuery = useQuery({
    queryKey: orchestratorKeys.agents(projectId ?? 'none'),
    queryFn: async (): Promise<Agent[]> =>
      (await api.list('agents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });

  const tasksQuery = useQuery({
    queryKey: orchestratorKeys.tasks(projectId ?? 'none'),
    queryFn: async (): Promise<Task[]> =>
      (await api.list('tasks', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });

  // Attempts não têm projectId — cruzamento pelo agentId dos agentes do projeto.
  const attemptsQuery = useQuery({
    queryKey: orchestratorKeys.attempts,
    queryFn: async (): Promise<Attempt[]> => (await api.list('attempts')).items,
  });

  const conversationsQuery = useQuery({
    queryKey: orchestratorKeys.conversations(projectId ?? 'none'),
    queryFn: async (): Promise<Conversation[]> =>
      (await api.list('conversations', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });

  const definitionsQuery = useQuery({
    queryKey: orchestratorKeys.definitions,
    queryFn: async (): Promise<AgentDefinition[]> => (await api.list('agent-definitions')).items,
  });

  const modelsQuery = useQuery({
    queryKey: orchestratorKeys.models,
    queryFn: async (): Promise<Model[]> => (await api.list('models')).items,
  });

  const accountsQuery = useQuery({
    queryKey: orchestratorKeys.accounts,
    queryFn: async (): Promise<Account[]> => (await api.list('accounts')).items,
  });

  const budgetsQuery = useQuery({
    queryKey: orchestratorKeys.budgets,
    queryFn: async (): Promise<Budget[]> => (await api.list('budgets')).items,
  });

  const queries = [
    agentsQuery,
    tasksQuery,
    attemptsQuery,
    conversationsQuery,
    definitionsQuery,
    modelsQuery,
    accountsQuery,
    budgetsQuery,
  ];

  return {
    agents: agentsQuery.data ?? [],
    tasks: tasksQuery.data ?? [],
    attempts: attemptsQuery.data ?? [],
    conversations: conversationsQuery.data ?? [],
    definitions: definitionsQuery.data ?? [],
    models: modelsQuery.data ?? [],
    accounts: accountsQuery.data ?? [],
    budgets: budgetsQuery.data ?? [],
    isPending: queries.some((query) => query.isPending),
    isError: queries.some((query) => query.isError),
    refetch: () => queries.forEach((query) => void query.refetch()),
  };
}

/** Eventos (evidências) de uma tentativa — alimentam a linha do tempo e o log. */
export function useAttemptEvents(attemptId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: orchestratorKeys.attemptEvents(attemptId ?? 'none'),
    queryFn: async (): Promise<AttemptEvent[]> =>
      (await api.list('attempt-events', { filter: { attemptId: attemptId! } })).items,
    enabled: attemptId !== null,
  });
}

/** Modelos habilitados — opções do wizard de passagem de bastão. */
export function useEnabledModels() {
  const api = useApi();
  return useQuery({
    queryKey: orchestratorKeys.models,
    queryFn: async (): Promise<Model[]> =>
      (await api.list('models')).items.filter((model) => model.enabled),
  });
}

/**
 * Estado do turno do chefe em tempo real: assina os streams das conversas
 * do projeto e guarda o ÚLTIMO estado recebido de `chief.turnStateChanged`
 * (padrão 'idle' antes de qualquer evento).
 */
export function useChiefTurnState(conversationIds: readonly Ulid[]): ChiefTurnState {
  const [turnState, setTurnState] = useState<ChiefTurnState>('idle');

  useRealtimeStream(
    conversationIds.length === 0 ? null : conversationIds.map((id) => streams.conversation(id)),
    {
      types: ['chief.turnStateChanged'],
      onEvent: (event) => {
        if (event.type === 'chief.turnStateChanged') setTurnState(event.payload.state);
      },
    },
  );

  return turnState;
}

/**
 * Tempo real da tela: `agent.statusChanged` no stream global + `attempt.*`
 * nos streams das tentativas em execução (heartbeat mantém duração/custo
 * frescos) → invalidação das queries do orquestrador e do projeto ativo.
 */
export function useOrchestratorRealtime(projectId: Ulid | null, attemptIds: readonly Ulid[]) {
  const streamNames =
    projectId === null
      ? null
      : [streams.global(), ...attemptIds.map((id) => streams.attempt(id))];
  useRealtimeStream(streamNames, {
    types: ORCHESTRATOR_EVENT_TYPES,
    invalidate: [ORCHESTRATOR_PREFIX, projectKeys.all],
  });
}

/** Invalidação padrão pós-mutação: feature + projeto ativo (estado/bastão). */
function useInvalidateOrchestrator() {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: ORCHESTRATOR_PREFIX });
    void queryClient.invalidateQueries({ queryKey: projectKeys.all });
  };
}

/** Pausa a orquestração do chefe (projeto → `paused`). */
export function usePauseChief(projectId: Ulid | null) {
  const api = useApi();
  const invalidate = useInvalidateOrchestrator();
  return useMutation({
    mutationFn: () => api.pauseChief(projectId!),
    onSuccess: invalidate,
  });
}

/** Retoma a orquestração do chefe (projeto → `active`). */
export function useResumeChief(projectId: Ulid | null) {
  const api = useApi();
  const invalidate = useInvalidateOrchestrator();
  return useMutation({
    mutationFn: () => api.resumeChief(projectId!),
    onSuccess: invalidate,
  });
}

/** Drena as tarefas em andamento do projeto (voltam para `ready`). */
export function useDrainChiefTasks(projectId: Ulid | null) {
  const api = useApi();
  const invalidate = useInvalidateOrchestrator();
  return useMutation({
    mutationFn: (input: DrainChiefTasksInput) => api.drainChiefTasks(projectId!, input),
    onSuccess: invalidate,
  });
}

/**
 * Passagem de bastão: nova instância assume a orquestração. A invalidação
 * recarrega agentes + projeto — o card do chefe reflete o novo modelo.
 */
export function useHandoffChief(projectId: Ulid | null) {
  const api = useApi();
  const invalidate = useInvalidateOrchestrator();
  return useMutation({
    mutationFn: (input: HandoffChiefInput) => api.handoffChief(projectId!, input),
    onSuccess: invalidate,
  });
}
