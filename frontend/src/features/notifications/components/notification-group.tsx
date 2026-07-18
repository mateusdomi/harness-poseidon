import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BellOff, Check, ChevronDown, ChevronUp } from 'lucide-react';

import type { Settings, Ulid } from '@/api';
import { Badge, Button } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';
import { notificationSeverityVariant, notificationStatusVariant } from '@/lib/status';
import { NotificationItem } from '@/features/notifications/components/notification-item';
import {
  effectiveStatus,
  type NotificationEntry,
} from '@/features/notifications/lib/notifications-derive';

export interface NotificationGroupProps {
  /** Card de grupo derivado por `groupNotifications` (mesmo `groupKey`). */
  group: Extract<NotificationEntry, { kind: 'group' }>;
  settings: Pick<Settings, 'notificationsEnabled' | 'mutedCategories'> | undefined;
  now: Date;
  onMarkRead: (ids: Ulid[]) => void;
  onMute: (ids: Ulid[]) => void;
  actionsPending?: boolean;
}

/**
 * Card de grupo (deduplicação): o item mais recente representa o grupo e um
 * badge mostra o total de ocorrências deduplicadas. Os demais itens ficam
 * expansíveis; as ações em lote (ler/silenciar) valem para o grupo inteiro.
 */
export function NotificationGroup({
  group,
  settings,
  now,
  onMarkRead,
  onMute,
  actionsPending = false,
}: NotificationGroupProps) {
  const { t, i18n } = useTranslation();
  const [expanded, setExpanded] = useState(false);

  const representative = group.notifications[0];
  const representativeStatus = effectiveStatus(representative, settings);
  const unreadIds = group.notifications
    .filter((item) => effectiveStatus(item, settings) === 'unread')
    .map((item) => item.id);
  const mutableIds = group.notifications
    .filter((item) => effectiveStatus(item, settings) !== 'muted')
    .map((item) => item.id);
  const rest = group.notifications.slice(1);

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={notificationSeverityVariant(representative.severity)}>
          {t(`status.notificationSeverity.${representative.severity}`)}
        </Badge>
        <span className="text-xs font-medium text-foreground-muted">
          {t(`status.notificationCategory.${representative.category}`)}
        </span>
        <Badge variant={notificationStatusVariant(representativeStatus)}>
          {t(`status.notificationStatus.${representativeStatus}`)}
        </Badge>
        <Badge variant="brand">
          {t('notifications.group.dedupe', { count: group.dedupeCount })}
        </Badge>
        <span className="ml-auto text-xs text-foreground-muted">
          {t('notifications.item.receivedAt', {
            when: formatRelativeTime(representative.createdAt, i18n.language, now),
          })}
        </span>
      </div>

      <div className="flex flex-col gap-1">
        <h3 className="text-sm font-semibold">{representative.title}</h3>
        <p className="text-xs text-foreground-muted">{representative.body}</p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {unreadIds.length > 0 && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={actionsPending}
            onClick={() => onMarkRead(unreadIds)}
          >
            <Check aria-hidden="true" />
            {t('notifications.group.markRead')}
          </Button>
        )}
        {mutableIds.length > 0 && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={actionsPending}
            onClick={() => onMute(mutableIds)}
          >
            <BellOff aria-hidden="true" />
            {t('notifications.group.mute')}
          </Button>
        )}
        {rest.length > 0 && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            aria-expanded={expanded}
            onClick={() => setExpanded((current) => !current)}
          >
            {expanded ? <ChevronUp aria-hidden="true" /> : <ChevronDown aria-hidden="true" />}
            {expanded
              ? t('notifications.group.hideItems')
              : t('notifications.group.showItems')}
          </Button>
        )}
      </div>

      {expanded && (
        <ul className="flex flex-col gap-3 border-t border-border pt-3">
          {rest.map((item) => (
            <li key={item.id} className="rounded-lg border border-border p-3">
              <NotificationItem
                notification={item}
                status={effectiveStatus(item, settings)}
                now={now}
                onMarkRead={onMarkRead}
                onMute={onMute}
                actionsPending={actionsPending}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
