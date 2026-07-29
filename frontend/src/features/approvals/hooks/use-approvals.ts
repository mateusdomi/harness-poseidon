import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type Approval,
  type Document,
  type Gate,
  type Project,
  type ResolveApprovalInput,
  type Task,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da fila de aprovações. */
export const approvalKeys = {
  all: ['approvals', 'all'] as const,
  projects: ['approvals', 'projects'] as const,
  gates: ['approvals', 'gates'] as const,
  documents: ['approvals', 'documents'] as const,
  tasks: ['approvals', 'tasks'] as const,
};

const APPROVALS_PREFIX = ['approvals'] as const;

/** Prefixo das queries da tela de Documentos, que hospeda a fila (D9). */
const DOCUMENTS_PREFIX = ['documents'] as const;

/**
 * Fila consolidada: todas as aprovações + projetos e entidades de
 * contexto (gates, documentos, tarefas) para exibir impacto/evidências.
 */
export function useApprovalsQueue() {
  const api = useApi();

  const approvalsQuery = useQuery({
    queryKey: approvalKeys.all,
    queryFn: async (): Promise<Approval[]> => (await api.list('approvals')).items,
  });

  const projectsQuery = useQuery({
    queryKey: approvalKeys.projects,
    queryFn: async (): Promise<Project[]> => (await api.list('projects')).items,
  });

  const gatesQuery = useQuery({
    queryKey: approvalKeys.gates,
    queryFn: async (): Promise<Gate[]> => (await api.list('gates')).items,
  });

  const documentsQuery = useQuery({
    queryKey: approvalKeys.documents,
    queryFn: async (): Promise<Document[]> => (await api.list('documents')).items,
  });

  const tasksQuery = useQuery({
    queryKey: approvalKeys.tasks,
    queryFn: async (): Promise<Task[]> => (await api.list('tasks')).items,
  });

  return {
    approvals: approvalsQuery.data ?? [],
    projects: projectsQuery.data ?? [],
    gates: gatesQuery.data ?? [],
    documents: documentsQuery.data ?? [],
    tasks: tasksQuery.data ?? [],
    isPending:
      approvalsQuery.isLoading ||
      projectsQuery.isLoading ||
      gatesQuery.isLoading ||
      documentsQuery.isLoading ||
      tasksQuery.isLoading,
    isError:
      approvalsQuery.isError ||
      projectsQuery.isError ||
      gatesQuery.isError ||
      documentsQuery.isError ||
      tasksQuery.isError,
    refetch: () => {
      void approvalsQuery.refetch();
      void projectsQuery.refetch();
      void gatesQuery.refetch();
      void documentsQuery.refetch();
      void tasksQuery.refetch();
    },
  };
}

/**
 * Tempo real: `approval.requested` adiciona à fila e `approval.resolved`
 * remove — assina o stream de todos os projetos (a fila é consolidada).
 */
export function useApprovalsRealtime(projectIds: Ulid[]) {
  useRealtimeStream(projectIds.length === 0 ? null : projectIds.map((id) => streams.project(id)), {
    types: ['approval.requested', 'approval.resolved'],
    invalidate: [APPROVALS_PREFIX],
  });
}

export function useResolveQueueApproval() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ approvalId, input }: { approvalId: Ulid; input: ResolveApprovalInput }) =>
      api.resolveApproval(approvalId, input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: APPROVALS_PREFIX });
      // A fila agora vive DENTRO de Documentos (D9): aprovar muda o estado do
      // documento, e o catálogo da aba vizinha tem de refletir isso na hora —
      // sem isso o dono aprova e continua vendo "aguardando aprovação" ao lado.
      void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX });
    },
  });
}
