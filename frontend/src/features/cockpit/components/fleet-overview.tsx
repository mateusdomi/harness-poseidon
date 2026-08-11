import { Info, UsersRound } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import type { Agent, AgentAccountRoster } from '@/api';
import {
  Badge,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Skeleton,
  Tooltip,
  type BadgeProps,
} from '@/design-system';
import { useAgentRoster } from '@/features/agents/hooks/use-agent-roster';
import { AgentIdentity } from '@/features/shared/components/agent-identity';
import { formatDateTime, formatNumber } from '@/lib/format';

const PROVIDER_LABELS: Record<string, string> = {
  anthropic: 'Claude · Anthropic',
  openai: 'Codex · OpenAI',
  zhipu: 'GLM · Zhipu',
  antigravity: 'Antigravity',
  moonshot: 'Kimi · Moonshot',
};

const CAPACITY_STATES = new Set(['working', 'idle']);
const STATE_VARIANTS: Record<string, BadgeProps['variant']> = {
  working: 'info',
  idle: 'success',
  'out-of-quota': 'error',
  cooldown: 'warning',
  'authentication-required': 'warning',
  offline: 'default',
  degraded: 'warning',
  disabled: 'default',
};

function providerLabel(provider: string): string {
  return PROVIDER_LABELS[provider] ?? provider;
}

function businessStateKey(
  state: AgentAccountRoster['state'],
): 'available' | 'working' | 'unwell' | 'admission' {
  if (state === 'idle') return 'available';
  if (state === 'working') return 'working';
  if (state === 'authentication-required') return 'admission';
  return 'unwell';
}

/**
 * Quadro da Equipe. Cada conta é apresentada como uma pessoa real do quadro,
 * com nome, competência, estado e jornada. Provedor e diagnóstico ficam
 * restritos ao modo Técnico.
 */
