import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Boxes, HeartPulse, Link2, Pencil, Sparkles, type LucideIcon } from 'lucide-react';

import { ApiError, type Account, type RoutingPolicy, type Ulid } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { formatCurrencyUSD } from '@/lib/format';
import { ConsumptionBar } from '@/features/providers/components/consumption-bar';
import { ProviderCard } from '@/features/providers/components/provider-card';
import { CollapsibleSection } from '@/features/providers/components/collapsible-section';
import { AccountFormDialog } from '@/features/providers/components/account-form-dialog';
import { RoutingPolicyDialog } from '@/features/providers/components/routing-policy-dialog';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  useAccounts,
  useBudgets,
  useDeleteAccount,
  useDisableAccount,
  useEnableAccount,
  useModels,
  useProviders,
  useProvidersRealtime,
  useRoutingPolicies,
  useSyncProviderCatalog,
} from '@/features/providers/hooks/use-providers';
import { modelDisplayName } from '@/features/providers/lib/providers-derive';
import { useNow } from '@/features/shared/hooks/use-now';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/** Tile de resumo (número em destaque + rótulo) para a visão geral da tela. */
function OverviewTile({ icon: Icon, value, label }: { icon: LucideIcon; value: number; label: string }) {
  return (
    <Card>
      <CardContent className="flex items-center gap-3 p-4">
        <span
          aria-hidden="true"
          className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-surface-elevated text-brand"
        >
          <Icon className="size-5" />
        </span>
        <span className="flex flex-col">
          <span className="font-heading text-2xl font-semibold leading-none">{value}</span>
          <span className="text-xs text-foreground-muted">{label}</span>
        </span>
      </CardContent>
    </Card>
  );
}

/**
 * Providers e contas — organizada para o fluxo do cliente:
 * 1) visão geral (contas conectadas, saúde, modelos habilitados);
 * 2) um cartão por provedor com CTA de conectar conta em destaque, estado
 *    (habilitado/desabilitado, com/sem conta) e catálogo de modelos recolhível;
 * 3) configurações avançadas (orçamentos e roteamento) em seções recolhíveis.
 * Realtime: `quota.updated` (stream global) atualiza as barras de cota.
 */
