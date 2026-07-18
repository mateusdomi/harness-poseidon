import * as React from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router-dom';
import { Bell } from 'lucide-react';

import { streams } from '@/api';
import { useApi, useRealtime } from '@/app/api-context';
import { Button } from '@/design-system';

const UNREAD_QUERY_KEY = ['notifications', 'unread'] as const;

export function NotificationsButton() {
  const { t } = useTranslation();
  const api = useApi();
  const realtime = useRealtime();
  const queryClient = useQueryClient();

  const { data } = useQuery({
    queryKey: UNREAD_QUERY_KEY,
    queryFn: () => api.list('notifications', { filter: { status: 'unread' }, limit: 50 }),
  });

  // Badge vivo: invalida a contagem quando chega notification.created.
  React.useEffect(() => {
    let unsubscribe: (() => void) | undefined;
    let cancelled = false;
    void api.getCurrentProfile().then((profile) => {
      if (cancelled) return;
      const subscription = realtime.subscribe(streams.profile(profile.id), (event) => {
        if (event.type === 'notification.created') {
          void queryClient.invalidateQueries({ queryKey: UNREAD_QUERY_KEY });
        }
      });
      unsubscribe = () => subscription.unsubscribe();
    });
    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, [api, realtime, queryClient]);

  const unread = data?.items.length ?? 0;

  return (
    <Button variant="ghost" size="icon" asChild>
      <NavLink to="/notifications" aria-label={t('shell.notifications.label')}>
        <span className="relative inline-flex">
          <Bell aria-hidden="true" />
          {unread > 0 && (
            <span
              className="absolute -right-2 -top-2 inline-flex min-w-5 items-center justify-center rounded-full bg-brand-magenta px-1 text-[10px] font-semibold text-white"
              aria-label={t('shell.notifications.unread', { count: unread })}
            >
              {unread}
            </span>
          )}
        </span>
      </NavLink>
    </Button>
  );
}
