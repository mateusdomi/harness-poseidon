import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { CircleCheck, CircleDot, Circle, ShieldCheck } from 'lucide-react';

import type { Document, Gate, Phase } from '@/api';
import { Badge } from '@/design-system';
import { cn } from '@/lib/utils';
import {
  documentStateVariant,
  gateStateVariant,
  phaseStateVariant,
} from '@/lib/status';

export interface PhaseStepperProps {
  phases: Phase[];
  gates: Gate[];
  documents: Document[];
}

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
      className="flex flex-col gap-4 md:flex-row md:items-stretch md:gap-3"
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
              'relative flex-1 rounded-xl border bg-surface p-4',
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
                  phase.state === 'active' ? 'text-brand' : 'text-foreground-muted',
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
                    {t('workflows.phases.documentsLabel')}
                  </span>
                  {phaseDocuments.length === 0 ? (
                    <span className="text-xs text-foreground-muted">
                      {t('workflows.phases.noDocuments')}
                    </span>
                  ) : (
                    <ul className="flex flex-col gap-1.5">
                      {phaseDocuments.map((doc) => (
                        <li key={doc.id} className="flex flex-wrap items-center gap-2 text-xs">
                          <Link
                            to={`/documents?doc=${doc.id}`}
                            className="min-h-11 font-medium text-brand underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent md:min-h-0"
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
                  )}
                </div>
              </div>
            </div>
          </li>
        );
      })}
    </ol>
  );
}
