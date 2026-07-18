import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Pencil, RefreshCw } from 'lucide-react';

import type { RoutingPolicy, Ulid } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { accountStateVariant } from '@/lib/status';
import { formatCurrencyUSD, formatDate, formatNumber } from '@/lib/format';
import { ConsumptionBar } from '@/features/providers/components/consumption-bar';
import { RoutingPolicyDialog } from '@/features/providers/components/routing-policy-dialog';
import {
  useAccounts,
  useBudgets,
  useModels,
  useProviders,
  useProvidersRealtime,
  useRoutingPolicies,
  useSyncProviderCatalog,
} from '@/features/providers/hooks/use-providers';
import {
  accountBudget,
  modelDisplayName,
  nextBudgetReset,
} from '@/features/providers/lib/providers-derive';
import { useNow } from '@/features/shared/hooks/use-now';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * Providers e contas: catálogo de modelos (somente leitura) com sync,
 * contas com saúde/cota/janela/reset, budgets por escopo e política de
 * roteamento em visualização estruturada com edição confirmada.
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
  const now = useNow();
  useProvidersRealtime();

  const [editingPolicy, setEditingPolicy] = useState<RoutingPolicy | null>(null);
  const [syncFeedback, setSyncFeedback] = useState<{ providerId: Ulid; count: number } | null>(
    null,
  );

  const providers = providersQuery.data ?? [];
  const accounts = accountsQuery.data ?? [];
  const models = modelsQuery.data ?? [];
  const budgets = budgetsQuery.data ?? [];

  const projectName = (id: Ulid | null) =>
    id === null ? null : (projects.find((project) => project.id === id)?.name ?? id);
  const accountLabel = (id: Ulid | null) =>
    id === null ? null : (accounts.find((account) => account.id === id)?.label ?? id);

  const loading =
    providersQuery.isPending ||
    accountsQuery.isPending ||
    modelsQuery.isPending ||
    budgetsQuery.isPending ||
    routingQuery.isPending;
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

  return (
    <div className="flex flex-col gap-6">
      <h1 className="font-heading text-2xl font-semibold">{t('features.providers.title')}</h1>

      {providers.length === 0 && (
        <Card>
          <CardContent className="p-4">
            <p className="text-sm text-foreground-muted">{t('providers.empty')}</p>
          </CardContent>
        </Card>
      )}

      {providers.map((provider) => {
        const providerAccounts = accounts.filter((account) => account.providerId === provider.id);
        const providerModels = models.filter((model) => model.providerId === provider.id);
        return (
          <section
            key={provider.id}
            className="flex flex-col gap-3"
            aria-labelledby={`provider-${provider.id}`}
          >
            <div className="flex flex-wrap items-center gap-3">
              <h2 id={`provider-${provider.id}`} className="font-heading text-lg font-semibold">
                {provider.name}
              </h2>
              <Badge variant="outline">{t(`status.providerKind.${provider.kind}`)}</Badge>
              <Badge variant={provider.enabled ? 'success' : 'outline'}>
                {provider.enabled ? t('providers.enabled') : t('providers.disabled')}
              </Badge>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="ml-auto"
                disabled={syncCatalog.isPending}
                onClick={() =>
                  syncCatalog.mutate(provider.id, {
                    onSuccess: (synced) =>
                      setSyncFeedback({ providerId: provider.id, count: synced.length }),
                  })
                }
              >
                <RefreshCw aria-hidden="true" />
                {t('providers.sync.action')}
              </Button>
            </div>
            {syncFeedback?.providerId === provider.id && (
              <p role="status" className="text-sm text-foreground-muted">
                {t('providers.sync.feedback', { count: syncFeedback.count })}
              </p>
            )}

            <div className="grid gap-3 lg:grid-cols-2">
              <Card>
                <CardHeader>
                  <CardTitle>{t('providers.accounts.title')}</CardTitle>
                </CardHeader>
                <CardContent className="flex flex-col gap-4">
                  {providerAccounts.length === 0 ? (
                    <p className="text-sm text-foreground-muted">{t('providers.accounts.empty')}</p>
                  ) : (
                    providerAccounts.map((account) => {
                      const budget = accountBudget(account, budgets);
                      return (
                        <div key={account.id} className="flex flex-col gap-2">
                          <div className="flex flex-wrap items-center gap-2 text-sm">
                            <span className="font-medium">{account.label}</span>
                            <Badge variant={accountStateVariant(account.state)}>
                              {t(`status.accountState.${account.state}`)}
                            </Badge>
                          </div>
                          <ConsumptionBar
                            used={account.quotaUsedUsd}
                            limit={account.quotaLimitUsd}
                            alertThresholdPct={budget?.alertThresholdPct ?? 80}
                            label={t('providers.accounts.quotaBar', { label: account.label })}
                          />
                          <p className="text-xs text-foreground-muted">
                            {account.quotaLimitUsd === null
                              ? t('providers.accounts.quotaNoLimit', {
                                  used: formatCurrencyUSD(account.quotaUsedUsd),
                                })
                              : t('providers.accounts.quota', {
                                  used: formatCurrencyUSD(account.quotaUsedUsd),
                                  limit: formatCurrencyUSD(account.quotaLimitUsd),
                                })}
                          </p>
                          {budget && (
                            <p className="text-xs text-foreground-muted">
                              {t('providers.accounts.window', {
                                period: t(`status.budgetPeriod.${budget.period}`),
                                reset: formatDate(nextBudgetReset(budget.period, now)),
                              })}
                            </p>
                          )}
                        </div>
                      );
                    })
                  )}
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle>{t('providers.models.title')}</CardTitle>
                </CardHeader>
                <CardContent className="flex flex-col gap-3">
                  {providerModels.length === 0 ? (
                    <p className="text-sm text-foreground-muted">{t('providers.models.empty')}</p>
                  ) : (
                    providerModels.map((model) => (
                      <div
                        key={model.id}
                        className="flex flex-col gap-1 rounded-md border border-border p-3"
                      >
                        <div className="flex flex-wrap items-center gap-2 text-sm">
                          <span className="font-medium">{model.displayName}</span>
                          <span className="text-xs text-foreground-muted">{model.name}</span>
                          <Badge variant={model.enabled ? 'success' : 'outline'}>
                            {model.enabled ? t('providers.enabled') : t('providers.disabled')}
                          </Badge>
                        </div>
                        <div className="flex flex-wrap gap-1">
                          {model.capabilities.map((capability) => (
                            <Badge key={capability} variant="info">
                              {t(`status.modelCapability.${capability}`)}
                            </Badge>
                          ))}
                        </div>
                        <p className="text-xs text-foreground-muted">
                          {t('providers.models.meta', {
                            context: formatNumber(model.contextWindow),
                            input:
                              model.costPer1kInputUsd === null
                                ? '—'
                                : formatCurrencyUSD(model.costPer1kInputUsd),
                            output:
                              model.costPer1kOutputUsd === null
                                ? '—'
                                : formatCurrencyUSD(model.costPer1kOutputUsd),
                          })}
                        </p>
                      </div>
                    ))
                  )}
                </CardContent>
              </Card>
            </div>
          </section>
        );
      })}

      <section className="flex flex-col gap-3" aria-labelledby="budgets-section">
        <h2 id="budgets-section" className="font-heading text-lg font-semibold">
          {t('providers.budgets.title')}
        </h2>
        {budgets.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('providers.budgets.empty')}</p>
        ) : (
          <div className="grid gap-3 lg:grid-cols-3">
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
      </section>

      <section className="flex flex-col gap-3" aria-labelledby="routing-section">
        <div className="flex flex-wrap items-center gap-3">
          <h2 id="routing-section" className="font-heading text-lg font-semibold">
            {t('providers.routing.title')}
          </h2>
        </div>
        {(routingQuery.data ?? []).length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('providers.routing.empty')}</p>
        ) : (
          (routingQuery.data ?? []).map((policy) => (
            <Card key={policy.id}>
              <CardHeader className="flex-row flex-wrap items-center gap-3">
                <CardTitle>{policy.name}</CardTitle>
                <Badge variant={policy.active ? 'success' : 'outline'}>
                  {policy.active ? t('providers.routing.active') : t('providers.routing.inactive')}
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
          ))
        )}
      </section>

      {editingPolicy && (
        <RoutingPolicyDialog
          policy={editingPolicy}
          models={models}
          onClose={() => setEditingPolicy(null)}
        />
      )}
    </div>
  );
}
