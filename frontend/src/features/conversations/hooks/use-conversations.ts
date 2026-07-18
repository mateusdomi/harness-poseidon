import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { Conversation, UpdateInputMap } from '@/api';
import { useApi } from '@/app/api-context';

/** Query keys da feature de conversas. */
export const conversationKeys = {
  list: ['conversations', 'list'] as const,
};

/** Todas as conversas (ativas e arquivadas — o filtro é da tela). */
export function useConversations() {
  const api = useApi();
  return useQuery({
    queryKey: conversationKeys.list,
    queryFn: async (): Promise<Conversation[]> => (await api.list('conversations')).items,
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
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: conversationKeys.list }),
  });
}
