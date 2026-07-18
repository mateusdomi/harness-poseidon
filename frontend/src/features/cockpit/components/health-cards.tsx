import { useTranslation } from 'react-i18next';

import { AGENT_STATES, type Agent, type Budget } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { formatCurrencyUSD, formatNumber } from '@/lib/format';
import { agentStateVariant } from '@/lib/status';
import { budgetSeverity, budgetUsagePct, countAgentsByState } from '@/features/cockpit/lib/cockpit-derive';
import { cn } from '@/lib/utils';

/** Saúde dos agentes do projeto: contadores por estado operacional. */
export function AgentsHealthCard({ agents }: { agents: Agent[] }) {
  const { t } = useTranslation();
  const counts = countAgentsByState(agents);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('cockpit.agents.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        {agents.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.agents.empty')}</p>
        ) : (
          <ul className="flex flex-wrap gap-2">
            {AGENT_STATES.map((state) => (
              <li key={state}>
                <Badge variant={agentStateVariant(state)} className="px-3 py-1.5 text-sm">
                  {t(`status.agentState.${state}`)}
                  <span className="font-semibold tabular-nums">{formatNumber(counts[state])}</span>
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

const SEVERITY_BAR = {
  ok: 'bg-success',
  warning: 'bg-warning',
  critical: 'bg-error',
} as const;

/** Cotas críticas: budgets no limiar de alerta ou estourados. */
export function QuotaCard({ budgets }: { budgets: Budget[] }) {
  const { t } = useTranslation();
  const critical = budgets.filter((budget) => budgetSeverity(budget) !== 'ok');

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('cockpit.quotas.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        {critical.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.quotas.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {critical.map((budget) => {
              const severity = budgetSeverity(budget);
              const pct = budgetUsagePct(budget);
              return (
                <li key={budget.id} className="flex flex-col gap-1">
                  <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                    <span className="flex items-center gap-2">
                      <Badge variant={severity === 'critical' ? 'error' : 'warning'}>
                        {t(`status.budgetScope.${budget.scope}`)}
                      </Badge>
                      <span className="text-foreground-muted">
                        {t(`status.budgetPeriod.${budget.period}`)}
                      </span>
                    </span>
                    <span className="tabular-nums">
                      {formatCurrencyUSD(budget.spentUsd)} / {formatCurrencyUSD(budget.limitUsd)}
                    </span>
                  </div>
                  <div
                    role="progressbar"
                    aria-label={t(`status.budgetScope.${budget.scope}`)}
                    aria-valuenow={pct ?? 0}
                    aria-valuemin={0}
                    aria-valuemax={100}
                    className="h-2 overflow-hidden rounded-full bg-surface-elevated"
                  >
                    <div
                      className={cn('h-full rounded-full', SEVERITY_BAR[severity])}
                      style={{ width: `${Math.min(pct ?? 0, 100)}%` }}
                    />
                  </div>
                  <span
                    className={cn(
                      'text-xs',
                      severity === 'critical' ? 'text-error' : 'text-warning',
                    )}
                  >
                    {t(`cockpit.quotas.severity.${severity}`, { pct: pct ?? 0 })}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
