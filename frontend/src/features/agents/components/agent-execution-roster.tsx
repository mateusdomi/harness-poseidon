import { useTranslation } from 'react-i18next';
import { ServerCog } from 'lucide-react';
import { useState } from 'react';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { AgentIdentity } from '@/features/shared/components/agent-identity';
import {
  useAgentRoster,
  useChiefAssignment,
  usePrepareAgentAccountAuth,
  useSetChiefPrimary,
} from '@/features/agents/hooks/use-agent-roster';
import { usePresentationMode } from '@/app/presentation';

const STATE_VARIANT: Record<string, 'warning' | 'default' | 'success' | 'info' | 'error'> = {
  working: 'info',
  idle: 'success',
  'out-of-quota': 'error',
  cooldown: 'warning',
  'authentication-required': 'warning',
  degraded: 'warning',
  disabled: 'default',
};

/**
 * Roster de EXECUÇÃO da fleet: as identidades (contas de agent-run) que rodam o
 * trabalho — chief/worker × provider. É deliberadamente distinto do organograma de
 * personas: aqui vemos QUEM executa (executor/provider/estado), não O QUE o agente é.
 * Somente leitura e REDIGIDO — nunca há credencial ou token.
 */
export function AgentExecutionRoster() {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const rosterQuery = useAgentRoster();
  const chiefAssignment = useChiefAssignment();
  const prepareAuth = usePrepareAgentAccountAuth();
  const setChief = useSetChiefPrimary();
  const [authCommand, setAuthCommand] = useState<string | null>(null);
  const accounts = rosterQuery.data ?? [];
  const availableAccounts = accounts.filter((account) => account.state === 'idle').length;
  const runningAccounts = accounts.filter((account) => account.state === 'working').length;
  const attentionAccounts = accounts.filter((account) =>
    ['out-of-quota', 'authentication-required', 'degraded', 'offline'].includes(account.state),
  ).length;

  async function prepare(alias: string) {
    const result = await prepareAuth.mutateAsync(alias);
    setAuthCommand(result.shellCommand);
  }

  return (
    <Card>
      <CardHeader className="flex flex-row items-start justify-between gap-3">
        <div className="flex flex-col gap-1">
          <CardTitle className="flex items-center gap-2 text-base">
            <ServerCog className="size-5" aria-hidden />
            {t('agents.roster.title')}
          </CardTitle>
          <p className="text-sm text-foreground-muted">{t('agents.roster.subtitle')}</p>
        </div>
        {!rosterQuery.isLoading && !rosterQuery.isError && (
          <Badge variant="default">{t('agents.roster.count', { count: accounts.length })}</Badge>
        )}
      </CardHeader>
      <CardContent>
        {rosterQuery.isLoading ? (
          <div className="grid gap-2" aria-busy>
            <Skeleton className="h-16 w-full" />
            <Skeleton className="h-16 w-full" />
          </div>
        ) : rosterQuery.isError ? (
          <p role="alert" className="text-sm text-error">
            {t('agents.roster.error')}
          </p>
        ) : accounts.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('agents.roster.empty')}</p>
        ) : !showTechnicalDetails ? (
          <div className="grid gap-3 sm:grid-cols-4">
            <RuntimeSummaryMetric label={t('agents.roster.metrics.available')} value={availableAccounts} />
            <RuntimeSummaryMetric label={t('agents.roster.metrics.running')} value={runningAccounts} />
            <RuntimeSummaryMetric label={t('agents.roster.metrics.attention')} value={attentionAccounts} />
            <RuntimeSummaryMetric label={t('agents.roster.metrics.total')} value={accounts.length} />
            <p className="sm:col-span-4 text-sm text-foreground-muted">
              {t('agents.roster.businessSummary')}
            </p>
          </div>
        ) : (
          <ul className="grid min-w-0 gap-2 md:grid-cols-2 xl:grid-cols-3">
            {accounts.map((account) => (
              <li
                key={account.alias}
                className="flex min-w-0 flex-col gap-2 overflow-hidden rounded-md border border-border p-3"
              >
                <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
                  <AgentIdentity
                    alias={account.alias}
                    technicalLabel={showTechnicalDetails ? account.alias : undefined}
                    size={36}
                  />
                  <Badge variant={STATE_VARIANT[account.state] ?? 'default'}>
                    {t(`agents.roster.state.${account.state}`, { defaultValue: account.state })}
                  </Badge>
                </div>
                {chiefAssignment.data?.primaryAlias === account.alias ? (
                  <Badge variant="info">{t('agents.roster.chiefPrimary')}</Badge>
                ) : null}
                {showTechnicalDetails ? (
                  <dl className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm text-foreground-muted">
                    <dt>{t('agents.roster.provider')}</dt>
                    <dd className="min-w-0 break-words text-foreground">{account.providerKind}</dd>
                    <dt>{t('agents.roster.executor')}</dt>
                    <dd className="min-w-0 break-words text-foreground">{account.executorId}</dd>
                    <dt>{t('agents.roster.roles')}</dt>
                    <dd className="flex min-w-0 flex-wrap gap-1">
                      {account.roles.map((role) => (
                        <Badge key={role} variant="outline">
                          {role}
                        </Badge>
                      ))}
                    </dd>
                  </dl>
                ) : null}
                {showTechnicalDetails ? (
                  <div className="flex flex-wrap gap-2">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      disabled={prepareAuth.isPending}
                      onClick={() => void prepare(account.alias)}
                    >
                      {t('agents.roster.actions.auth')}
                    </Button>
                    {account.roles.includes('chief-orchestrator') ? (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={setChief.isPending}
                        onClick={() => void setChief.mutateAsync(account.alias)}
                      >
                        {t('agents.roster.actions.setChief')}
                      </Button>
                    ) : null}
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
        {authCommand ? (
          <div className="mt-4 rounded-md border border-border bg-surface-subtle p-3">
            <p className="text-sm font-medium">{t('agents.roster.authCommand')}</p>
            <pre className="mt-2 overflow-auto rounded bg-background p-2 text-xs">
              <code>{authCommand}</code>
            </pre>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

function RuntimeSummaryMetric({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-md border border-border bg-surface-elevated p-3">
      <p className="text-xs text-foreground-muted">{label}</p>
      <p className="mt-1 font-heading text-2xl font-semibold">{value}</p>
    </div>
  );
}