export default function UprovidersPage() {
  const { t } = useTranslation();
  const providersQuery = useProviders();
  const accountsQuery = useAccounts();
  const modelsQuery = useModels();
  const budgetsQuery = useBudgets();
  const routingQuery = useRoutingPolicies();
  const { projects } = useActiveProject();
  const syncCatalog = useSyncProviderCatalog();
  const enableAccount = useEnableAccount();
  const disableAccount = useDisableAccount();
  const deleteAccount = useDeleteAccount();
  const now = useNow();
  useProvidersRealtime();

  const [editingPolicy, setEditingPolicy] = useState<RoutingPolicy | null>(null);
  const [accountForm, setAccountForm] = useState<{ providerId: Ulid; account: Account | null } | null>(
    null,
  );
  const [deletingAccount, setDeletingAccount] = useState<Account | null>(null);
  const [syncFeedback, setSyncFeedback] = useState<{ providerId: Ulid; count: number } | null>(
    null,
  );

  const providers = providersQuery.data ?? [];
  const accounts = accountsQuery.data ?? [];
  const models = modelsQuery.data ?? [];
  const budgets = budgetsQuery.data ?? [];
  const routingPolicies = routingQuery.data ?? [];

  const projectName = (id: Ulid | null) =>
    id === null ? null : (projects.find((project) => project.id === id)?.name ?? id);
  const accountLabel = (id: Ulid | null) =>
    id === null ? null : (accounts.find((account) => account.id === id)?.label ?? id);

  const loading =
    providersQuery.isLoading ||
    accountsQuery.isLoading ||
    modelsQuery.isLoading ||
    budgetsQuery.isLoading ||
    routingQuery.isLoading;
  const errored =
    providersQuery.isError ||
    accountsQuery.isError ||
    modelsQuery.isError ||
    budgetsQuery.isError ||
    routingQuery.isError;

  function refetchAll() {
    void providersQuery.refetch();
    void accountsQuery.refetch();
    void modelsQuery.refetch();
    void budgetsQuery.refetch();
    void routingQuery.refetch();
  }

  if (loading) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-64 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  if (errored) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={refetchAll}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  const healthyAccounts = accounts.filter((account) => account.health === 'healthy').length;
  const enabledModels = models.filter((model) => model.enabled).length;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-1">
        <h1 className="font-heading text-2xl font-semibold">{t('features.providers.title')}</h1>
        <p className="text-sm text-foreground-muted">{t('providers.subtitle')}</p>
      </header>

      {providers.length === 0 ? (
        <Card>
          <CardContent className="p-4">
            <p className="text-sm text-foreground-muted">{t('providers.empty')}</p>
          </CardContent>
        </Card>
      ) : (
        <>
          <section
            aria-label={t('providers.overview.title')}
            className="grid grid-cols-2 gap-3 lg:grid-cols-4"
          >
            <OverviewTile
              icon={Boxes}
              value={providers.length}
              label={t('providers.overview.providers')}
            />
            <OverviewTile
              icon={Link2}
              value={accounts.length}
              label={t('providers.overview.accounts')}
            />
            <OverviewTile
              icon={HeartPulse}
              value={healthyAccounts}
              label={t('providers.overview.healthy')}
            />
            <OverviewTile
              icon={Sparkles}
              value={enabledModels}
              label={t('providers.overview.models')}
            />
          </section>

          <div className="flex flex-col gap-4">
            {providers.map((provider) => (
              <ProviderCard
                key={provider.id}
                provider={provider}
                accounts={accounts.filter((account) => account.providerId === provider.id)}
                models={models.filter((model) => model.providerId === provider.id)}
                budgets={budgets}
                now={now}
                syncPending={syncCatalog.isPending}
                syncFeedbackCount={
                  syncFeedback?.providerId === provider.id ? syncFeedback.count : null
                }
                enablePending={enableAccount.isPending}
                disablePending={disableAccount.isPending}
                onSync={() =>
                  syncCatalog.mutate(provider.id, {
                    onSuccess: (synced) =>
                      setSyncFeedback({ providerId: provider.id, count: synced.length }),
                  })
                }
                onNewAccount={() => setAccountForm({ providerId: provider.id, account: null })}
                onEditAccount={(account) =>
                  setAccountForm({ providerId: provider.id, account })
                }
                onEnableAccount={(account) => enableAccount.mutate(account.id)}
                onDisableAccount={(account) => disableAccount.mutate(account.id)}
                onDeleteAccount={(account) => {
                  deleteAccount.reset();
                  setDeletingAccount(account);
                }}
              />
            ))}
          </div>

          <section aria-labelledby="advanced-section" className="flex flex-col gap-3">
            <div className="flex flex-col gap-1">
              <h2 id="advanced-section" className="font-heading text-lg font-semibold">
                {t('providers.advanced.title')}
              </h2>
              <p className="text-sm text-foreground-muted">{t('providers.advanced.subtitle')}</p>
            </div>

            <CollapsibleSection
              title={t('providers.budgets.title')}
              summary={t('providers.budgets.count', { count: budgets.length })}
            >
              {budgets.length === 0 ? (
                <p className="text-sm text-foreground-muted">{t('providers.budgets.empty')}</p>
              ) : (
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                  {budgets.map((budget) => (
                    <Card key={budget.id}>
                      <CardContent className="flex flex-col gap-2 p-4">
                        <div className="flex flex-wrap items-center gap-2 text-sm">
                          <Badge variant="outline">{t(`status.budgetScope.${budget.scope}`)}</Badge>
                          <span className="font-medium">
                            {budget.scope === 'project'
                              ? projectName(budget.scopeId)
                              : budget.scope === 'account'
                                ? accountLabel(budget.scopeId)
                                : t('providers.budgets.global')}
                          </span>
                          <span className="text-xs text-foreground-muted">
                            {t(`status.budgetPeriod.${budget.period}`)}
                          </span>
                        </div>
                        <ConsumptionBar
                          used={budget.spentUsd}
                          limit={budget.limitUsd}
                          alertThresholdPct={budget.alertThresholdPct}
                          label={t('providers.budgets.bar', {
                            scope: t(`status.budgetScope.${budget.scope}`),
                          })}
                        />
                        <p className="text-xs text-foreground-muted">
                          {t('providers.budgets.consumption', {
                            spent: formatCurrencyUSD(budget.spentUsd),
                            limit: formatCurrencyUSD(budget.limitUsd),
                            threshold: budget.alertThresholdPct,
                          })}
                        </p>
                      </CardContent>
                    </Card>
                  ))}
                </div>
              )}
            </CollapsibleSection>

            <CollapsibleSection
              title={t('providers.routing.title')}
              summary={t('providers.routing.count', { count: routingPolicies.length })}
            >
              {routingPolicies.length === 0 ? (
                <p className="text-sm text-foreground-muted">{t('providers.routing.empty')}</p>
              ) : (
                <div className="flex flex-col gap-3">
                  {routingPolicies.map((policy) => (
                    <Card key={policy.id}>
                      <CardHeader className="flex-row flex-wrap items-center gap-3">
                        <CardTitle>{policy.name}</CardTitle>
                        <Badge variant={policy.active ? 'success' : 'outline'}>
                          {policy.active
                            ? t('providers.routing.active')
                            : t('providers.routing.inactive')}
                        </Badge>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          className="ml-auto"
                          onClick={() => setEditingPolicy(policy)}
                        >
                          <Pencil aria-hidden="true" />
                          {t('providers.routing.edit')}
                        </Button>
                      </CardHeader>
                      <CardContent>
                        <ul className="flex flex-col gap-2">
                          {policy.rules.map((rule, index) => (
                            <li key={index} className="rounded-md border border-border p-3 text-sm">
                              <span className="font-medium">
                                {rule.taskKind ?? t('providers.routing.defaultRule')}
                              </span>
                              : {modelDisplayName(rule.preferredModelId, models)}
                              {rule.fallbackModelIds.length > 0 && (
                                <span className="text-foreground-muted">
                                  {' → '}
                                  {rule.fallbackModelIds
                                    .map((id) => modelDisplayName(id, models))
                                    .join(', ')}
                                </span>
                              )}
                              {rule.maxCostPerAttemptUsd !== null && (
                                <span className="text-foreground-muted">
                                  {' · '}
                                  {t('providers.routing.maxCostShort', {
                                    value: formatCurrencyUSD(rule.maxCostPerAttemptUsd),
                                  })}
                                </span>
                              )}
                            </li>
                          ))}
                        </ul>
                      </CardContent>
                    </Card>
                  ))}
                </div>
              )}
            </CollapsibleSection>
          </section>
        </>
      )}

      {editingPolicy && (
        <RoutingPolicyDialog
          policy={editingPolicy}
          models={models}
          onClose={() => setEditingPolicy(null)}
        />
      )}

      {accountForm && (
        <AccountFormDialog
          providers={providers}
          account={accountForm.account ?? undefined}
          initialProviderId={accountForm.providerId}
          onClose={() => setAccountForm(null)}
        />
      )}

      {deletingAccount && (
        <ModalDialog
          label={t('providers.accounts.delete.title')}
          onClose={() => setDeletingAccount(null)}
        >
          <div className="flex flex-col gap-4">
            <h3 className="font-heading text-lg font-semibold">
              {t('providers.accounts.delete.title')}
            </h3>
            <p className="text-sm">
              {t('providers.accounts.delete.body', { label: deletingAccount.label })}
            </p>
            {deleteAccount.isError && (
              <p role="alert" className="text-sm text-error">
                {deleteAccount.error instanceof ApiError
                  ? deleteAccount.error.problem.detail || deleteAccount.error.problem.title
                  : t('providers.accounts.delete.error')}
              </p>
            )}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setDeletingAccount(null)}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                type="button"
                variant="destructive"
                disabled={deleteAccount.isPending}
                onClick={() =>
                  deleteAccount.mutate(deletingAccount.id, {
                    onSuccess: () => setDeletingAccount(null),
                  })
                }
              >
                {t('providers.accounts.delete.confirm')}
              </Button>
            </div>
          </div>
        </ModalDialog>
      )}
    </div>
  );
}
