import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { operationModeSchema, type OperationMode, type Workflow } from '@/api';
import { Badge, Button, Checkbox, Field, Select, Textarea } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { operationModeVariant } from '@/lib/status';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useSetOperationMode } from '@/features/workflows/hooks/use-workflows';

export interface OperationModeCardProps {
  workflow: Workflow;
  /** Todos os nomes de gates da versão ativa (seleção do modo semiautônomo). */
  gateNames: string[];
}

/**
 * Modo de operação atual + troca protegida: dialog exige checkbox de
 * aceite de risco + justificativa (registrada em `riskAcceptances` via
 * comando `setWorkflowOperationMode`). No semiautônomo, é obrigatório
 * selecionar ao menos um gate que pausa para aprovação humana.
 */
export function OperationModeCard({ workflow, gateNames }: OperationModeCardProps) {
  const { t, i18n } = useTranslation();
  const setOperationMode = useSetOperationMode();
  const [dialogOpen, setDialogOpen] = useState(false);
  const [mode, setMode] = useState<OperationMode>(workflow.operationMode);
  const [pauseGates, setPauseGates] = useState<string[]>(workflow.semiautonomousPauseGates);
  const [accepted, setAccepted] = useState(false);
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);

  function openDialog() {
    setMode(workflow.operationMode);
    setPauseGates(workflow.semiautonomousPauseGates);
    setAccepted(false);
    setNote('');
    setError(null);
    setDialogOpen(true);
  }

  function toggleGate(name: string) {
    setPauseGates((current) =>
      current.includes(name) ? current.filter((g) => g !== name) : [...current, name],
    );
  }

  function confirm() {
    if (!accepted || note.trim().length === 0) {
      setError(t('workflows.mode.errors.acceptanceRequired'));
      return;
    }
    if (mode === 'semiautonomous' && pauseGates.length === 0) {
      setError(t('workflows.mode.errors.gatesRequired'));
      return;
    }
    setOperationMode.mutate(
      {
        workflowId: workflow.id,
        input: {
          mode,
          semiautonomousPauseGates: mode === 'semiautonomous' ? pauseGates : undefined,
          riskAcceptanceNote: note.trim(),
        },
      },
      { onSuccess: () => setDialogOpen(false) },
    );
  }

  return (
    <section
      aria-labelledby="operation-mode-title"
      className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4"
    >
      <div className="flex flex-wrap items-center gap-2">
        <h2 id="operation-mode-title" className="font-heading text-lg font-semibold">
          {t('workflows.mode.title')}
        </h2>
        <Badge variant={operationModeVariant(workflow.operationMode)}>
          {t(`status.operationMode.${workflow.operationMode}`)}
        </Badge>
        <Button type="button" variant="outline" size="sm" className="ml-auto" onClick={openDialog}>
          {t('workflows.mode.change')}
        </Button>
      </div>

      <p className="text-sm text-foreground-muted">
        {t(`workflows.mode.description.${workflow.operationMode}`)}
      </p>

      {workflow.operationMode === 'semiautonomous' && (
        <p className="text-sm text-foreground-muted">
          {workflow.semiautonomousPauseGates.length > 0
            ? t('workflows.mode.pauseGates', { gates: workflow.semiautonomousPauseGates.join(', ') })
            : t('workflows.mode.noPauseGates')}
        </p>
      )}

      {workflow.riskAcceptances.length > 0 && (
        <details className="text-xs text-foreground-muted">
          <summary className="cursor-pointer py-2">
            {t('workflows.mode.acceptances', { count: workflow.riskAcceptances.length })}
          </summary>
          <ul className="flex flex-col gap-1 pt-1">
            {workflow.riskAcceptances.map((acceptance, index) => (
              <li key={index}>
                {t('workflows.mode.acceptanceItem', {
                  mode: t(`status.operationMode.${acceptance.mode}`),
                  date: formatDateTime(acceptance.acceptedAt, i18n.language),
                  note: acceptance.note,
                })}
              </li>
            ))}
          </ul>
        </details>
      )}

      {dialogOpen && (
        <ModalDialog label={t('workflows.mode.dialogTitle')} onClose={() => setDialogOpen(false)}>
          <h3 className="font-heading text-lg font-semibold">{t('workflows.mode.dialogTitle')}</h3>

          <Field htmlFor="mode-select" label={t('workflows.mode.selectLabel')}>
            <Select
              id="mode-select"
              value={mode}
              onChange={(event) => {
                setMode(event.target.value as OperationMode);
                setError(null);
              }}
            >
              {operationModeSchema.options.map((option) => (
                <option key={option} value={option}>
                  {t(`status.operationMode.${option}`)}
                </option>
              ))}
            </Select>
          </Field>

          <p className="rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
            {t(`workflows.mode.risk.${mode}`)}
          </p>

          {mode === 'semiautonomous' && (
            <fieldset className="flex flex-col gap-2">
              <legend className="text-sm font-medium">
                {t('workflows.mode.pauseGatesLabel')}
              </legend>
              {gateNames.map((name) => (
                <label key={name} className="flex min-h-11 items-center gap-2 text-sm">
                  <Checkbox
                    checked={pauseGates.includes(name)}
                    onChange={() => toggleGate(name)}
                  />
                  {name}
                </label>
              ))}
            </fieldset>
          )}

          <Field
            htmlFor="mode-note"
            label={t('workflows.mode.noteLabel')}
            required
            requiredLabel={t('common.requiredMark')}
          >
            <Textarea
              id="mode-note"
              value={note}
              placeholder={t('workflows.mode.notePlaceholder')}
              onChange={(event) => {
                setNote(event.target.value);
                if (error) setError(null);
              }}
            />
          </Field>

          <label className="flex min-h-11 items-center gap-2 text-sm">
            <Checkbox
              checked={accepted}
              onChange={(event) => {
                setAccepted(event.target.checked);
                if (error) setError(null);
              }}
            />
            {t('workflows.mode.acceptCheckbox')}
          </label>

          {error && (
            <p role="alert" className="text-xs text-error">
              {error}
            </p>
          )}

          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={setOperationMode.isPending} onClick={confirm}>
              {t('workflows.mode.confirm')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setDialogOpen(false)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}
    </section>
  );
}
