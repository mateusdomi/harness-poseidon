import { useTranslation } from 'react-i18next';
import {
  Bot,
  Boxes,
  Cloud,
  Cpu,
  Plus,
  RefreshCw,
  Server,
  Sparkles,
  type LucideIcon,
} from 'lucide-react';

import type { Account, Budget, Model, Provider, ProviderKind } from '@/api';
import { Badge, Button } from '@/design-system';
import { formatCurrencyUSD, formatNumber } from '@/lib/format';
import { AccountCard } from '@/features/providers/components/account-card';
import { CollapsibleSection } from '@/features/providers/components/collapsible-section';
import { accountBudget } from '@/features/providers/lib/providers-derive';

/**
 * Ícone por tipo de provedor — escalável: novos kinds caem no fallback
 * neutro sem quebrar a tela (o dono usa 7 provedores; só 3 vêm pré-cadastrados).
 */
const PROVIDER_KIND_ICONS: Record<ProviderKind, LucideIcon> = {
  openai: Sparkles,
  anthropic: Bot,
  azureOpenai: Cloud,
  google: Boxes,
  ollama: Cpu,
  custom: Server,
};

function providerIcon(kind: ProviderKind): LucideIcon {
  return PROVIDER_KIND_ICONS[kind] ?? Server;
}

export interface ProviderCardProps {
  provider: Provider;
  accounts: Account[];
  models: Model[];
  budgets: Budget[];
  now: Date;
  syncPending: boolean;
  syncFeedbackCount: number | null;
  enablePending: boolean;
  disablePending: boolean;
  onSync: () => void;
  onNewAccount: () => void;
  onEditAccount: (account: Account) => void;
  onEnableAccount: (account: Account) => void;
  onDisableAccount: (account: Account) => void;
  onDeleteAccount: (account: Account) => void;
}

/**
 * Cartão de um provedor: cabeçalho com identidade/estado e CTA de conectar
 * conta em destaque; contas conectadas como conteúdo primário; catálogo de
 * modelos recolhível (conteúdo secundário) mostrando quantos estão habilitados.
 */
