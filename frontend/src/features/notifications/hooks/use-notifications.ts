import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type Notification,
  type Settings,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da central de notificações. */
export const notificationKeys = {
  profile: ['notifications', 'profile'] as const,
  list: (profileId: Ulid) => ['notifications', 'list', profileId] as const,
  settings: (profileId: Ulid) => ['notifications', 'settings', profileId] as const,
};

/** Prefixo comum: invalida a central E o badge ['notifications','unread'] do shell. */
const NOTIFICATIONS_PREFIX = ['notifications'] as const;

/**
 * Central: perfil atual → notificações do perfil + settings (preferências
 * de silenciamento), ambos dependentes do profileId.
 */
export function useNotificationsCenter() {
  const api = useApi();

  const profileQuery = useQuery({
    queryKey: notificationKeys.profile,
    queryFn: () => api.getCurrentProfile(),
  });
  const profileId = profileQuery.data?.id;

  const notificationsQuery = useQuery({
    queryKey: notificationKeys.list(profileId ?? ''),
    enabled: profileId !== undefined,
    queryFn: async (): Promise<Notification[]> =>
      (await api.list('notifications', { filter: { profileId } })).items,
  });

  const settingsQuery = useQuery({
    queryKey: notificationKeys.settings(profileId ?? ''),
    enabled: profileId !== undefined,
    queryFn: async (): Promise<Settings | undefined> =>
      (await api.list('settings', { filter: { profileId } })).items[0],
  });

  return {
    profileId,
    notifications: notificationsQuery.data ?? [],
    settings: settingsQuery.data,
    isPending:
      profileQuery.isPending ||
      (profileId !== undefined && (notificationsQuery.isPending || settingsQuery.isPending)),
    isError: profileQuery.isError || notificationsQuery.isError || settingsQuery.isError,
    refetch: () => {
      void profileQuery.refetch();
      void notificationsQuery.refetch();
      void settingsQuery.refetch();
    },
  };
}

/**
 * Tempo real: `notification.created` no stream do perfil invalida a central
 * e o badge do shell (mesmo prefixo de query key).
 */
export function useNotificationsRealtime(profileId: Ulid | undefined) {
  useRealtimeStream(profileId === undefined ? null : streams.profile(profileId), {
    types: ['notification.created'],
    invalidate: [NOTIFICATIONS_PREFIX],
  });
}

/** Marca notificações como lidas; invalida central + badge do shell. */
export function useMarkNotificationsRead() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (ids: Ulid[]) => api.markNotificationsRead(ids),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: NOTIFICATIONS_PREFIX }),
  });
}

/** Silencia notificações; invalida central + badge do shell. */
export function useMuteNotifications() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (ids: Ulid[]) => api.muteNotifications(ids),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: NOTIFICATIONS_PREFIX }),
  });
}

/**
 * Persiste preferências de notificação (toggle global + categorias mutadas)
 * via update parcial de settings.
 */
export function useUpdateNotificationSettings() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      settingsId,
      input,
    }: {
      settingsId: Ulid;
      input: Partial<Pick<Settings, 'notificationsEnabled' | 'mutedCategories'>>;
    }) => api.update('settings', settingsId, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: NOTIFICATIONS_PREFIX }),
  });
}
