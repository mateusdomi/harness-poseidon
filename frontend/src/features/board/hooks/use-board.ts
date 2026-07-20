import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type Agent,
  type Approval,
  type Attempt,
  type AttemptEvent,
  type MoveTaskInput,
  type ResolveApprovalInput,
  type SetTaskPriorityInput,
  type Task,
  type TaskInstruction,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys do quadro — detalhe por tarefa, listas por projeto. */
export const boardKeys = {
  tasks: (projectId: Ulid) => ['board', 'tasks', projectId] as const,
  agents: (projectId: Ulid) => ['board', 'agents', projectId] as const,
  task: (taskId: Ulid) => ['board', 'task', taskId] as const,
  instructions: (taskId: Ulid) => ['board', 'task-instructions', taskId] as const,
  attempts: (taskId: Ulid) => ['board', 'attempts', taskId] as const,
  attemptEvents: (taskId: Ulid) => ['board', 'attempt-events', taskId] as const,
  approvals: (taskId: Ulid) => ['board', 'approvals', taskId] as const,
  demand: (demandId: Ulid) => ['board', 'demand', demandId] as const,
};

/** Prefixo que cobre todas as queries do quadro (listas + detalhes). */
const BOARD_PREFIX = ['board'] as const;

export function useBoardTasks(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: boardKeys.tasks(projectId ?? 'none'),
    queryFn: async (): Promise<Task[]> =>
      (await api.list('tasks', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

export function useBoardAgents(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: boardKeys.agents(projectId ?? 'none'),
    queryFn: async (): Promise<Agent[]> =>
      (await api.list('agents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Eventos do stream do projeto que movem/atualizam o quadro. */
const BOARD_EVENT_TYPES = [
  'task.created',
  'task.stateChanged',
  'progress.updated',
  'approval.requested',
  'approval.resolved',
] as const;

/** Quanto tempo (ms) um card recém-movido fica destacado. */
const MOVED_HIGHLIGHT_MS = 1_500;

/**
 * Tempo real do quadro: `task.stateChanged` aplica atualização otimista no
 * cache (movimento imediato + destaque discreto no card) e os demais
 * eventos invalidam o prefixo `board` (re-sync autoritativo).
 */
export function useBoardRealtime(projectId: Ulid | null): ReadonlySet<Ulid> {
  const queryClient = useQueryClient();
  const [recentlyMoved, setRecentlyMoved] = useState<ReadonlySet<Ulid>>(new Set());

  useRealtimeStream(projectId === null ? null : streams.project(projectId), {
    types: BOARD_EVENT_TYPES,
    onEvent: (event) => {
      if (event.type === 'task.stateChanged') {
        const { taskId, to, note } = event.payload;
        queryClient.setQueryData<Task[]>(boardKeys.tasks(projectId!), (current) =>
          current?.map((task) =>
            task.id === taskId
              ? {
                  ...task,
                  state: to,
                  blockedReason: to === 'blocked' ? (note ?? task.blockedReason) : null,
                  updatedAt: event.occurredAt,
                }
              : task,
          ),
        );
        setRecentlyMoved((previous) => new Set(previous).add(taskId));
      }
    },
    invalidate: [BOARD_PREFIX],
  });

  // Remove o destaque após a janela de animação.
  useEffect(() => {
    if (recentlyMoved.size === 0) return;
    const timer = setTimeout(() => setRecentlyMoved(new Set()), MOVED_HIGHLIGHT_MS);
    return () => clearTimeout(timer);
  }, [recentlyMoved]);

  return recentlyMoved;
}

/** Detalhe completo da tarefa (instruções, attempts, eventos, aprovações). */
export function useTaskDetail(taskId: Ulid | null) {
  const api = useApi();

  const taskQuery = useQuery({
    queryKey: boardKeys.task(taskId ?? 'none'),
    queryFn: () => api.get('tasks', taskId!),
    enabled: taskId !== null,
  });

  const instructionsQuery = useQuery({
    queryKey: boardKeys.instructions(taskId ?? 'none'),
    queryFn: async (): Promise<TaskInstruction[]> =>
      (await api.list('task-instructions', { filter: { taskId: taskId! } })).items.sort(
        (a, b) => b.version - a.version,
      ),
    enabled: taskId !== null,
  });

  const attemptsQuery = useQuery({
    queryKey: boardKeys.attempts(taskId ?? 'none'),
    queryFn: async (): Promise<Attempt[]> =>
      (await api.list('attempts', { filter: { taskId: taskId! } })).items.sort(
        (a, b) => a.number - b.number,
      ),
    enabled: taskId !== null,
  });

  const attemptEventsQuery = useQuery({
    queryKey: boardKeys.attemptEvents(taskId ?? 'none'),
    queryFn: async (): Promise<AttemptEvent[]> => {
      const all = (await api.list('attempt-events')).items;
      const attemptIds = new Set((attemptsQuery.data ?? []).map((attempt) => attempt.id));
      return all
        .filter((event) => attemptIds.has(event.attemptId))
        .sort((a, b) => a.occurredAt.localeCompare(b.occurredAt));
    },
    enabled: taskId !== null && attemptsQuery.data !== undefined,
  });

  const approvalsQuery = useQuery({
    queryKey: boardKeys.approvals(taskId ?? 'none'),
    queryFn: async (): Promise<Approval[]> =>
      (await api.list('approvals', { filter: { taskId: taskId! } })).items,
    enabled: taskId !== null,
  });

  const demandId = taskQuery.data?.demandId ?? null;
  const demandQuery = useQuery({
    queryKey: boardKeys.demand(demandId ?? 'none'),
    queryFn: () => api.get('demands', demandId!),
    enabled: demandId !== null,
  });

  return {
    task: taskQuery.data ?? null,
    instructions: instructionsQuery.data ?? [],
    attempts: attemptsQuery.data ?? [],
    attemptEvents: attemptEventsQuery.data ?? [],
    approvals: approvalsQuery.data ?? [],
    demand: demandQuery.data ?? null,
    isPending:
      taskQuery.isLoading || instructionsQuery.isLoading || attemptsQuery.isLoading ||
      approvalsQuery.isLoading,
    isError:
      taskQuery.isError || instructionsQuery.isError || attemptsQuery.isError ||
      attemptEventsQuery.isError || approvalsQuery.isError,
    refetch: () => {
      void taskQuery.refetch();
      void instructionsQuery.refetch();
      void attemptsQuery.refetch();
      void attemptEventsQuery.refetch();
      void approvalsQuery.refetch();
    },
  };
}

/** Stream `task:<id>`: attempts/progresso do detalhe se atualizam sozinhos. */
export function useTaskRealtime(taskId: Ulid | null) {
  useRealtimeStream(taskId === null ? null : streams.task(taskId), {
    invalidate: [BOARD_PREFIX],
  });
}

export function useMoveTask() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ taskId, input }: { taskId: Ulid; input: MoveTaskInput }) =>
      api.moveTask(taskId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: BOARD_PREFIX }),
  });
}

