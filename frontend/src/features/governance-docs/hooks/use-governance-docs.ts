import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { GovernanceDocContent, GovernanceDocTree } from '@/api';
import { useApi } from '@/app/api-context';

export const governanceDocsKeys = {
  all: ['governance-docs'] as const,
  tree: ['governance-docs', 'tree'] as const,
  content: (path: string) => ['governance-docs', 'content', path] as const,
};

/** Árvore dos documentos de governança em disco. */
export function useGovernanceDocsTree() {
  const api = useApi();
  return useQuery<GovernanceDocTree>({
    queryKey: governanceDocsKeys.tree,
    queryFn: () => api.listGovernanceDocs(),
  });
}

/** Conteúdo de um documento (habilitado apenas com um path selecionado). */
export function useGovernanceDoc(path: string | null) {
  const api = useApi();
  return useQuery<GovernanceDocContent>({
    queryKey: governanceDocsKeys.content(path ?? ''),
    queryFn: () => api.readGovernanceDoc(path!),
    enabled: path !== null,
  });
}

/** Salva (cria/sobrescreve) um documento e revalida árvore + conteúdo. */
export function useSaveGovernanceDoc() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ path, content }: { path: string; content: string }) =>
      api.saveGovernanceDoc(path, content),
    onSuccess: async (result) => {
      queryClient.setQueryData(governanceDocsKeys.content(result.path), result);
      await queryClient.invalidateQueries({ queryKey: governanceDocsKeys.tree });
    },
  });
}

/** Exclui um documento e revalida a árvore, limpando o conteúdo em cache. */
export function useDeleteGovernanceDoc() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (path: string) => api.deleteGovernanceDoc(path),
    onSuccess: async (_result, path) => {
      queryClient.removeQueries({ queryKey: governanceDocsKeys.content(path) });
      await queryClient.invalidateQueries({ queryKey: governanceDocsKeys.tree });
    },
  });
}
