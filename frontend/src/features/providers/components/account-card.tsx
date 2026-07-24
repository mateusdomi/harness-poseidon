import { useTranslation } from 'react-i18next';
import { Pencil, Power, PowerOff, Trash2 } from 'lucide-react';

import type { Account, Budget } from '@/api';
import { Badge, Button } from '@/design-system';
import { accountHealthVariant, accountStateVariant } from '@/lib/status';
import { formatCurrencyUSD, formatDate } from '@/lib/format';
import { ConsumptionBar } from '@/features/providers/components/consumption-bar';
import { nextBudgetReset } from '@/features/providers/lib/providers-derive';

export interface AccountCardProps {
  account: Account;
  budget: Budget | null;
  now: Date;
  /** Conta de provedor local (kind 'ollama') — sem custo/rede. */
  isLocal: boolean;
  onEdit: () => void;
  onEnable: () => void;
  onDisable: () => void;
  onDelete: () => void;
  enablePending: boolean;
  disablePending: boolean;
}

/**
 * Cartão de uma conta conectada: identidade, estado e saúde em destaque,
 * consumo da cota com barra, e ações operacionais (editar/habilitar/
 * desabilitar/remover). Elemento `article` rotulado pelo apelido da conta.
 */
export function AccountCard({
  account,
  budget,
  now,
  isLocal,
  onEdit,
  onEnable,
  onDisable,
  onDelete,
  enablePending,
  disablePending,
}: AccountCardProps) {
  const { t } = useTranslation();

  return (
    <article
      aria-label={account.label}
      className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4"
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium text-foreground">{account.label}</span>
        <Badge variant={accountStateVariant(account.state)}>
          {t(`status.accountState.${account.state}`)}
        </Badge>
        <Badge variant={accountHealthVariant(account.health)}>
          {t(`providers.accounts.health.${account.health}`)}
        </Badge>
        {/* O contrato não tem flag "local" na conta: deriva do kind 'ollama'. */}
        {isLocal && <Badge variant="info">{t('providers.local')}</Badge>}
      </div>

      <p className="text-xs text-foreground-muted">
        {t('providers.accounts.meta', {
          identity: account.identity ?? t('providers.accounts.notProvided'),
          plan: t(`providers.accounts.plan.${account.plan}`),
          authentication: t(`providers.accounts.authentication.${account.authentication}`),
        })}
      </p>

      {account.capabilities.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {account.capabilities.map((capability) => (
            <Badge key={capability} variant="outline">
              {t(`providers.accounts.capability.${capability}`)}
            </Badge>
          ))}
        </div>
      )}

      <div className="flex flex-col gap-1">
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
        <p className="text-xs text-foreground-muted">
          {t('providers.accounts.window', {
            period: t(`providers.accounts.quotaWindow.${account.quotaWindow}`),
            reset:
              account.quotaResetsAt === null
                ? t('providers.accounts.noReset')
                : formatDate(account.quotaResetsAt),
          })}
        </p>
        {budget && account.quotaResetsAt === null && (
          <p className="text-xs text-foreground-muted">
            {t('providers.accounts.budgetReset', {
              reset: formatDate(nextBudgetReset(budget.period, now)),
            })}
          </p>
        )}
      </div>

      <div className="flex flex-wrap gap-2 border-t border-border pt-3">
        <Button type="button" variant="ghost" size="sm" onClick={onEdit}>
          <Pencil aria-hidden="true" />
          {t('providers.accounts.actions.edit')}
        </Button>
        {account.state === 'active' ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={disablePending}
            onClick={onDisable}
          >
            <PowerOff aria-hidden="true" />
            {t('providers.accounts.actions.disable')}
          </Button>
        ) : (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={enablePending}
            onClick={onEnable}
          >
            <Power aria-hidden="true" />
            {t('providers.accounts.actions.enable')}
          </Button>
        )}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="ml-auto text-error"
          disabled={account.state !== 'disabled'}
          title={
            account.state !== 'disabled'
              ? t('providers.accounts.delete.disableFirst')
              : undefined
          }
          onClick={onDelete}
        >
          <Trash2 aria-hidden="true" />
          {t('providers.accounts.actions.remove')}
        </Button>
      </div>
    </article>
  );
}
