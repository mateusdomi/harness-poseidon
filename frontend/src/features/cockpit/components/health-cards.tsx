import { Gauge, Info } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { type Agent, type AgentAccountRoster, type Budget, type TaskState } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle, Tooltip } from '@/design-system';
import { formatCurrencyUSD, formatNumber } from '@/lib/format';
import {
  budgetSeverity,
  budgetUsagePct,
  factoryAgentMetrics,
} from '@/features/cockpit/lib/cockpit-derive';
import { cn } from '@/lib/utils';
import { useAgentRoster } from '@/features/agents/hooks/use-agent-roster';
import { AgentIdentity } from '@/features/shared/components/agent-identity';

/** KPI da fábrica: rótulo, número e explicação (tooltip). */
function FactoryKpi({
  label,
  tooltip,
  value,
  tone = 'default',
}: {
  label: string;
  tooltip: string;
  value: number;
  tone?: 'default' | 'good' | 'alert';
}) {
  const valueClass =
    tone === 'alert' && value > 0
      ? 'text-error'
      : tone === 'good'
        ? 'text-success'
        : 'text-foreground';
  return (
    <div className="flex flex-col gap-1 rounded-md border border-border bg-surface-elevated/40 p-3">
      <dt className="flex items-center gap-1 text-xs text-foreground-muted">
        {label}
        <Tooltip label={tooltip}>
          <button
            type="button"
            aria-label={tooltip}
            className="rounded-full text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            <Info aria-hidden="true" className="size-3.5" />
          </button>
        </Tooltip>
      </dt>
      <dd className={cn('font-heading text-2xl font-semibold tabular-nums', valueClass)}>
        {formatNumber(value)}
      </dd>
    </div>
  );
}

/**
 * Fábrica de agentes — visão de dono: capacidade produtiva em números
 * (online, entregas, fila, capacidade parada) e nomes humanos de quem está
 * produzindo agora e de quem precisa de atenção. Não é log técnico.
 */
export function AgentsHealthCard({
  agents,
  taskCounts,
}: {
  agents: Agent[];
  taskCounts: Record<TaskState, number>;
}) {
  const { t } = useTranslation();
  const m = factoryAgentMetrics(agents, taskCounts);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('cockpit.agents.title')}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {agents.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.agents.empty')}</p>
        ) : (
          <>
            <dl className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              <FactoryKpi
                label={t('cockpit.agents.online')}
                tooltip={t('cockpit.agents.onlineTooltip')}
                value={m.online}
                tone="good"
              />
              <FactoryKpi
                label={t('cockpit.agents.tasksDone')}
                tooltip={t('cockpit.agents.tasksDoneTooltip')}
                value={m.tasksDone}
              />
              <FactoryKpi
                label={t('cockpit.agents.tasksTodo')}
                tooltip={t('cockpit.agents.tasksTodoTooltip')}
                value={m.tasksTodo}
              />
              <FactoryKpi
                label={t('cockpit.agents.problems')}
                tooltip={t('cockpit.agents.problemsTooltip')}
                value={m.problems}
                tone="alert"
              />
            </dl>
            {m.working.length > 0 && (
              <div className="flex flex-col gap-1">
                <span className="text-xs font-medium text-foreground-muted">
                  {t('cockpit.agents.workingNow')}
                </span>
                <ul className="flex flex-wrap gap-2">
                  {m.working.map((agent) => (
                    <li key={agent.id}>
                      <Badge variant="info" className="px-3 py-1.5 text-sm">
                        {agent.name}
                      </Badge>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {m.attention.length > 0 && (
              <div className="flex flex-col gap-1">
                <span className="text-xs font-medium text-error">
                  {t('cockpit.agents.needAttention')}
                </span>
                <ul className="flex flex-wrap gap-2">
                  {m.attention.map((agent) => (
                    <li key={agent.id}>
                      <Badge variant="error" className="px-3 py-1.5 text-sm">
                        {agent.name}
                        <span className="font-normal">
                          {agent.state === 'outOfQuota'
                            ? t('status.agentState.outOfQuota')
                            : t('status.agentState.error')}
                        </span>
                      </Badge>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </>
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

function capacityVariant(
  state: AgentAccountRoster['state'],
): 'success' | 'info' | 'warning' | 'error' | 'outline' {
  if (state === 'idle') return 'success';
  if (state === 'working') return 'info';
  if (state === 'authentication-required') return 'warning';
  return 'error';
}

function capacityKey(
  state: AgentAccountRoster['state'],
): 'available' | 'working' | 'admission' | 'unavailable' {
  if (state === 'idle') return 'available';
  if (state === 'working') return 'working';
  if (state === 'authentication-required') return 'admission';
  return 'unavailable';
}

/** Capacidade da equipe por pessoa, sem expor séries ou diagnósticos técnicos. */
export function TeamCapacityCard() {
  const { t } = useTranslation();
  const rosterQuery = useAgentRoster();
  const accounts = rosterQuery.data ?? [];
  const attention = accounts.filter(
    (account) => !['idle', 'working'].includes(account.state),
  ).length;

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle className="flex items-center gap-2">
            <Gauge aria-hidden="true" className="size-5" />
            {t('cockpit.capacity.title')}
          </CardTitle>
          {!rosterQuery.isLoading && !rosterQuery.isError && (
            <Badge variant={attention > 0 ? 'warning' : 'success'}>
              {attention > 0
                ? t('cockpit.capacity.attention', { count: attention })
                : t('cockpit.capacity.healthy')}
            </Badge>
          )}
        </div>
      </CardHeader>
      <CardContent>
        {rosterQuery.isLoading ? (
          <p className="text-sm text-foreground-muted">{t('common.states.loading')}</p>
        ) : rosterQuery.isError ? (
          <p role="alert" className="text-sm text-error">
            {t('cockpit.capacity.error')}
          </p>
        ) : accounts.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.capacity.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {accounts.map((account) => (
              <li
                key={account.alias}
                className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border p-3"
              >
                <AgentIdentity alias={account.alias} technicalLabel={null} size={34} />
                <Badge variant={capacityVariant(account.state)}>
                  {t(`cockpit.capacity.state.${capacityKey(account.state)}`)}
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

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