export function useSetTaskPriority() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ taskId, input }: { taskId: Ulid; input: SetTaskPriorityInput }) =>
      api.setTaskPriority(taskId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: BOARD_PREFIX }),
  });
}

/** Arquiva uma tarefa concluída (metaestado — não muda a coluna). */
export function useArchiveTask() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (taskId: Ulid) => api.archiveTask(taskId),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: BOARD_PREFIX }),
  });
}

/** Desarquiva uma tarefa (sempre permitido). */
export function useUnarchiveTask() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (taskId: Ulid) => api.unarchiveTask(taskId),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: BOARD_PREFIX }),
  });
}

/**
 * Ação em lote: arquiva todas as tarefas concluídas e ainda ativas
 * (sequencial, para manter a ordem de erros determinística no mock).
 * Retorna quantas foram arquivadas.
 */
export function useArchiveCompletedTasks() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (tasks: Task[]) => {
      let archived = 0;
      for (const task of tasks) {
        if (task.state !== 'done' || task.archivedAt !== null) continue;
        await api.archiveTask(task.id);
        archived += 1;
      }
      return archived;
    },
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: BOARD_PREFIX }),
  });
}

export function useResolveTaskApproval() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ approvalId, input }: { approvalId: Ulid; input: ResolveApprovalInput }) =>
      api.resolveApproval(approvalId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: BOARD_PREFIX }),
  });
}

/** Relógio compartilhado para tempos relativos ("há 2 min") que se atualizam. */
export { useNow } from '@/features/shared/hooks/use-now';
