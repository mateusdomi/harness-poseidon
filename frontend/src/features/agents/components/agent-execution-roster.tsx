import { useTranslation } from 'react-i18next';
import { ServerCog } from 'lucide-react';
import { type FormEvent, useMemo, useState } from 'react';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Input, Select, Skeleton } from '@/design-system';
import { AgentIdentity } from '@/features/shared/components/agent-identity';
import {
  useDisableV3AgentAccount,
  useEnableV3AgentAccount,
  useAgentRoster,
  useChiefAssignment,
  useLogoutV3AgentAccount,
  usePrepareAgentAccountAuth,
  useSetChiefPrimary,
  useUpsertV3AgentAccount,
  useV3AgentAccounts,
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

const PROVIDER_OPTIONS = [
  { value: 'openai', label: 'OpenAI' },
  { value: 'anthropic', label: 'Claude' },
  { value: 'antigravity', label: 'Antigravity' },
  { value: 'moonshot', label: 'Kimi' },
  { value: 'zhipu', label: 'GLM' },
] as const;

const EXECUTOR_OPTIONS = [
  { value: 'codex', label: 'Codex' },
  { value: 'claude-code', label: 'Claude Code' },
  { value: 'antigravity', label: 'Antigravity' },
  { value: 'kimi-code', label: 'Kimi Code' },
  { value: 'glm', label: 'GLM' },
] as const;

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
  const v3Accounts = useV3AgentAccounts();
  const chiefAssignment = useChiefAssignment();
  const prepareAuth = usePrepareAgentAccountAuth();
  const setChief = useSetChiefPrimary();
  const upsertAccount = useUpsertV3AgentAccount();
  const enableAccount = useEnableV3AgentAccount();
  const disableAccount = useDisableV3AgentAccount();
  const logoutAccount = useLogoutV3AgentAccount();
  const [authCommand, setAuthCommand] = useState<string | null>(null);
  const [showAddAccount, setShowAddAccount] = useState(false);
  const [draft, setDraft] = useState({
    alias: '',
    providerKind: 'openai',
    executorId: 'codex',
    allowedRoles: ['project-executor'],
  });
  const accounts = rosterQuery.data ?? [];
  const v3ByAlias = useMemo(
    () => new Map((v3Accounts.data?.accounts ?? []).map((account) => [account.alias, account])),
    [v3Accounts.data?.accounts],
  );
  const availableAccounts = accounts.filter((account) => account.state === 'idle').length;
  const runningAccounts = accounts.filter((account) => account.state === 'working').length;
  const attentionAccounts = accounts.filter((account) =>
    ['out-of-quota', 'authentication-required', 'degraded', 'offline'].includes(account.state),
  ).length;

  async function prepare(alias: string) {
    const result = await prepareAuth.mutateAsync(alias);
    setAuthCommand(result.shellCommand);
  }

  function toggleRole(role: string) {
    setDraft((current) => ({
      ...current,
      allowedRoles: current.allowedRoles.includes(role)
        ? current.allowedRoles.filter((item) => item !== role)
        : [...current.allowedRoles, role],
    }));
  }

  async function submitAccount(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await upsertAccount.mutateAsync({
      ...draft,
      concurrencyLimit: 1,
      priority: 100,
      enabled: true,
      usagePolicy: 'AUTOMATIC',
    });
    setDraft({ alias: '', providerKind: 'openai', executorId: 'codex', allowedRoles: ['project-executor'] });
    setShowAddAccount(false);
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
          <div className="grid gap-3 md:grid-cols-4">
            <RuntimeSummaryMetric label={t('agents.roster.metrics.available')} value={availableAccounts} />
            <RuntimeSummaryMetric label={t('agents.roster.metrics.running')} value={runningAccounts} />
            <RuntimeSummaryMetric label={t('agents.roster.metrics.attention')} value={attentionAccounts} />
            <RuntimeSummaryMetric label={t('agents.roster.metrics.total')} value={accounts.length} />
            <p className="md:col-span-4 text-sm text-foreground-muted">
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
                    <dt>{t('agents.roster.usagePolicy')}</dt>
                    <dd className="min-w-0 break-words text-foreground">
                      {v3ByAlias.get(account.alias)?.usagePolicy ?? '—'}
                    </dd>
                    <dt>{t('agents.roster.health')}</dt>
                    <dd className="min-w-0 break-words text-foreground">
                      {v3ByAlias.get(account.alias)?.health ?? account.health ?? '—'}
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
                    {v3ByAlias.get(account.alias)?.usagePolicy === 'RESERVED' ? (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={enableAccount.isPending}
                        onClick={() => void enableAccount.mutateAsync(account.alias)}
                      >
                        {t('agents.roster.actions.release')}
                      </Button>
                    ) : (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={disableAccount.isPending}
                        onClick={() => void disableAccount.mutateAsync(account.alias)}
                      >
                        {t('agents.roster.actions.reserve')}
                      </Button>
                    )}
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      disabled={
                        logoutAccount.isPending ||
                        chiefAssignment.data?.primaryAlias === account.alias
                      }
                      onClick={() => void logoutAccount.mutateAsync(account.alias)}
                    >
                      {t('agents.roster.actions.logout')}
                    </Button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
        {showTechnicalDetails ? (
          <div className="mt-4 rounded-md border border-border p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <p className="text-sm font-medium">{t('agents.roster.add.title')}</p>
                <p className="text-sm text-foreground-muted">{t('agents.roster.add.help')}</p>
              </div>
              <Button type="button" variant="outline" size="sm" onClick={() => setShowAddAccount((value) => !value)}>
                {showAddAccount ? t('common.actions.cancel') : t('agents.roster.actions.add')}
              </Button>
            </div>
            {showAddAccount ? (
              <form className="mt-3 grid gap-3 md:grid-cols-2" onSubmit={submitAccount}>
                <label className="text-sm">
                  <span className="text-foreground-muted">{t('agents.roster.add.alias')}</span>
                  <Input
                    className="mt-1"
                    value={draft.alias}
                    onChange={(event) => setDraft((current) => ({ ...current, alias: event.target.value }))}
                    required
                  />
                </label>
                <label className="text-sm">
                  <span className="text-foreground-muted">{t('agents.roster.provider')}</span>
                  <Select
                    className="mt-1"
                    value={draft.providerKind}
                    onChange={(event) => setDraft((current) => ({ ...current, providerKind: event.target.value }))}
                  >
                    {PROVIDER_OPTIONS.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </Select>
                </label>
                <label className="text-sm">
                  <span className="text-foreground-muted">{t('agents.roster.executor')}</span>
                  <Select
                    className="mt-1"
                    value={draft.executorId}
                    onChange={(event) => setDraft((current) => ({ ...current, executorId: event.target.value }))}
                  >
                    {EXECUTOR_OPTIONS.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </Select>
                </label>
                <fieldset className="text-sm">
                  <legend className="text-foreground-muted">{t('agents.roster.add.capabilities')}</legend>
                  {['project-executor', 'chief-orchestrator', 'critic', 'platform-maintainer'].map((role) => (
                    <label key={role} className="mt-2 flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={draft.allowedRoles.includes(role)}
                        onChange={() => toggleRole(role)}
                      />
                      <span>{role}</span>
                    </label>
                  ))}
                </fieldset>
                <div className="md:col-span-2">
                  <Button type="submit" disabled={upsertAccount.isPending || draft.allowedRoles.length === 0}>
                    {t('agents.roster.actions.save')}
                  </Button>
                </div>
              </form>
            ) : null}
          </div>
        ) : null}
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
