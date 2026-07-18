import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { PROTOTYPING_MODES, type Project, type PrototypingMode } from '@/api';
import { Button, Select, Textarea } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useUpdatePrototyping } from '@/features/prototypes/hooks/use-prototypes';

export interface PrototypingModeDialogProps {
  project: Project;
  onClose: () => void;
}

/**
 * Seleção do cenário de prototipação do projeto. `notApplicable` exige
 * motivo (waiver — dispensa formal); sair de `notApplicable` zera o waiver.
 */
export function PrototypingModeDialog({ project, onClose }: PrototypingModeDialogProps) {
  const { t } = useTranslation();
  const updatePrototyping = useUpdatePrototyping();
  const [mode, setMode] = useState<PrototypingMode>(project.prototyping.mode);
  const [reason, setReason] = useState(project.prototyping.waiver?.reason ?? '');
  const [error, setError] = useState<string | null>(null);

  async function save() {
    if (mode === 'notApplicable' && reason.trim() === '') {
      setError(t('prototypes.mode.waiverRequired'));
      return;
    }
    try {
      await updatePrototyping.mutateAsync({
        projectId: project.id,
        prototyping: {
          mode,
          waiver:
            mode === 'notApplicable'
              ? { reason: reason.trim(), grantedAt: new Date().toISOString() }
              : null,
        },
      });
      onClose();
    } catch {
      setError(t('prototypes.mode.error'));
    }
  }

  return (
    <ModalDialog label={t('prototypes.mode.title')} onClose={onClose}>
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <div className="flex flex-col gap-1">
          <label htmlFor="prototyping-mode" className="text-sm font-medium">
            {t('prototypes.mode.label')}
          </label>
          <Select
            id="prototyping-mode"
            value={mode}
            onChange={(event) => {
              setMode(event.target.value as PrototypingMode);
              setError(null);
            }}
          >
            {PROTOTYPING_MODES.map((option) => (
              <option key={option} value={option}>
                {t(`status.prototypingMode.${option}`)}
              </option>
            ))}
          </Select>
          <p className="text-xs text-foreground-muted">{t(`prototypes.mode.help.${mode}`)}</p>
        </div>
        {mode === 'notApplicable' && (
          <div className="flex flex-col gap-1">
            <label htmlFor="prototyping-waiver" className="text-sm font-medium">
              {t('prototypes.mode.waiverLabel')}
            </label>
            <Textarea
              id="prototyping-waiver"
              value={reason}
              onChange={(event) => {
                setReason(event.target.value);
                setError(null);
              }}
            />
          </div>
        )}
        {error && (
          <p role="alert" className="text-sm text-error">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button type="submit" disabled={updatePrototyping.isPending}>
            {t('common.actions.save')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}
