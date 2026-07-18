import { useTranslation } from 'react-i18next';

import type { Gate, Phase, Task } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { gateStateVariant, phaseStateVariant } from '@/lib/status';
import { aggregateProgress, gateOfPhase, tasksOfPhase } from '@/features/cockpit/lib/cockpit-derive';
import { ProgressTracks } from '@/features/cockpit/components/progress-tracks';

interface PhaseSummaryProps {
  phase: Phase | null;
  gates: Gate[];
  tasks: Task[];
}

/** Fase atual do workflow: nome, estado, gate associado e progresso em 3 trilhas. */
export function PhaseSummary({ phase, gates, tasks }: PhaseSummaryProps) {
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
  const phaseTasks = tasksOfPhase(tasks, phase);

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
        <ProgressTracks progress={aggregateProgress(phaseTasks)} />
      </CardContent>
    </Card>
  );
}
