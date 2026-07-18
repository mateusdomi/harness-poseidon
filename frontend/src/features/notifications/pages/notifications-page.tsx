import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BellOff, CheckCheck } from 'lucide-react';

import { notificationCategorySchema, type NotificationCategory } from '@/api';
import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { NotificationGroup } from '@/features/notifications/components/notification-group';
import { NotificationItem } from '@/features/notifications/components/notification-item';
import { NotificationPreferences } from '@/features/notifications/components/notification-preferences';
import {
  useMarkNotificationsRead,
  useMuteNotifications,
  useNotificationsCenter,
  useNotificationsRealtime,
} from '@/features/notifications/hooks/use-notifications';
import {
  effectiveStatus,
  filterNotifications,
  groupNotifications,
  sortByCreatedAtDesc,
  type NotificationStatusFilter,
} from '@/features/notifications/lib/notifications-derive';
import { useNow } from '@/features/shared/hooks/use-now';

/**
 * Central de notificações do perfil: itens ordenados por mais recente,
 * agrupamento/deduplicação visual por `groupKey`, ações embutidas (abrir
 * contexto, marcar como lida, silenciar — por item e em lote), filtros por
 * status efetivo e categoria, e preferências de silenciamento.
 */
export default function UnotificationsPage() {
  const { t } = useTranslation();
  const { profileId, notifications, settings, isPending, isError, refetch } =
    useNotificationsCenter();
  useNotificationsRealtime(profileId);
  const markRead = useMarkNotificationsRead();
  const mute = useMuteNotifications();
  const now = useNow();

  const [statusFilter, setStatusFilter] = useState<NotificationStatusFilter>('all');
  const [categoryFilter, setCategoryFilter] = useState<NotificationCategory | ''>('');

  // Filtra itens soltos primeiro; o agrupamento por `groupKey` vem depois.
  const entries = useMemo(() => {
    const filtered = filterNotifications(notifications, statusFilter, categoryFilter, settings);
    return groupNotifications(sortByCreatedAtDesc(filtered));
  }, [notifications, statusFilter, categoryFilter, settings]);

  const unreadIds = notifications
    .filter((notification) => effectiveStatus(notification, settings) === 'unread')
    .map((notification) => notification.id);

  const actionsPending = markRead.isPending || mute.isPending;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">
          {t('features.notifications.title')}
        </h1>
        {!isPending && !isError && (
          <span className="text-sm text-foreground-muted">
            {t('notifications.center.count', { count: notifications.length })}
          </span>
        )}
      </div>

      {isPending ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          {Array.from({ length: 3 }, (_, index) => (
            <Skeleton key={index} className="h-32 w-full" />
          ))}
        </div>
      ) : isError ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={refetch}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <>
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <label htmlFor="notifications-filter-status" className="text-xs font-medium">
                {t('notifications.filters.status')}
              </label>
              <Select
                id="notifications-filter-status"
                value={statusFilter}
                onChange={(event) =>
                  setStatusFilter(event.target.value as NotificationStatusFilter)
                }
              >
                <option value="all">{t('notifications.filters.statusAll')}</option>
                <option value="unread">{t('notifications.filters.statusUnread')}</option>
                <option value="muted">{t('notifications.filters.statusMuted')}</option>
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <label htmlFor="notifications-filter-category" className="text-xs font-medium">
                {t('notifications.filters.category')}
              </label>
              <Select
                id="notifications-filter-category"
                value={categoryFilter}
                onChange={(event) =>
                  setCategoryFilter(event.target.value as NotificationCategory | '')
                }
              >
                <option value="">{t('notifications.filters.allCategories')}</option>
                {notificationCategorySchema.options.map((category) => (
                  <option key={category} value={category}>
                    {t(`status.notificationCategory.${category}`)}
                  </option>
                ))}
              </Select>
            </div>
            {unreadIds.length > 0 && (
              <Button
                type="button"
                variant="outline"
                disabled={actionsPending}
                onClick={() => markRead.mutate(unreadIds)}
              >
                <CheckCheck aria-hidden="true" />
                {t('notifications.actions.markAllRead')}
              </Button>
            )}
          </div>

          {entries.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
                <BellOff aria-hidden="true" className="size-8 text-foreground-muted" />
                <h2 className="font-heading text-lg font-semibold">
                  {t('notifications.empty.title')}
                </h2>
                <p className="text-sm text-foreground-muted">
                  {t('notifications.empty.body')}
                </p>
              </CardContent>
            </Card>
          ) : (
            <ul
              className="flex flex-col gap-3"
              aria-label={t('notifications.center.label')}
            >
              {entries.map((entry) => (
                <li
                  key={entry.kind === 'single' ? entry.notification.id : entry.groupKey}
                  className="rounded-xl border border-border bg-surface p-4"
                >
                  {entry.kind === 'single' ? (
                    <NotificationItem
                      notification={entry.notification}
                      status={effectiveStatus(entry.notification, settings)}
                      now={now}
                      onMarkRead={(ids) => markRead.mutate(ids)}
                      onMute={(ids) => mute.mutate(ids)}
                      actionsPending={actionsPending}
                    />
                  ) : (
                    <NotificationGroup
                      group={entry}
                      settings={settings}
                      now={now}
                      onMarkRead={(ids) => markRead.mutate(ids)}
                      onMute={(ids) => mute.mutate(ids)}
                      actionsPending={actionsPending}
                    />
                  )}
                </li>
              ))}
            </ul>
          )}

          {settings && <NotificationPreferences settings={settings} />}
        </>
      )}
    </div>
  );
}
