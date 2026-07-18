import { useTranslation } from 'react-i18next';

import type { AuditEvent } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';

const ACTOR_VARIANTS = {
  user: 'brand',
  chief: 'info',
  agent: 'default',
  system: 'outline',
} as const;

/** Atividade recente (timeline de auditoria, mais recente primeiro). */
export function ActivityFeed({ events }: { events: AuditEvent[] }) {
  const { t } = useTranslation();

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('cockpit.activity.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        {events.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.activity.empty')}</p>
        ) : (
          <ol className="flex flex-col gap-3">
            {events.map((event) => (
              <li key={event.id} className="flex flex-col gap-1 border-l-2 border-border pl-3">
                <span className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted">
                  <Badge variant={ACTOR_VARIANTS[event.actorKind]}>
                    {t(`status.auditActorKind.${event.actorKind}`)}
                  </Badge>
                  <time dateTime={event.occurredAt}>{formatRelativeTime(event.occurredAt)}</time>
                </span>
                <span className="text-sm">{event.detail ?? event.action}</span>
              </li>
            ))}
          </ol>
        )}
      </CardContent>
    </Card>
  );
}
