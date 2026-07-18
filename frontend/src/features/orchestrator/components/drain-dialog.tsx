import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Ulid } from '@/api';
import { Button, Field, Textarea } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useDrainChiefTasks } from '@/features/orchestrator/hooks/use-orchestrator';

export interface DrainDialogProps {
  projectId: Ulid;
  onClose: () => void;
}

/**
 * Confirmação de "drenar tarefas": explica o efeito (tarefas em andamento
 * voltam para `ready`, attempts running são canceladas, agentes ficam
 * ociosos) e aceita uma observação opcional antes de executar.
 */
export function DrainDialog({ projectId, onClose }: DrainDialogProps) {
  const { t } = useTranslation();
  const drainMutation = useDrainChiefTasks(projectId);
  const [note, setNote] = useState('');

  function confirm() {
    const trimmed = note.trim();
    drainMutation.mutate(trimmed === '' ? {} : { note: trimmed }, { onSuccess: onClose });
  }

  return (
    <ModalDialog label={t('orchestrator.drain.title')} onClose={onClose}>
      <h2 className="font-heading text-lg font-semibold">{t('orchestrator.drain.title')}</h2>
      <p className="text-sm text-foreground-muted">{t('orchestrator.drain.description')}</p>
      <Field htmlFor="drain-note" label={t('orchestrator.drain.noteLabel')}>
        <Textarea
          id="drain-note"
          value={note}
          onChange={(event) => setNote(event.target.value)}
        />
      </Field>
      {drainMutation.isError ? (
        <p role="alert" className="text-sm text-error">
          {t('orchestrator.mutation.error')}
        </p>
      ) : null}
      <div className="flex flex-wrap justify-end gap-2">
        <Button type="button" variant="outline" onClick={onClose}>
          {t('common.actions.cancel')}
        </Button>
        <Button
          type="button"
          variant="destructive"
          disabled={drainMutation.isPending}
          onClick={confirm}
        >
          {t('orchestrator.drain.confirm')}
        </Button>
      </div>
    </ModalDialog>
  );
}