export function ProviderCard({
  provider,
  accounts,
  models,
  budgets,
  now,
  syncPending,
  syncFeedbackCount,
  enablePending,
  disablePending,
  onSync,
  onNewAccount,
  onEditAccount,
  onEnableAccount,
  onDisableAccount,
  onDeleteAccount,
}: ProviderCardProps) {
  const { t } = useTranslation();
  const Icon = providerIcon(provider.kind);
  const isLocal = provider.kind === 'ollama';
  const enabledModels = models.filter((model) => model.enabled).length;

  return (
    <section
      aria-labelledby={`provider-${provider.id}`}
      className="overflow-hidden rounded-lg border border-border bg-surface text-foreground shadow-card"
    >
      <div className="flex flex-col gap-4 border-b border-border p-4 sm:p-6">
        <div className="flex flex-wrap items-start gap-3">
          <span
            aria-hidden="true"
            className="flex size-11 shrink-0 items-center justify-center rounded-lg bg-surface-elevated text-brand"
          >
            <Icon className="size-6" />
          </span>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <h2
                id={`provider-${provider.id}`}
                className="font-heading text-lg font-semibold"
              >
                {provider.name}
              </h2>
              <Badge variant="outline">{t(`status.providerKind.${provider.kind}`)}</Badge>
              <Badge variant={provider.enabled ? 'success' : 'outline'}>
                {provider.enabled ? t('providers.enabled') : t('providers.disabled')}
              </Badge>
            </div>
            <p className="mt-1">
              <Badge variant={accounts.length === 0 ? 'warning' : 'success'}>
                {accounts.length === 0
                  ? t('providers.status.noAccount')
                  : t('providers.status.withAccounts', { count: accounts.length })}
              </Badge>
            </p>
          </div>
          <div className="flex w-full gap-2 sm:w-auto">
            <Button
              type="button"
              variant="primary"
              size="sm"
              className="flex-1 sm:flex-none"
              onClick={onNewAccount}
            >
              <Plus aria-hidden="true" />
              {t('providers.accounts.connect')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={syncPending}
              onClick={onSync}
            >
              <RefreshCw aria-hidden="true" />
              {t('providers.sync.action')}
            </Button>
          </div>
        </div>
        {syncFeedbackCount !== null && (
          <p role="status" className="text-sm text-foreground-muted">
            {t('providers.sync.feedback', { count: syncFeedbackCount })}
          </p>
        )}
      </div>

      <div className="flex flex-col gap-4 p-4 sm:p-6">
        <section aria-labelledby={`accounts-${provider.id}`} className="flex flex-col gap-3">
          <h3
            id={`accounts-${provider.id}`}
            className="text-xs font-semibold uppercase tracking-wide text-foreground-muted"
          >
            {t('providers.accounts.title')}
          </h3>
          {accounts.length === 0 ? (
            <div className="flex flex-col items-center gap-3 rounded-lg border border-dashed border-border p-6 text-center">
              <p className="text-sm text-foreground-muted">
                {t('providers.accounts.emptyCta')}
              </p>
              <Button type="button" variant="primary" size="sm" onClick={onNewAccount}>
                <Plus aria-hidden="true" />
                {t('providers.accounts.connect')}
              </Button>
            </div>
          ) : (
            <div className="grid gap-3 md:grid-cols-2">
              {accounts.map((account) => (
                <AccountCard
                  key={account.id}
                  account={account}
                  budget={accountBudget(account, budgets)}
                  now={now}
                  isLocal={isLocal}
                  onEdit={() => onEditAccount(account)}
                  onEnable={() => onEnableAccount(account)}
                  onDisable={() => onDisableAccount(account)}
                  onDelete={() => onDeleteAccount(account)}
                  enablePending={enablePending}
                  disablePending={disablePending}
                />
              ))}
            </div>
          )}
        </section>

        <CollapsibleSection
          title={t('providers.models.title')}
          summary={t('providers.models.summary', {
            enabled: enabledModels,
            total: models.length,
          })}
        >
          {models.length === 0 ? (
            <p className="text-sm text-foreground-muted">{t('providers.models.empty')}</p>
          ) : (
            <ul className="grid gap-3 md:grid-cols-2">
              {models.map((model) => (
                <li
                  key={model.id}
                  aria-label={model.displayName}
                  className="flex flex-col gap-1 rounded-md border border-border p-3"
                >
                  <div className="flex flex-wrap items-center gap-2 text-sm">
                    <span className="font-medium">{model.displayName}</span>
                    <span className="text-xs text-foreground-muted">{model.name}</span>
                    <Badge variant={model.enabled ? 'success' : 'outline'}>
                      {model.enabled ? t('providers.enabled') : t('providers.disabled')}
                    </Badge>
                    {isLocal && <Badge variant="info">{t('providers.local')}</Badge>}
                  </div>
                  {model.capabilities.length > 0 && (
                    <div className="flex flex-wrap gap-1">
                      {model.capabilities.map((capability) => (
                        <Badge key={capability} variant="outline">
                          {t(`status.modelCapability.${capability}`)}
                        </Badge>
                      ))}
                    </div>
                  )}
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
                  {model.effortMappings.length > 0 && (
                    <div className="flex flex-wrap items-center gap-1 text-xs text-foreground-muted">
                      <span>{t('providers.models.effortMappings')}:</span>
                      {model.effortMappings.map((mapping) => (
                        <Badge key={mapping.effort} variant="outline">
                          {t(`providers.models.effort.${mapping.effort}`, {
                            value: mapping.providerValue,
                          })}
                        </Badge>
                      ))}
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
        </CollapsibleSection>
      </div>
    </section>
  );
}
