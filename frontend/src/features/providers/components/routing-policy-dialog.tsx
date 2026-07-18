import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Model, RoutingPolicy, RoutingRule } from '@/api';
import { Button, Checkbox, Input, Select } from '@/design-system';
import { formatCurrencyUSD } from '@/lib/format';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useUpdateRoutingPolicy } from '@/features/providers/hooks/use-providers';
import { modelDisplayName } from '@/features/providers/lib/providers-derive';

export interface RoutingPolicyDialogProps {
  policy: RoutingPolicy;
  models: Model[];
  onClose: () => void;
}

/**
 * Edição estruturada da política de roteamento (nunca JSON cru), com passo
 * de confirmação: resumo das regras editadas antes de salvar.
 */
export function RoutingPolicyDialog({ policy, models, onClose }: RoutingPolicyDialogProps) {
  const { t } = useTranslation();
  const updatePolicy = useUpdateRoutingPolicy();
  const [rules, setRules] = useState<RoutingRule[]>(() => structuredClone(policy.rules));
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function patchRule(index: number, patch: Partial<RoutingRule>) {
    setRules((current) =>
      current.map((rule, ruleIndex) => (ruleIndex === index ? { ...rule, ...patch } : rule)),
    );
  }

  function toggleFallback(index: number, modelId: string, checked: boolean) {
    const rule = rules[index];
    const fallbackModelIds = checked
      ? [...rule.fallbackModelIds, modelId]
      : rule.fallbackModelIds.filter((id) => id !== modelId);
    patchRule(index, { fallbackModelIds });
  }

  async function save() {
    try {
      await updatePolicy.mutateAsync({ id: policy.id, rules });
      onClose();
    } catch {
      setError(t('providers.routing.error'));
    }
  }

  return (
    <ModalDialog label={t('providers.routing.editTitle')} onClose={onClose}>
      <div className="flex flex-col gap-4">
        {!confirming ? (
          <>
            <ul className="flex flex-col gap-4">
              {rules.map((rule, index) => (
                <li key={index} className="flex flex-col gap-3 rounded-md border border-border p-3">
                  <span className="text-sm font-medium">
                    {rule.taskKind ?? t('providers.routing.defaultRule')}
                  </span>
                  <div className="flex flex-col gap-1">
                    <label htmlFor={`rule-preferred-${index}`} className="text-xs font-medium">
                      {t('providers.routing.preferred')}
                    </label>
                    <Select
                      id={`rule-preferred-${index}`}
                      value={rule.preferredModelId}
                      onChange={(event) => patchRule(index, { preferredModelId: event.target.value })}
                    >
                      {models.map((model) => (
                        <option key={model.id} value={model.id}>
                          {model.displayName}
                        </option>
                      ))}
                    </Select>
                  </div>
                  <fieldset className="flex flex-col gap-1">
                    <legend className="text-xs font-medium">{t('providers.routing.fallbacks')}</legend>
                    {models
                      .filter((model) => model.id !== rule.preferredModelId)
                      .map((model) => (
                        <label key={model.id} className="flex min-h-11 items-center gap-2 text-sm">
                          <Checkbox
                            checked={rule.fallbackModelIds.includes(model.id)}
                            onChange={(event) => toggleFallback(index, model.id, event.target.checked)}
                          />
                          {model.displayName}
                        </label>
                      ))}
                  </fieldset>
                  <div className="flex flex-col gap-1">
                    <label htmlFor={`rule-max-cost-${index}`} className="text-xs font-medium">
                      {t('providers.routing.maxCost')}
                    </label>
                    <Input
                      id={`rule-max-cost-${index}`}
                      type="number"
                      min="0"
                      step="0.01"
                      value={rule.maxCostPerAttemptUsd ?? ''}
                      placeholder={t('providers.routing.maxCostPlaceholder')}
                      onChange={(event) =>
                        patchRule(index, {
                          maxCostPerAttemptUsd:
                            event.target.value === '' ? null : Number(event.target.value),
                        })
                      }
                    />
                  </div>
                </li>
              ))}
            </ul>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={onClose}>
                {t('common.actions.cancel')}
              </Button>
              <Button type="button" onClick={() => setConfirming(true)}>
                {t('common.actions.next')}
              </Button>
            </div>
          </>
        ) : (
          <>
            <p className="text-sm">{t('providers.routing.confirmBody')}</p>
            <ul className="flex flex-col gap-2 text-sm">
              {rules.map((rule, index) => (
                <li key={index} className="rounded-md border border-border p-3">
                  <span className="font-medium">
                    {rule.taskKind ?? t('providers.routing.defaultRule')}
                  </span>
                  : {modelDisplayName(rule.preferredModelId, models)}
                  {rule.fallbackModelIds.length > 0 && (
                    <span className="text-foreground-muted">
                      {' → '}
                      {rule.fallbackModelIds.map((id) => modelDisplayName(id, models)).join(', ')}
                    </span>
                  )}
                  {rule.maxCostPerAttemptUsd !== null && (
                    <span className="text-foreground-muted">
                      {' · '}
                      {formatCurrencyUSD(rule.maxCostPerAttemptUsd)}
                    </span>
                  )}
                </li>
              ))}
            </ul>
            {error && (
              <p role="alert" className="text-sm text-error">
                {error}
              </p>
            )}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setConfirming(false)}>
                {t('common.actions.previous')}
              </Button>
              <Button type="button" disabled={updatePolicy.isPending} onClick={() => void save()}>
                {t('providers.routing.confirm')}
              </Button>
            </div>
          </>
        )}
      </div>
    </ModalDialog>
  );
}
