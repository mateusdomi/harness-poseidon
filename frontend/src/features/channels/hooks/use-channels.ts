import { useQuery } from '@tanstack/react-query';

import type { ChannelLink, ChannelMessagePage, Ulid } from '@/api';
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

/** Histórico de mensagens de um canal (habilitado só quando há link selecionado). */
export function useChannelMessages(linkId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: linkId ? channelKeys.messages(linkId) : ['channels', 'messages', 'none'],
    enabled: linkId !== null,
    queryFn: async (): Promise<ChannelMessagePage> => api.listChannelMessages(linkId as Ulid),
  });
}
