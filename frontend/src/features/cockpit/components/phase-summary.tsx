import { useTranslation } from 'react-i18next';
import { Info } from 'lucide-react';

import type { Gate, Phase } from '@/api';
import {
  Badge,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Tooltip,
  type BadgeProps,
} from '@/design-system';
import { formatNumber } from '@/lib/format';
import { gateStateVariant, phaseStateVariant } from '@/lib/status';
import { gateOfPhase } from '@/features/cockpit/lib/cockpit-derive';

interface PhaseSummaryProps {
  phase: Phase | null;
  gates: Gate[];
}

const DELIVERABLE_VARIANTS: Record<
  Phase['deliverables'][number]['status'],
  BadgeProps['variant']
> = {
  planned: 'outline',
  notStarted: 'outline',
  inProduction: 'info',
  inReview: 'brand',
  approved: 'success',
  rejected: 'error',
};

/** Fase atual: somente progresso, gate e entregáveis específicos desta fase. */
export function PhaseSummary({ phase, gates }: PhaseSummaryProps) {
  const { t } = useTranslation();

  if (!phase) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>{t('cockpit.phase.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-foreground-muted">{t('cockpit.phase.noRun')}</p>
        </CardContent>
      </Card>
    );
  }

  const gate = gateOfPhase(gates, phase);
  const progressTooltip = t('cockpit.phase.progressTooltip', {
    completed: phase.progress.completed,
    total: phase.progress.total,
    tasksDone: phase.progress.tasks.completed,
    tasksTotal: phase.progress.tasks.total,
    documentsDone: phase.progress.documents.completed,
    documentsTotal: phase.progress.documents.total,
    gatesDone: phase.progress.gates.completed,
    gatesTotal: phase.progress.gates.total,
  });

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-center gap-2">
          <CardTitle>{phase.name}</CardTitle>
          <Badge variant={phaseStateVariant(phase.state)}>
            {t(`status.phaseState.${phase.state}`)}
          </Badge>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-foreground-muted">{t('cockpit.phase.gate')}</span>
          {gate ? (
            <>
              <span className="font-medium">{gate.name}</span>
              <Badge variant={gateStateVariant(gate.state)}>
                {t(`status.gateState.${gate.state}`)}
              </Badge>
            </>
          ) : (
            <span className="text-foreground-muted">{t('cockpit.phase.noGate')}</span>
          )}
        </div>
        <div className="flex items-center gap-2">
          <div
            role="progressbar"
            aria-label={t('cockpit.phase.progressLabel', { phase: phase.name })}
            aria-valuenow={phase.progress.percent}
            aria-valuemin={0}
            aria-valuemax={100}
            className="h-2 flex-1 overflow-hidden rounded-full bg-surface-elevated"
          >
            <div
              className="h-full rounded-full bg-brand"
              style={{ width: `${phase.progress.percent}%` }}
            />
          </div>
          <span className="font-medium tabular-nums">
            {formatNumber(phase.progress.percent)}%
          </span>
          <Tooltip label={progressTooltip}>
            <button
              type="button"
              aria-label={progressTooltip}
              className="rounded-full text-foreground-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
              <Info aria-hidden="true" className="size-4" />
            </button>
          </Tooltip>
        </div>
        <p className="text-xs text-foreground-muted">
          {t('cockpit.phase.progressFraction', {
            completed: phase.progress.completed,
            total: phase.progress.total,
          })}
        </p>
        <div className="flex flex-col gap-2">
          <span className="text-sm font-medium">{t('cockpit.phase.deliverables')}</span>
          {phase.deliverables.length === 0 ? (
            <p className="text-xs text-foreground-muted">
              {t('cockpit.phase.deliverablesUnavailable')}
            </p>
          ) : (
            <ul className="flex flex-col gap-1.5">
              {phase.deliverables.map((deliverable) => (
                <li
                  key={deliverable.name}
                  className="flex flex-wrap items-center justify-between gap-2 text-sm"
                >
                  <span>{deliverable.name}</span>
                  <Badge variant={DELIVERABLE_VARIANTS[deliverable.status]}>
                    {t(`chat.workflowPanel.health.${deliverable.status}`)}
                  </Badge>
                </li>
              ))}
            </ul>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
