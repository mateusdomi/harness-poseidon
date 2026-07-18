import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Agent, Attempt, Task } from '@/api';
import { Badge, Button, Card, CardContent } from '@/design-system';
import { formatCurrencyUSD, formatDurationMs } from '@/lib/format';
import { agentStateVariant } from '@/lib/status';
import { AttemptDialog } from '@/features/orchestrator/components/attempt-dialog';
import {
  attemptsOf,
  groupAgentsByState,
  latestAttemptOf,
  runningAttemptOf,
} from '@/features/orchestrator/lib/orchestrator-derive';

export interface AgentGridProps {
  /** Agentes do projeto ativo (o chefe NÃO entra — tem card próprio). */
  agents: Agent[];
  tasks: Task[];
  attempts: Attempt[];
  now: Date;
}

/**
 * Grade de agentes agrupada por estado (ordem do domínio). Cada card mostra
 * tarefa atual, duração da tentativa em execução, nº de tentativas, custo
 * acumulado e a ação "Abrir" (linha do tempo + log da tentativa).
 */
export function AgentGrid({ agents, tasks, attempts, now }: AgentGridProps) {
  const { t } = useTranslation();
  const [openAttempt, setOpenAttempt] = useState<Attempt | null>(null);
  const groups = groupAgentsByState(agents);

  if (groups.length === 0) {
    return (
      <section aria-label={t('orchestrator.agents.title')}>
        <h2 className="font-heading text-lg font-semibold">{t('orchestrator.agents.title')}</h2>
        <Card className="mt-3">
          <CardContent className="p-6 text-sm text-foreground-muted">
            {t('orchestrator.agents.empty')}
          </CardContent>
        </Card>
      </section>
    );
  }

  return (
    <section aria-label={t('orchestrator.agents.title')} className="flex flex-col gap-4">
      <h2 className="font-heading text-lg font-semibold">{t('orchestrator.agents.title')}</h2>
      {groups.map((group) => (
        <section key={group.state} aria-label={t(`status.agentState.${group.state}`)}>
          <div className="mb-2 flex items-center gap-2">
            <Badge variant={agentStateVariant(group.state)}>
              {t(`status.agentState.${group.state}`)}
            </Badge>
            <span className="text-sm text-foreground-muted">{group.agents.length}</span>
          </div>
          <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {group.agents.map((agent) => {
              const task = tasks.find((entry) => entry.id === agent.currentTaskId) ?? null;
              const running = runningAttemptOf(attempts, agent.id);
              const latest = latestAttemptOf(attempts, agent.id);
              return (
                <li key={agent.id}>
                  <Card className="h-full">
                    <CardContent className="flex h-full flex-col gap-2 p-4">
                      <p className="font-medium">{agent.name}</p>
                      <p className="text-sm text-foreground-muted">
                        {task ? task.title : t('orchestrator.agents.noTask')}
                      </p>
                      {running ? (
                        <p className="text-sm">
                          {t('orchestrator.agents.runningFor', {
                            duration: formatDurationMs(
                              Math.max(0, now.getTime() - Date.parse(running.startedAt)),
                            ),
                          })}
                        </p>
                      ) : null}
                      <p className="text-sm text-foreground-muted">
                        {t('orchestrator.agents.attempts', { count: attemptsOf(attempts, agent.id).length })}
                        {' · '}
                        {t('orchestrator.agents.cost')}: {formatCurrencyUSD(agent.metrics.costUsd)}
                      </p>
                      <div className="mt-auto pt-1">
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          disabled={latest === null}
                          onClick={() => setOpenAttempt(latest)}
                        >
                          {t('orchestrator.agents.open')}
                        </Button>
                      </div>
                    </CardContent>
                  </Card>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
      {openAttempt ? (
        <AttemptDialog attempt={openAttempt} onClose={() => setOpenAttempt(null)} />
      ) : null}
    </section>
  );
}
