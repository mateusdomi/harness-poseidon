import { Check, Circle, Play } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Gate, Phase } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

function obligationName(name: string): string {
  const withoutTechnicalPrefix = name
    .replace(/\bgates?\s+(?:de|da|do)\s+/giu, '')
    .replace(/\bgates?\b/giu, '')
    .trim();

  return withoutTechnicalPrefix || name;
}

function PhaseMarker({ state }: { state: Phase['state'] }) {
  if (state === 'completed' || state === 'skipped') {
    return <Check aria-hidden="true" className="size-4" />;
  }
  if (state === 'active') {
    return <Play aria-hidden="true" className="size-3.5 fill-current" />;
  }
  return <Circle aria-hidden="true" className="size-3 fill-current" />;
}

export function ProjectTimeline({
  phases,
  gates,
  overallProgress,
}: {
  phases: Phase[];
  gates: Gate[];
  overallProgress: number;
}) {
  const { t } = useTranslation();
  const orderedPhases = useMemo(
    () => [...phases].sort((left, right) => left.order - right.order),
    [phases],
  );
  const defaultPhaseId =
    orderedPhases.find((phase) => phase.state === 'active')?.id ??
    orderedPhases.find((phase) => phase.state === 'pending')?.id ??
    orderedPhases.at(-1)?.id ??
    null;
  const [selectedPhaseId, setSelectedPhaseId] = useState<string | null>(defaultPhaseId);

  useEffect(() => {
    if (!selectedPhaseId || !orderedPhases.some((phase) => phase.id === selectedPhaseId)) {
      setSelectedPhaseId(defaultPhaseId);
    }
  }, [defaultPhaseId, orderedPhases, selectedPhaseId]);

  if (orderedPhases.length === 0) {
    return (
      <Card className="lg:col-span-2">
        <CardHeader>
          <CardTitle>{t('cockpit.timeline.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-foreground-muted">{t('cockpit.timeline.empty')}</p>
        </CardContent>
      </Card>
    );
  }

  const selectedPhase =
    orderedPhases.find((phase) => phase.id === selectedPhaseId) ?? orderedPhases[0];
  const phaseGates = gates.filter((gate) => gate.phaseId === selectedPhase.id);

  return (
    <Card className="lg:col-span-2">
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
        <div className="flex flex-col gap-1">
          <CardTitle>{t('cockpit.timeline.title')}</CardTitle>
          <p className="text-xs text-foreground-muted">{t('cockpit.timeline.subtitle')}</p>
        </div>
        <div className="flex items-baseline gap-2">
          <span className="font-heading text-3xl font-semibold tabular-nums">
            {formatNumber(overallProgress)}%
          </span>
          <span className="text-xs text-foreground-muted">
            {t('cockpit.timeline.overallProgress')}
          </span>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        <div className="overflow-x-auto pb-2">
          <ol
            className="grid min-w-max auto-cols-[minmax(8.5rem,1fr)] grid-flow-col"
            aria-label={t('cockpit.timeline.ariaLabel')}
          >
            {orderedPhases.map((phase, index) => {
              const selected = phase.id === selectedPhase.id;
              const completed = phase.state === 'completed' || phase.state === 'skipped';
              const previousPhase = orderedPhases[index - 1];
              const previousCompleted =
                previousPhase?.state === 'completed' || previousPhase?.state === 'skipped';
              return (
                <li key={phase.id} className="relative flex min-w-36 flex-col items-center px-2">
                  {index > 0 && (
                    <span
                      aria-hidden="true"
                      className={cn(
                        'absolute right-1/2 top-4 h-0.5 w-full',
                        previousCompleted ? 'bg-success' : 'bg-border',
                      )}
                    />
                  )}
                  <button
                    type="button"
                    aria-pressed={selected}
                    aria-label={t('cockpit.timeline.openStage', { stage: phase.name })}
                    onClick={() => setSelectedPhaseId(phase.id)}
                    className="group relative z-10 flex max-w-40 flex-col items-center gap-2 rounded-lg px-2 py-1 text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    <span
                      className={cn(
                        'inline-flex size-8 items-center justify-center rounded-full border-2 transition-colors',
                        completed && 'border-success bg-success text-success-foreground',
                        phase.state === 'active' &&
                          'border-brand bg-brand text-primary-foreground shadow-glow',
                        !completed &&
                          phase.state !== 'active' &&
                          'border-border bg-surface-elevated text-foreground-muted',
                        selected && 'ring-2 ring-accent ring-offset-2 ring-offset-surface',
                      )}
                    >
                      <PhaseMarker state={phase.state} />
                    </span>
                    <span
                      className={cn(
                        'text-xs font-medium',
                        selected ? 'text-foreground' : 'text-foreground-muted',
                      )}
                    >
                      {phase.name}
                    </span>
                  </button>
                </li>
              );
            })}
          </ol>
        </div>

        <section
          className="rounded-lg border border-border bg-surface-elevated/40 p-4"
          aria-live="polite"
          aria-labelledby="cockpit-selected-stage"
        >
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 id="cockpit-selected-stage" className="font-heading font-semibold">
              {selectedPhase.name}
            </h3>
            <Badge
              variant={
                selectedPhase.state === 'completed' || selectedPhase.state === 'skipped'
                  ? 'success'
                  : selectedPhase.state === 'active'
                    ? 'brand'
                    : selectedPhase.state === 'failed'
                      ? 'error'
                      : 'outline'
              }
            >
              {t(`cockpit.timeline.state.${selectedPhase.state}`)}
            </Badge>
          </div>
          <p className="mt-3 text-sm font-medium">{t('cockpit.timeline.obligations')}</p>
          {phaseGates.length === 0 && selectedPhase.deliverables.length === 0 ? (
            <p className="mt-1 text-sm text-foreground-muted">
              {t('cockpit.timeline.noObligations')}
            </p>
          ) : (
            <ul className="mt-2 grid gap-2 md:grid-cols-2">
              {phaseGates.map((gate) => (
                <li
                  key={gate.id}
                  className="flex items-center justify-between gap-3 rounded-md border border-border bg-surface p-3 text-sm"
                >
                  <span>{obligationName(gate.name)}</span>
                  <Badge variant={gate.state === 'approved' ? 'success' : 'outline'}>
                    {t(`cockpit.timeline.obligationState.${gate.state}`)}
                  </Badge>
                </li>
              ))}
              {selectedPhase.deliverables.map((deliverable) => (
                <li
                  key={deliverable.name}
                  className="flex items-center justify-between gap-3 rounded-md border border-border bg-surface p-3 text-sm"
                >
                  <span>{deliverable.name}</span>
                  <Badge variant={deliverable.status === 'approved' ? 'success' : 'outline'}>
                    {t(`cockpit.timeline.deliverableState.${deliverable.status}`)}
                  </Badge>
                </li>
              ))}
            </ul>
          )}
        </section>
      </CardContent>
    </Card>
  );
}
