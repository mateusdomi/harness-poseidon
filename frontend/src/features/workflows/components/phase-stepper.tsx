import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { CircleCheck, CircleDot, Circle, Info, ShieldCheck } from 'lucide-react';

import type { Document, Gate, Phase } from '@/api';
import { Badge, Tooltip, type BadgeProps } from '@/design-system';
import { formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';
import { documentStateVariant, gateStateVariant, phaseStateVariant } from '@/lib/status';

export interface PhaseStepperProps {
  phases: Phase[];
  gates: Gate[];
  documents: Document[];
}

type DeliverableStatus = Phase['deliverables'][number]['status'];

const DELIVERABLE_VARIANTS: Record<DeliverableStatus, BadgeProps['variant']> = {
  planned: 'outline',
  notStarted: 'outline',
  inProduction: 'info',
  inReview: 'brand',
  approved: 'success',
  rejected: 'error',
};

/**
 * Stepper das fases do run ativo: vertical no mobile, horizontal em md+.
 * Cada fase mostra estado, gates e os documentos vinculados (por `phaseName`)
 * com estado, versão e link para o detalhe no catálogo de documentos.
 */
export function PhaseStepper({ phases, gates, documents }: PhaseStepperProps) {
  const { t } = useTranslation();

  return (
    <ol
      aria-label={t('workflows.phases.label')}
      className="flex w-full max-w-full flex-col gap-4 overflow-x-auto pb-1 md:flex-row md:items-stretch md:gap-3"
    >
      {phases.map((phase, index) => {
        const phaseGates = gates.filter((gate) => gate.phaseId === phase.id);
        const phaseDocuments = documents.filter((doc) => doc.phaseName === phase.name);
        const Icon =
          phase.state === 'completed' ? CircleCheck : phase.state === 'active' ? CircleDot : Circle;

        return (
          <li
            key={phase.id}
            aria-current={phase.state === 'active' ? 'step' : undefined}
            className={cn(
              'relative flex-1 rounded-xl border bg-surface p-4 md:min-w-36',
              phase.state === 'active' ? 'border-brand' : 'border-border',
            )}
          >
            {/* Conector (desktop): linha até a próxima fase. */}
            {index < phases.length - 1 && (
              <span
                aria-hidden="true"
                className="absolute left-full top-8 hidden h-px w-3 bg-border md:block"
              />
            )}
            <div className="flex items-start gap-3">
              <Icon
                aria-hidden="true"
                className={cn(
                  'mt-0.5 size-5 shrink-0',
                  phase.state === 'active' ? 'text-brand-strong' : 'text-foreground-muted',
                )}
              />
              <div className="flex min-w-0 flex-1 flex-col gap-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm font-semibold">
                    {index + 1}. {phase.name}
                  </span>
                  <Badge variant={phaseStateVariant(phase.state)}>
                    {t(`status.phaseState.${phase.state}`)}
                  </Badge>
                </div>

                <div className="mt-2 flex items-center gap-2">
                  <div
                    role="progressbar"
                    aria-label={t('workflows.phases.progressLabel', { phase: phase.name })}
                    aria-valuenow={phase.progress.percent}
                    aria-valuemin={0}
                    aria-valuemax={100}
                    className="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-elevated"
                  >
                    <div
                      className="h-full rounded-full bg-brand"
                      style={{ width: `${phase.progress.percent}%` }}
                    />
                  </div>
                  <span className="text-xs tabular-nums text-foreground-muted">
                    {formatNumber(phase.progress.percent)}%
                  </span>
                  <Tooltip
                    label={t('workflows.phases.progressTooltip', {
                      completed: phase.progress.completed,
                      total: phase.progress.total,
                      tasksDone: phase.progress.tasks.completed,
                      tasksTotal: phase.progress.tasks.total,
                      documentsDone: phase.progress.documents.completed,
                      documentsTotal: phase.progress.documents.total,
                      gatesDone: phase.progress.gates.completed,
                      gatesTotal: phase.progress.gates.total,
                    })}
                  >
                    <button
                      type="button"
                      aria-label={t('workflows.phases.progressDetails', { phase: phase.name })}
                      className="rounded-full text-foreground-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    >
                      <Info aria-hidden="true" className="size-3.5" />
                    </button>
                  </Tooltip>
                </div>

                {phaseGates.length > 0 && (
                  <ul
                    aria-label={t('workflows.phases.gatesLabel', { phase: phase.name })}
                    className="mt-2 flex flex-col gap-1.5"
                  >
                    {phaseGates.map((gate) => (
                      <li key={gate.id} className="flex flex-wrap items-center gap-2 text-xs">
                        <ShieldCheck aria-hidden="true" className="size-4 text-foreground-muted" />
                        <span>{gate.name}</span>
                        <Badge variant={gateStateVariant(gate.state)}>
                          {t(`status.gateState.${gate.state}`)}
                        </Badge>
                      </li>
                    ))}
                  </ul>
                )}

                <div className="mt-2 flex flex-col gap-1.5">
                  <span className="text-xs font-medium text-foreground-muted">
                    {t('workflows.phases.deliverablesLabel')}
                  </span>
                  {phase.deliverables.length === 0 ? (
                    <span className="text-xs text-foreground-muted">
                      {t('workflows.phases.deliverablesUnavailable')}
                    </span>
                  ) : (
                    <ul className="flex flex-col gap-1.5">
                      {phase.deliverables.map((deliverable) => (
                        <li
                          key={deliverable.name}
                          className="flex flex-wrap items-center gap-2 text-xs"
                        >
                          <span className="font-medium">{deliverable.name}</span>
                          <Badge variant={DELIVERABLE_VARIANTS[deliverable.status]}>
                            {t(`chat.workflowPanel.health.${deliverable.status}`)}
                          </Badge>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>

                {phaseDocuments.length > 0 && (
                  <div className="mt-2 flex flex-col gap-1.5">
                    <span className="text-xs font-medium text-foreground-muted">
                      {t('workflows.phases.producedDocumentsLabel')}
                    </span>
                    <ul className="flex flex-col gap-1.5">
                      {phaseDocuments.map((doc) => (
                        <li key={doc.id} className="flex flex-wrap items-center gap-2 text-xs">
                          <Link
                            to={`/documents?doc=${doc.id}`}
                            className="min-h-11 font-medium text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent md:min-h-0"
                          >
                            {doc.title}
                          </Link>
                          <span className="text-foreground-muted">
                            {t('workflows.phases.documentVersion', { version: doc.currentVersion })}
                          </span>
                          <Badge variant={documentStateVariant(doc.state)}>
                            {t(`status.documentState.${doc.state}`)}
                          </Badge>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </div>
            </div>
          </li>
        );
      })}
    </ol>
  );
}
