import { AlertTriangle, CheckCircle2, Circle, Clock3, ShieldCheck } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import type { Gate, Phase, PhaseObligationProgress, Ulid } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { cn } from '@/lib/utils';

const STANDARD_PHASES = [
  'triage',
  'discovery',
  'architecture',
  'planning',
  'development',
  'testing',
  'homologation',
  'release',
  'support',
] as const;

type TimelineState = 'completed' | 'active' | 'future' | 'approval' | 'blocked' | 'correction';

function timelineState(
  phase: Phase,
  progress: PhaseObligationProgress | null,
  gates: readonly Gate[],
): TimelineState {
  const phaseGates = gates.filter((gate) => gate.phaseId === phase.id);
  if (phaseGates.some((gate) => gate.state === 'rejected') || phase.state === 'failed') {
    return 'correction';
  }
  if ((progress?.blocked ?? 0) > 0) return 'blocked';
  if (
    progress?.percentage === 100 &&
    phaseGates.some((gate) => gate.requiresApproval && gate.state === 'pending')
  ) {
    return 'approval';
  }
  if (phase.state === 'completed' || phase.state === 'skipped') return 'completed';
  if (phase.state === 'active') return 'active';
  return 'future';
}

export function WorkflowProgress({
  phases,
  currentPhase,
  progress,
  progressByPhaseId,
  gates,
  humanGateCount,
  isPending,
  isError,
}: {
  phases: Phase[];
  currentPhase: Phase | null;
  progress: PhaseObligationProgress | null;
  progressByPhaseId: ReadonlyMap<Ulid, PhaseObligationProgress | null>;
  gates: readonly Gate[];
  humanGateCount: number;
  isPending: boolean;
  isError: boolean;
}) {
  const { t } = useTranslation();
  const phaseByKey = new Map(
    phases.flatMap((phase) => {
      const key = STANDARD_PHASES[phase.order - 1];
      return key ? [[key, phase] as const] : [];
    }),
  );
  const percentage = progress?.percentage ?? 0;

  return (
    <Card className="lg:col-span-2">
      <CardHeader className="gap-1">
        <CardTitle>{t('cockpit.workflow.title')}</CardTitle>
        <p className="text-sm text-foreground-muted">{t('cockpit.workflow.subtitle')}</p>
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        <div
          className="overflow-x-auto pb-2"
          tabIndex={0}
          aria-label={t('cockpit.workflow.timelineLabel')}
        >
          <ol className="grid min-w-max auto-cols-[9rem] grid-flow-col gap-2 xl:min-w-0 xl:grid-cols-9">
            {STANDARD_PHASES.map((key, index) => {
              const phase = phaseByKey.get(key);
              const active = phase?.id === currentPhase?.id;
              const phaseProgress = phase ? progressByPhaseId.get(phase.id) ?? null : null;
              const state = phase ? timelineState(phase, phaseProgress, gates) : 'future';
              const phasePercentage = phaseProgress?.percentage ?? phase?.progress.percent ?? 0;
              return (
                <li
                  key={key}
                  aria-current={active ? 'step' : undefined}
                  className={cn(
                    'relative rounded-lg border p-3',
                    state === 'active' && 'border-brand bg-brand-soft/50 shadow-sm',
                    state === 'completed' && 'border-success/40 bg-success/5',
                    state === 'approval' && 'border-warning/50 bg-warning/5',
                    state === 'blocked' && 'border-error/50 bg-error/5',
                    state === 'correction' && 'border-warning/50 bg-warning/5',
                    state === 'future' && 'border-border bg-surface-elevated/30',
                  )}
                >
                  <span className="flex items-center gap-2 text-xs text-foreground-muted">
                    {state === 'completed' ? (
                      <CheckCircle2 aria-hidden="true" className="size-4 text-success" />
                    ) : state === 'active' ? (
                      <Clock3 aria-hidden="true" className="size-4 text-brand-strong" />
                    ) : state === 'blocked' || state === 'correction' ? (
                      <AlertTriangle aria-hidden="true" className="size-4 text-error" />
                    ) : state === 'approval' ? (
                      <ShieldCheck aria-hidden="true" className="size-4 text-warning" />
                    ) : (
                      <Circle aria-hidden="true" className="size-4" />
                    )}
                    {t('cockpit.workflow.phaseNumber', { number: index + 1 })}
                  </span>
                  {phase ? (
                    <Link
                      to="/workflows"
                      className="mt-2 block rounded-sm text-sm font-semibold underline-offset-4 hover:text-brand-strong hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                      aria-label={t('cockpit.workflow.openPhase', {
                        phase: t(`cockpit.workflow.phases.${key}`),
                      })}
                    >
                      {t(`cockpit.workflow.phases.${key}`)}
                    </Link>
                  ) : (
                    <span className="mt-2 block text-sm font-semibold">
                      {t(`cockpit.workflow.phases.${key}`)}
                    </span>
                  )}
                  <div className="mt-2 flex items-center justify-between gap-2">
                    <Badge
                      variant={
                        state === 'completed'
                          ? 'success'
                          : state === 'active'
                            ? 'brand'
                            : state === 'blocked'
                              ? 'error'
                              : state === 'approval' || state === 'correction'
                                ? 'warning'
                                : 'outline'
                      }
                    >
                      {t(`cockpit.workflow.states.${state}`)}
                    </Badge>
                    <span className="text-xs tabular-nums text-foreground-muted">
                      {phasePercentage}%
                    </span>
                  </div>
                </li>
              );
            })}
          </ol>
        </div>

        {currentPhase ? (
          <section className="rounded-xl border border-border bg-surface-elevated/30 p-4">
            <div className="flex flex-wrap items-end justify-between gap-2">
              <div>
                <p className="text-xs font-medium uppercase tracking-wide text-foreground-muted">
                  {t('cockpit.workflow.currentPhase')}
                </p>
                <h3 className="font-heading text-xl font-semibold">{currentPhase.name}</h3>
              </div>
              <span className="font-heading text-3xl font-semibold tabular-nums">
                {isPending ? '—' : `${percentage}%`}
              </span>
            </div>
            <div
              role="progressbar"
              aria-label={t('cockpit.workflow.acceptedProgress', { phase: currentPhase.name })}
              aria-valuenow={percentage}
              aria-valuemin={0}
              aria-valuemax={100}
              className="mt-3 h-3 overflow-hidden rounded-full bg-surface-elevated"
            >
              <div
                className="h-full rounded-full bg-gradient-brand transition-[width]"
                style={{ width: `${percentage}%` }}
              />
            </div>
            <p className="mt-2 text-xs text-foreground-muted">
              {isError
                ? t('cockpit.workflow.progressError')
                : progress
                  ? t('cockpit.workflow.acceptedFraction', {
                      accepted: progress.requiredAccepted,
                      total: progress.requiredTotal,
                    })
                  : t('cockpit.workflow.planUnavailable')}
            </p>
            <dl className="mt-4 grid grid-cols-2 gap-2 md:grid-cols-4">
              <ProgressSignal
                label={t('cockpit.workflow.signals.inProgress')}
                value={progress?.inProgress ?? 0}
                icon={Clock3}
              />
              <ProgressSignal
                label={t('cockpit.workflow.signals.inReview')}
                value={progress?.inReview ?? 0}
                icon={ShieldCheck}
              />
              <ProgressSignal
                label={t('cockpit.workflow.signals.blocked')}
                value={progress?.blocked ?? 0}
                icon={AlertTriangle}
                alert
              />
              <ProgressSignal
                label={t('cockpit.workflow.signals.humanGates')}
                value={humanGateCount}
                icon={Circle}
              />
            </dl>
          </section>
        ) : (
          <p className="text-sm text-foreground-muted">{t('cockpit.phase.noRun')}</p>
        )}
      </CardContent>
    </Card>
  );
}

function ProgressSignal({
  label,
  value,
  icon: Icon,
  alert = false,
}: {
  label: string;
  value: number;
  icon: typeof Clock3;
  alert?: boolean;
}) {
  return (
    <div className="rounded-lg border border-border bg-surface p-3">
      <dt className="flex items-center gap-1.5 text-xs text-foreground-muted">
        <Icon aria-hidden="true" className={cn('size-3.5', alert && value > 0 && 'text-error')} />
        {label}
      </dt>
      <dd className={cn('mt-1 text-xl font-semibold tabular-nums', alert && value > 0 && 'text-error')}>
        {value}
      </dd>
    </div>
  );
}
