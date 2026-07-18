import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Model, Ulid } from '@/api';
import { Button, Field, Select, Skeleton, Textarea } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  useEnabledModels,
  useHandoffChief,
} from '@/features/orchestrator/hooks/use-orchestrator';

export interface HandoffWizardProps {
  projectId: Ulid;
  /** Modelo atual do chefe (para a opção "manter modelo atual"). */
  currentModel: Model | null;
  onClose: () => void;
}

/**
 * Wizard de passagem de bastão em 2 etapas: (1) escolha do modelo do novo
 * chefe (ou manter o atual) + motivo obrigatório; (2) confirmação com o
 * resumo das escolhas → `handoffChief`. O sucesso invalida as queries da
 * feature — o card do chefe reflete o novo modelo.
 */
export function HandoffWizard({ projectId, currentModel, onClose }: HandoffWizardProps) {
  const { t } = useTranslation();
  const modelsQuery = useEnabledModels();
  const handoffMutation = useHandoffChief(projectId);

  const [step, setStep] = useState<1 | 2>(1);
  // Nulo = manter o modelo atual (targetModelId omitido no comando).
  const [targetModelId, setTargetModelId] = useState<Ulid | ''>('');
  const [reason, setReason] = useState('');
  const [reasonError, setReasonError] = useState<string | null>(null);

  const chosenModel =
    (modelsQuery.data ?? []).find((model) => model.id === targetModelId) ?? null;

  function goNext() {
    if (reason.trim().length === 0) {
      setReasonError(t('orchestrator.handoff.reasonRequired'));
      return;
    }
    setReasonError(null);
    setStep(2);
  }

  function confirm() {
    handoffMutation.mutate(
      {
        note: reason.trim(),
        ...(targetModelId === '' ? {} : { targetModelId }),
      },
      { onSuccess: onClose },
    );
  }

  return (
    <ModalDialog label={t('orchestrator.handoff.title')} onClose={onClose}>
      <h2 className="font-heading text-lg font-semibold">{t('orchestrator.handoff.title')}</h2>

      {modelsQuery.isPending ? (
        <div className="flex flex-col gap-2" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : modelsQuery.isError ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={() => void modelsQuery.refetch()}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : step === 1 ? (
        <>
          <p className="text-sm text-foreground-muted">{t('orchestrator.handoff.stepChoice')}</p>
          <Field htmlFor="handoff-model" label={t('orchestrator.handoff.modelLabel')}>
            <Select
              id="handoff-model"
              value={targetModelId}
              onChange={(event) => setTargetModelId(event.target.value)}
            >
              <option value="">
                {currentModel
                  ? `${t('orchestrator.handoff.keepCurrentModel')} (${currentModel.displayName})`
                  : t('orchestrator.handoff.keepCurrentModel')}
              </option>
              {(modelsQuery.data ?? []).map((model) => (
                <option key={model.id} value={model.id}>
                  {model.displayName}
                </option>
              ))}
            </Select>
          </Field>
          <Field
            htmlFor="handoff-reason"
            label={t('orchestrator.handoff.reasonLabel')}
            required
            requiredLabel={t('common.requiredMark')}
            error={reasonError ?? undefined}
          >
            <Textarea
              id="handoff-reason"
              value={reason}
              onChange={(event) => {
                setReason(event.target.value);
                if (reasonError) setReasonError(null);
              }}
            />
          </Field>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>
              {t('common.actions.cancel')}
            </Button>
            <Button type="button" onClick={goNext}>
              {t('common.actions.next')}
            </Button>
          </div>
        </>
      ) : (
        <>
          <p className="text-sm text-foreground-muted">{t('orchestrator.handoff.stepConfirm')}</p>
          <dl className="flex flex-col gap-2 rounded-md border border-border bg-surface-elevated p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <dt className="text-sm text-foreground-muted">
                {t('orchestrator.handoff.summaryModel')}
              </dt>
              <dd className="text-sm font-medium">
                {chosenModel
                  ? chosenModel.displayName
                  : currentModel
                    ? `${t('orchestrator.handoff.keepCurrentModel')} (${currentModel.displayName})`
                    : t('orchestrator.handoff.keepCurrentModel')}
              </dd>
            </div>
            <div className="flex flex-wrap items-start justify-between gap-2">
              <dt className="text-sm text-foreground-muted">
                {t('orchestrator.handoff.summaryReason')}
              </dt>
              <dd className="text-sm font-medium">{reason.trim()}</dd>
            </div>
          </dl>
          {handoffMutation.isError ? (
            <p role="alert" className="text-sm text-error">
              {t('orchestrator.mutation.error')}
            </p>
          ) : null}
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => setStep(1)}>
              {t('common.actions.previous')}
            </Button>
            <Button type="button" disabled={handoffMutation.isPending} onClick={confirm}>
              {t('orchestrator.handoff.confirm')}
            </Button>
          </div>
        </>
      )}
    </ModalDialog>
  );
}
