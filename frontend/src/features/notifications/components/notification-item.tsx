import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { BellOff, Check, ExternalLink } from 'lucide-react';

import type { Notification, NotificationStatus, Ulid } from '@/api';
import { Badge, Button } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';
import { notificationSeverityVariant, notificationStatusVariant } from '@/lib/status';

export interface NotificationItemProps {
  notification: Notification;
  /** Status efetivo de exibição (preferências já aplicadas). */
  status: NotificationStatus;
  now: Date;
  /** Marcar como lida / silenciar (ids em lote; aqui um item só). */
  onMarkRead: (ids: Ulid[]) => void;
  onMute: (ids: Ulid[]) => void;
  actionsPending?: boolean;
}

/**
 * Item da central: severidade e status (badges), categoria, título, corpo,
 * tempo relativo e ações embutidas (abrir contexto, marcar como lida,
 * silenciar). O link navega para o deep-link do domínio (ex.: /approvals).
 */
export function NotificationItem({
  notification,
  status,
  now,
  onMarkRead,
  onMute,
  actionsPending = false,
}: NotificationItemProps) {
  const { t, i18n } = useTranslation();

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={notificationSeverityVariant(notification.severity)}>
          {t(`status.notificationSeverity.${notification.severity}`)}
        </Badge>
        <span className="text-xs font-medium text-foreground-muted">
          {t(`status.notificationCategory.${notification.category}`)}
        </span>
        <Badge variant={notificationStatusVariant(status)}>
          {t(`status.notificationStatus.${status}`)}
        </Badge>
        <span className="ml-auto text-xs text-foreground-muted">
          {t('notifications.item.receivedAt', {
            when: formatRelativeTime(notification.createdAt, i18n.language, now),
          })}
        </span>
      </div>

      <div className="flex flex-col gap-1">
        <h3 className="text-sm font-semibold">{notification.title}</h3>
        <p className="text-xs text-foreground-muted">{notification.body}</p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {notification.link !== null && (
          <Button variant="ghost" size="sm" asChild>
            <Link to={notification.link}>
              <ExternalLink aria-hidden="true" />
              {t('notifications.item.openLink')}
            </Link>
          </Button>
        )}
        {status === 'unread' && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={actionsPending}
            onClick={() => onMarkRead([notification.id])}
          >
            <Check aria-hidden="true" />
            {t('notifications.item.markRead')}
          </Button>
        )}
        {status !== 'muted' && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={actionsPending}
            onClick={() => onMute([notification.id])}
          >
            <BellOff aria-hidden="true" />
            {t('notifications.item.mute')}
          </Button>
        )}
      </div>
    </div>
  );
}
