import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { Conversation, UpdateInputMap } from '@/api';
import { useApi } from '@/app/api-context';

/** Query keys da feature de conversas. */
export const conversationKeys = {
  list: (projectId: string | null) => ['conversations', 'list', projectId ?? 'none'] as const,
};

/**
 * Conversas do projeto ativo. O filtro é aplicado no servidor; a tela
 * aplica apenas busca textual, período, autor e arquivadas/ativas.
 */
export function useConversations(projectId: string | null) {
  const api = useApi();
  return useQuery({
    queryKey: conversationKeys.list(projectId),
    queryFn: async (): Promise<Conversation[]> =>
      (await api.list('conversations', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
}

/** Renomear e arquivar/desarquivar — as únicas mutações permitidas (contrato). */
export function useUpdateConversation() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      input,
    }: {
      id: string;
      input: UpdateInputMap['conversations'];
    }): Promise<Conversation> => api.update('conversations', id, input),
    onSuccess: () =>
      void queryClient.invalidateQueries({ queryKey: ['conversations', 'list'] }),
  });
}
