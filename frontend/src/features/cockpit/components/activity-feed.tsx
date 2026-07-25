import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import {
  AlertTriangle,
  CheckCircle2,
  Info,
  XCircle,
  type LucideIcon,
} from 'lucide-react';

import type { AuditEvent } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import {
  ACTIVITY_PERIODS,
  filterActivityByPeriod,
  type ActivityPeriod,
} from '@/features/cockpit/lib/cockpit-derive';
import {
  humanizeActivity,
  isMeaningfulActivity,
  type ActivityOutcome,
} from '@/features/cockpit/lib/activity-humanize';
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

const OUTCOME_ICONS: Record<ActivityOutcome, LucideIcon> = {
  success: CheckCircle2,
  error: XCircle,
  warning: AlertTriangle,
  neutral: Info,
};

const OUTCOME_CLASSES: Record<ActivityOutcome, string> = {
  success: 'text-success',
  error: 'text-error',
  warning: 'text-warning',
  neutral: 'text-foreground-muted',
};

/**
 * Item da atividade: ator, horário, mensagem humana, resultado (ícone), link
 * para o objeto e disclosure com o detalhe técnico (código cru + alvo), que é
 * a única superfície onde o código aparece literalmente.
 */
function ActivityItem({ event }: { event: AuditEvent }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const humanized = humanizeActivity(event);
  const OutcomeIcon = OUTCOME_ICONS[humanized.outcome];

  // Códigos técnicos nunca vazam para a linha principal. O detalhe do
  // servidor é preservado quando existe; o código cru fica no disclosure.
  const message = humanized.labelKey
    ? t(humanized.labelKey)
    : (event.detail ?? t('cockpit.activity.unknown'));
  // O `detail` identifica o objeto ("Projeto Poseidon"). Quando já usamos o
  // rótulo humano, ele continua visível como o objeto do evento — humanizar
  // não pode esconder qual objeto foi afetado.
  const objectLine = humanized.labelKey ? event.detail : null;

  return (
    <li className="flex flex-col gap-1 border-l-2 border-border pl-3">
      <span className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted">
        <OutcomeIcon
          aria-hidden="true"
          className={cn('size-3.5 shrink-0', OUTCOME_CLASSES[humanized.outcome])}
        />
        <span className="sr-only">{t(`cockpit.activity.outcome.${humanized.outcome}`)}</span>
        <Badge variant={ACTOR_VARIANTS[event.actorKind]}>
          {t(`status.auditActorKind.${event.actorKind}`)}
        </Badge>
        <time dateTime={event.occurredAt}>{formatRelativeTime(event.occurredAt)}</time>
      </span>
      <span className="text-sm">{message}</span>
      {objectLine ? (
        <span className="text-sm text-foreground-muted">{objectLine}</span>
      ) : null}
      <span className="flex flex-wrap items-center gap-3">
        {humanized.link ? (
          <Link
            to={humanized.link}
            className="text-xs text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            {t('cockpit.activity.openTarget')}
          </Link>
        ) : null}
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
          className="text-xs text-foreground-muted underline-offset-4 hover:text-foreground hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          {open ? t('cockpit.activity.hideDetails') : t('cockpit.activity.showDetails')}
        </button>
      </span>
      {open ? (
        <dl className="mt-1 flex flex-col gap-1 rounded-md bg-surface-elevated p-2 text-xs">
          <div className="flex flex-wrap gap-2">
            <dt className="text-foreground-muted">{t('cockpit.activity.details.action')}</dt>
            <dd>
              <code className="font-mono">{humanized.rawAction}</code>
            </dd>
          </div>
          <div className="flex flex-wrap gap-2">
            <dt className="text-foreground-muted">{t('cockpit.activity.details.target')}</dt>
            <dd>
              <code className="font-mono">{humanized.targetType}</code>
            </dd>
          </div>
          {event.detail ? (
            <div className="flex flex-wrap gap-2">
              <dt className="text-foreground-muted">{t('cockpit.activity.details.detail')}</dt>
              <dd className="break-words">{event.detail}</dd>
            </div>
          ) : null}
        </dl>
      ) : null}
    </li>
  );
}

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

  // "Visão de dono": o feed mostra o que muda a operação (tarefas, documentos,
  // aprovações, conversas) e ESCONDE o vaivém técnico interno (turnos do chefe,
  // mensagens anexadas, heartbeats). O histórico cru fica na Governança.
  const meaningful = events.filter(isMeaningfulActivity);
  const hiddenNoise = events.length - meaningful.length;
  const filtered = filterActivityByPeriod(meaningful, period, now);
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
                'min-h-11 rounded-md px-3 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent sm:min-h-0 sm:py-1.5',
                period === option
                  ? 'bg-surface-elevated font-semibold text-foreground shadow-sm ring-1 ring-inset ring-border-strong'
                  : 'font-medium text-foreground-muted hover:text-foreground',
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
              {meaningful.length === 0
                ? t('cockpit.activity.empty')
                : t('cockpit.activity.emptyPeriod', {
                    period: t(`cockpit.activity.period.options.${period}`).toLowerCase(),
                  })}
            </p>
            {meaningful.length > 0 && (
              <p className="text-xs text-foreground-muted">
                {t('cockpit.activity.emptyPeriodHint')}
              </p>
            )}
          </div>
        ) : (
          <>
            <ol className="flex flex-col gap-3">
              {visible.map((event) => (
                <ActivityItem key={event.id} event={event} />
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
        {hiddenNoise > 0 && (
          <p className="text-xs text-foreground-muted">{t('cockpit.activity.noise')}</p>
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
