import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import type { AuditEvent } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import {
  ACTIVITY_PERIODS,
  filterActivityByPeriod,
  type ActivityPeriod,
} from '@/features/cockpit/lib/cockpit-derive';
import { useNow } from '@/features/shared/hooks/use-now';
import { formatRelativeTime } from '@/lib/format';
import { cn } from '@/lib/utils';

const ACTOR_VARIANTS = {
  user: 'brand',
  chief: 'info',
  agent: 'default',
  system: 'outline',
} as const;

/** Lote do carregamento incremental ("carregar mais"). */
const ACTIVITY_BATCH_SIZE = 8;

/**
 * Atividade recente: timeline de auditoria com recorte por período
 * (24h padrão, 3d, 7d) e carregamento incremental em lotes — NÃO
 * paginação tradicional, porque o feed é realtime/timeline (D-070).
 * Quadro e Governança seguem como fontes históricas completas.
 */
export function ActivityFeed({ events }: { events: AuditEvent[] }) {
  const { t } = useTranslation();
  // Relógio compartilhado: a janela do período se move sozinha (30s).
  const now = useNow();
  const [period, setPeriod] = useState<ActivityPeriod>('24h');
  const [visibleCount, setVisibleCount] = useState(ACTIVITY_BATCH_SIZE);

  const filtered = filterActivityByPeriod(events, period, now);
  const visible = filtered.slice(0, visibleCount);
  const remaining = filtered.length - visible.length;

  function selectPeriod(next: ActivityPeriod) {
    setPeriod(next);
    setVisibleCount(ACTIVITY_BATCH_SIZE);
  }

  return (
    <Card>
      <CardHeader className="flex flex-wrap items-center gap-3 sm:flex-row">
        <CardTitle>{t('cockpit.activity.title')}</CardTitle>
        <div
          role="group"
          aria-label={t('cockpit.activity.period.label')}
          className="ml-auto flex rounded-lg border border-border bg-surface p-0.5"
        >
          {ACTIVITY_PERIODS.map((option) => (
            <button
              key={option}
              type="button"
              aria-pressed={period === option}
              onClick={() => selectPeriod(option)}
              className={cn(
                'min-h-11 rounded-md px-3 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent sm:min-h-0 sm:py-1.5',
                period === option
                  ? 'bg-surface-elevated text-foreground'
                  : 'text-foreground-muted hover:text-foreground',
              )}
            >
              {t(`cockpit.activity.period.options.${option}`)}
            </button>
          ))}
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {visible.length === 0 ? (
          <div className="flex flex-col gap-1">
            <p className="text-sm text-foreground-muted">
              {events.length === 0
                ? t('cockpit.activity.empty')
                : t('cockpit.activity.emptyPeriod', {
                    period: t(`cockpit.activity.period.options.${period}`).toLowerCase(),
                  })}
            </p>
            {events.length > 0 && (
              <p className="text-xs text-foreground-muted">
                {t('cockpit.activity.emptyPeriodHint')}
              </p>
            )}
          </div>
        ) : (
          <>
            <ol className="flex flex-col gap-3">
              {visible.map((event) => (
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
            <div className="flex flex-wrap items-center gap-3">
              <span className="text-xs text-foreground-muted">
                {t('cockpit.activity.showing', { shown: visible.length, total: filtered.length })}
              </span>
              {remaining > 0 && (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => setVisibleCount((count) => count + ACTIVITY_BATCH_SIZE)}
                >
                  {t('cockpit.activity.loadMore', {
                    count: Math.min(remaining, ACTIVITY_BATCH_SIZE),
                  })}
                </Button>
              )}
            </div>
          </>
        )}
        <Link
          to="/governance"
          className="self-start text-xs text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          {t('cockpit.activity.fullHistory')}
        </Link>
      </CardContent>
    </Card>
  );
}
