import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';

import type { Agent } from '@/api';
import { Button, Field, Input, Select } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';

import type { DeliverySummary } from '../api/types';
import { useConfigurePlanning } from '../hooks/use-delivery';

export function DeliveryPlanningDialog({
  delivery,
  agents,
  onClose,
  onSaved,
}: {
  delivery: DeliverySummary;
  agents: readonly Agent[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const projectAgents = useMemo(
    () => agents.filter((agent) => agent.projectId === delivery.projectId),
    [agents, delivery.projectId],
  );
  const [ownerAgentId, setOwnerAgentId] = useState(
    projectAgents.some((agent) => agent.id === delivery.owner)
      ? (delivery.owner ?? '')
      : (projectAgents[0]?.id ?? ''),
  );
  const [committedDate, setCommittedDate] = useState(
    delivery.committedDate?.slice(0, 10) ?? '',
  );
  const [forecastDate, setForecastDate] = useState(
    delivery.forecastDate?.slice(0, 10) ?? '',
  );
  const planning = useConfigurePlanning(delivery.deliveryId);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!ownerAgentId || !committedDate) return;
    planning.mutate(
      {
        ownerAgentId,
        committedDate: new Date(`${committedDate}T12:00:00.000Z`).toISOString(),
        forecastDate: forecastDate
          ? new Date(`${forecastDate}T12:00:00.000Z`).toISOString()
          : null,
      },
      { onSuccess: onSaved },
    );
  }

  return (
    <ModalDialog
      label={t('delivery.planning.title', { name: delivery.name })}
      onClose={onClose}
      className="max-w-lg"
    >
      <form className="flex flex-col gap-4" onSubmit={submit}>
        <div>
          <h2 className="text-lg font-semibold text-foreground">
            {t('delivery.planning.title', { name: delivery.name })}
          </h2>
          <p className="mt-1 text-sm text-foreground-muted">{t('delivery.planning.explain')}</p>
        </div>
        <Field
          htmlFor="delivery-owner"
          label={t('delivery.planning.owner')}
          hint={t('delivery.planning.ownerHint')}
        >
          <Select
            id="delivery-owner"
            value={ownerAgentId}
            onChange={(event) => setOwnerAgentId(event.target.value)}
            required
          >
            <option value="">{t('delivery.planning.selectOwner')}</option>
            {projectAgents.map((agent) => (
              <option key={agent.id} value={agent.id}>
                {agent.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field
          htmlFor="delivery-forecast-date"
          label={t('delivery.planning.forecastDate')}
          hint={t('delivery.planning.forecastDateHint')}
        >
          <Input
            id="delivery-forecast-date"
            type="date"
            value={forecastDate}
            onChange={(event) => setForecastDate(event.target.value)}
          />
        </Field>
        <Field
          htmlFor="delivery-date"
          label={t('delivery.planning.date')}
          hint={t('delivery.planning.dateHint')}
        >
          <Input
            id="delivery-date"
            type="date"
            value={committedDate}
            onChange={(event) => setCommittedDate(event.target.value)}
            required
          />
        </Field>
        {projectAgents.length === 0 && (
          <p className="text-sm text-warning">{t('delivery.planning.noAgents')}</p>
        )}
        {planning.isError && (
          <p role="alert" className="text-sm text-error">
            {t('delivery.planning.error')}
          </p>
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button
            type="submit"
            disabled={!ownerAgentId || !committedDate || planning.isPending}
          >
            {planning.isPending ? t('delivery.planning.saving') : t('delivery.planning.save')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}
