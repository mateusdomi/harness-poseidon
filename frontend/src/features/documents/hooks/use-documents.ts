import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type Approval,
  type ClassifyDocumentInput,
  type Document,
  type DocumentVersion,
  type ResolveApprovalInput,
  type TransitionDocumentInput,
  type Ulid,
  type Workflow,
  type WorkflowVersion,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da feature de documentos. */
export const documentKeys = {
  list: (projectId: Ulid) => ['documents', 'list', projectId] as const,
  detail: (documentId: Ulid) => ['documents', 'detail', documentId] as const,
  versions: (documentId: Ulid) => ['documents', 'versions', documentId] as const,
  approvals: (projectId: Ulid) => ['documents', 'approvals', projectId] as const,
  workflow: (projectId: Ulid) => ['documents', 'workflow', projectId] as const,
  workflowVersions: () => ['documents', 'workflow-versions'] as const,
};

/** Prefixo que cobre todas as queries da feature. */
const DOCUMENTS_PREFIX = ['documents'] as const;

/** Catálogo de documentos do projeto. */
export function useDocuments(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: documentKeys.list(projectId ?? 'none'),
    queryFn: async (): Promise<Document[]> =>
      (await api.list('documents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Detalhe de um documento + versões (ordenadas, mais recente primeiro). */
export function useDocumentDetail(documentId: Ulid | null) {
  const api = useApi();

  const documentQuery = useQuery({
    queryKey: documentKeys.detail(documentId ?? 'none'),
    queryFn: () => api.get('documents', documentId!),
    enabled: documentId !== null,
  });

  const versionsQuery = useQuery({
    queryKey: documentKeys.versions(documentId ?? 'none'),
    queryFn: async (): Promise<DocumentVersion[]> =>
      (await api.list('document-versions', { filter: { documentId: documentId! } })).items.sort(
        (a, b) => b.version - a.version,
      ),
    enabled: documentId !== null,
  });

  return {
    document: documentQuery.data ?? null,
    versions: versionsQuery.data ?? [],
    isPending: documentQuery.isLoading || versionsQuery.isLoading,
    isError: documentQuery.isError || versionsQuery.isError,
    refetch: () => {
      void documentQuery.refetch();
      void versionsQuery.refetch();
    },
  };
}

/** Aprovações do projeto (para localizar a pendente de cada documento). */
export function useDocumentApprovals(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: documentKeys.approvals(projectId ?? 'none'),
    queryFn: async (): Promise<Approval[]> =>
      (await api.list('approvals', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Fases do workflow do projeto (filtro por fase + classificação de órfãos). */
export function useWorkflowPhases(projectId: Ulid | null) {
  const api = useApi();

  const workflowQuery = useQuery({
    queryKey: documentKeys.workflow(projectId ?? 'none'),
    queryFn: async (): Promise<Workflow | null> =>
      (await api.list('workflows', { filter: { projectId: projectId! } })).items[0] ?? null,
    enabled: projectId !== null,
  });

  const versionsQuery = useQuery({
    queryKey: documentKeys.workflowVersions(),
    queryFn: async (): Promise<WorkflowVersion[]> => (await api.list('workflow-versions')).items,
    enabled: projectId !== null,
  });

  const workflow = workflowQuery.data ?? null;
  const activeVersion =
    versionsQuery.data?.find((version) => version.id === workflow?.activeVersionId) ?? null;

  return {
    phases: activeVersion?.phases ?? [],
    isPending: workflowQuery.isLoading || versionsQuery.isLoading,
    isError: workflowQuery.isError || versionsQuery.isError,
  };
}

/**
 * Tempo real: `document.stateChanged` aplica o novo estado no cache do
 * catálogo/detalhe e invalida o prefixo (re-sync autoritativo).
 */
export function useDocumentsRealtime(projectId: Ulid | null) {
  const queryClient = useQueryClient();
  useRealtimeStream(projectId === null ? null : streams.project(projectId), {
    types: ['document.stateChanged'],
    onEvent: (event) => {
      if (event.type !== 'document.stateChanged') return;
      const { documentId, to } = event.payload;
      queryClient.setQueryData<Document[]>(documentKeys.list(projectId!), (current) =>
        current?.map((doc) =>
          doc.id === documentId ? { ...doc, state: to, updatedAt: event.occurredAt } : doc,
        ),
      );
      queryClient.setQueryData<Document>(documentKeys.detail(documentId), (current) =>
        current ? { ...current, state: to, updatedAt: event.occurredAt } : current,
      );
    },
    invalidate: [DOCUMENTS_PREFIX],
  });
}

export function useTransitionDocument() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ documentId, input }: { documentId: Ulid; input: TransitionDocumentInput }) =>
      api.transitionDocument(documentId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX }),
  });
}

export function useClassifyDocument() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ documentId, input }: { documentId: Ulid; input: ClassifyDocumentInput }) =>
      api.classifyDocument(documentId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX }),
  });
}

export function useResolveDocumentApproval() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ approvalId, input }: { approvalId: Ulid; input: ResolveApprovalInput }) =>
      api.resolveApproval(approvalId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX }),
  });
}

/**
 * Edição manual: salva o conteúdo como NOVA versão (origem humana —
 * `authorKind: 'user'`); as anteriores permanecem imutáveis no histórico.
 */
export function useSaveDocumentVersion() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ documentId, body }: { documentId: Ulid; body: string }) =>
      api.saveDocumentVersion(documentId, { body }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX }),
  });
}

/** Solicita aprovação: cria a aprovação pendente e move o documento para `awaitingApproval`. */
export function useRequestDocumentApproval() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: {
      document: Document;
      requestedByAgentId: Ulid;
      title: string;
      description: string;
    }) => {
      await api.transitionDocument(input.document.id, { toState: 'awaitingApproval' });
      return api.create('approvals', {
        projectId: input.document.projectId,
        documentId: input.document.id,
        title: input.title,
        description: input.description,
        requestedByAgentId: input.requestedByAgentId,
      });
    },
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX }),
  });
}

/** Upload de documento externo (arquivo já lido como texto pelo chamador). */
export function useUploadDocument() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      projectId: Ulid;
      title: string;
      kind: Document['kind'];
      body: string;
      classifications: string[];
    }) => api.create('documents', input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DOCUMENTS_PREFIX }),
  });
}
