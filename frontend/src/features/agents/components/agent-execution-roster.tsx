import { useTranslation } from 'react-i18next';
import { Cpu, ServerCog } from 'lucide-react';

import { Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { useAgentRoster } from '@/features/agents/hooks/use-agent-roster';

const STATE_VARIANT: Record<string, 'warning' | 'default'> = {
  'authentication-required': 'warning',
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
  const rosterQuery = useAgentRoster();
  const accounts = rosterQuery.data ?? [];

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
        ) : (
          <ul className="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
            {accounts.map((account) => (
              <li
                key={account.alias}
                className="flex flex-col gap-2 rounded-md border border-border p-3"
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="flex items-center gap-2 font-medium">
                    <Cpu className="size-4 text-foreground-muted" aria-hidden />
                    {account.alias}
                  </span>
                  <Badge variant={STATE_VARIANT[account.state] ?? 'default'}>
                    {t(`agents.roster.state.${account.state}`, { defaultValue: account.state })}
                  </Badge>
                </div>
                <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm text-foreground-muted">
                  <dt>{t('agents.roster.provider')}</dt>
                  <dd className="text-foreground">{account.providerKind}</dd>
                  <dt>{t('agents.roster.executor')}</dt>
                  <dd className="text-foreground">{account.executorId}</dd>
                  <dt>{t('agents.roster.roles')}</dt>
                  <dd className="flex flex-wrap gap-1">
                    {account.roles.map((role) => (
                      <Badge key={role} variant="outline">
                        {role}
                      </Badge>
                    ))}
                  </dd>
                </dl>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