export function FleetOverview({
  mode = 'business',
}: {
  agents: Agent[];
  mode?: 'business' | 'technical';
}) {
  const { t } = useTranslation();
  const rosterQuery = useAgentRoster();
  const accounts = rosterQuery.data ?? [];
  const technical = mode === 'technical';
  const availableAccounts = accounts.filter(
    (account) => account.enabled && account.state === 'idle',
  ).length;
  const runningAccounts = accounts.filter(
    (account) => account.enabled && account.state === 'working',
  ).length;
  const attentionAccounts = accounts.filter(
    (account) => !account.enabled || !CAPACITY_STATES.has(account.state),
  ).length;

  function journeyLabel(account: AgentAccountRoster): string {
    if (account.state === 'working') return t('cockpit.fleet.journey.working');
    if (account.state === 'idle') return t('cockpit.fleet.journey.ready');
    if (account.state === 'authentication-required') return t('cockpit.fleet.journey.admission');
    if ((account.state === 'cooldown' || account.state === 'out-of-quota') && account.returnsAt) {
      return t('cockpit.fleet.journey.returnsAt', {
        time: formatDateTime(account.returnsAt),
      });
    }
    if (account.state === 'cooldown' || account.state === 'out-of-quota') {
      return t('cockpit.fleet.journey.pause');
    }
    return t('cockpit.fleet.journey.unavailable');
  }

  return (
    <Card className="lg:col-span-2">
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
        <div className="flex flex-col gap-1">
          <CardTitle className="flex items-center gap-2">
            <UsersRound aria-hidden="true" className="size-5" />
            {t('cockpit.fleet.title')}
          </CardTitle>
          <p className="text-xs text-foreground-muted">{t('cockpit.fleet.subtitle')}</p>
        </div>
        {!rosterQuery.isLoading && !rosterQuery.isError && (
          <Badge
            variant={
              accounts.some((account) => account.state === 'authentication-required')
                ? 'warning'
                : 'success'
            }
          >
            {t('cockpit.fleet.people', { count: accounts.length })}
          </Badge>
        )}
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        <dl className="grid grid-cols-2 gap-2 md:grid-cols-4">
          {[
            [
              'online',
              availableAccounts,
            ],
            ['running', runningAccounts],
            ['withoutCapacity', attentionAccounts],
            ['total', accounts.length],
          ].map(([key, value]) => (
            <div key={key} className="rounded-lg border border-border bg-surface-elevated/40 p-3">
              <dt className="text-xs text-foreground-muted">{t(`cockpit.fleet.kpis.${key}`)}</dt>
              <dd className="font-heading text-2xl font-semibold tabular-nums">
                {formatNumber(value as number)}
              </dd>
            </div>
          ))}
        </dl>

        {rosterQuery.isLoading ? (
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3" aria-busy>
            {Array.from({ length: 3 }, (_, index) => (
              <Skeleton key={index} className="h-36 w-full" />
            ))}
          </div>
        ) : rosterQuery.isError ? (
          <p role="alert" className="text-sm text-error">
            {t('cockpit.fleet.error')}
          </p>
        ) : accounts.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.fleet.empty')}</p>
        ) : (
          <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {accounts.map((account: AgentAccountRoster) => (
              <li
                key={account.alias}
                className="flex flex-col gap-3 rounded-lg border border-border bg-surface/60 p-3"
              >
                <AgentIdentity
                  alias={account.alias}
                  technicalLabel={technical ? providerLabel(account.providerKind) : null}
                  size={44}
                />
                <div className="flex flex-wrap gap-2">
                  <Badge variant={STATE_VARIANTS[account.state] ?? 'outline'}>
                    {technical
                      ? t(`agents.roster.state.${account.state}`)
                      : t(`cockpit.fleet.businessState.${businessStateKey(account.state)}`)}
                  </Badge>
                  {technical && (
                    <Badge
                      variant={
                        account.health === 'healthy'
                          ? 'success'
                          : account.health === 'unhealthy'
                            ? 'error'
                            : 'warning'
                      }
                    >
                      {t(`cockpit.fleet.health.${account.health ?? 'attention'}`)}
                    </Badge>
                  )}
                </div>
                <p className="text-xs text-foreground-muted">
                  <span className="font-medium text-foreground">
                    {t('cockpit.fleet.journey.label')}:
                  </span>{' '}
                  {technical
                    ? account.state === 'out-of-quota'
                      ? account.returnsAt
                        ? t('cockpit.fleet.quotaReturns', {
                            time: formatDateTime(account.returnsAt),
                          })
                        : t('cockpit.fleet.quotaNoReturn')
                      : account.state === 'cooldown' && account.returnsAt
                        ? t('cockpit.fleet.cooldownReturns', {
                            time: formatDateTime(account.returnsAt),
                          })
                        : t('cockpit.fleet.quotaUnavailable')
                    : journeyLabel(account)}
                </p>
              </li>
            ))}
          </ul>
        )}

        <section className="flex flex-col gap-3" aria-labelledby="subscription-productivity-title">
          <div className="flex items-center gap-2">
            <h3 id="subscription-productivity-title" className="font-heading font-semibold">
              {t('cockpit.fleet.productivity.title')}
            </h3>
            <Tooltip label={t('cockpit.fleet.productivity.source')}>
              <button
                type="button"
                aria-label={t('cockpit.fleet.productivity.source')}
                className="rounded-full text-foreground-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                <Info aria-hidden="true" className="size-4" />
              </button>
            </Tooltip>
          </div>
          <div
            className="overflow-x-auto"
            tabIndex={0}
            aria-label={t('cockpit.fleet.productivity.title')}
          >
            <table className="w-full min-w-[44rem] text-left text-sm">
              <thead className="text-xs text-foreground-muted">
                <tr className="border-b border-border">
                  {['person', 'completed', 'success', 'rework', 'failures', 'duration'].map(
                    (key) => (
                      <th key={key} scope="col" className="px-2 py-2 font-medium">
                        {t(`cockpit.fleet.productivity.columns.${key}`)}
                      </th>
                    ),
                  )}
                </tr>
              </thead>
              <tbody>
                {accounts.map((account) => (
                  <tr key={account.alias} className="border-b border-border/70 last:border-0">
                    <th scope="row" className="px-2 py-2 font-medium">
                      <AgentIdentity
                        alias={account.alias}
                        technicalLabel={technical ? providerLabel(account.providerKind) : null}
                        size={32}
                      />
                    </th>
                    <td className="px-2 py-2 tabular-nums">0</td>
                    <td className="px-2 py-2 tabular-nums">0%</td>
                    <td className="px-2 py-2 tabular-nums">0%</td>
                    <td className="px-2 py-2 tabular-nums">0</td>
                    <td className="px-2 py-2">{t('cockpit.fleet.productivity.unavailable')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="text-xs text-foreground-muted">
            {t('cockpit.fleet.productivity.zeroExplanation')}
          </p>
        </section>
      </CardContent>
    </Card>
  );
}
