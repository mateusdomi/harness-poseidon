import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { ChannelLink, ChannelMessagePage, CreateChannelLinkInput, Ulid } from '@/api';
import { useApi } from '@/app/api-context';

/** Query keys da feature de canais externos. */
export const channelKeys = {
  links: ['channels', 'links'] as const,
  messages: (linkId: Ulid) => ['channels', 'messages', linkId] as const,
};

/** Canais externos vinculados (ex.: Telegram) do tenant. */
export function useChannelLinks() {
  const api = useApi();
  return useQuery({
    queryKey: channelKeys.links,
    queryFn: async (): Promise<ChannelLink[]> => api.listChannelLinks(),
  });
}

/**
 * Vincula um canal externo (Telegram/Teams) a um projeto e revalida a lista.
 * Idempotente no backend por identidade — revincular devolve o mesmo vínculo.
 */
export function useCreateChannelLink() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateChannelLinkInput): Promise<ChannelLink> =>
      api.createChannelLink(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: channelKeys.links });
    },
  });
}

/** Histórico de mensagens de um canal (habilitado só quando há link selecionado). */
export function useChannelMessages(linkId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: linkId ? channelKeys.messages(linkId) : ['channels', 'messages', 'none'],
    enabled: linkId !== null,
    queryFn: async (): Promise<ChannelMessagePage> => api.listChannelMessages(linkId as Ulid),
  });
}
