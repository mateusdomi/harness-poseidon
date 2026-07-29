import { useTranslation } from 'react-i18next';
import { CalendarClock, Download, Flag, PlayCircle } from 'lucide-react';

import { Badge, Button } from '@/design-system';

import { formatDate } from '../lib/format';
import type { DeliverySummary } from '../api/types';

/**
 * Rastreamento de encomenda (D10): a resposta que o dono procura ao abrir a
 * Central de Entregas — quando começou, para quando ele pediu, e como está.
 *
 * O prazo aqui é DECLARAÇÃO do dono, nunca cálculo: quando ele não disse, a
 * tela diz "sem prazo definido" em vez de mostrar a previsão no lugar. A
 * orientação de risco só aparece quando existe prazo para comparar — sem
 * prazo, ninguém está atrasado.
 */
export function TrackingCard({
  delivery,
  onDownloadDocuments,
  downloading = false,
}: {
  delivery: DeliverySummary;
  onDownloadDocuments: (delivery: DeliverySummary) => void;
  downloading?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const started = formatDate(delivery.startedAt, i18n.language);
  const deadline = formatDate(delivery.targetDeadline, i18n.language);
  const risk = scheduleRisk(delivery);

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4">
      <dl className="grid grid-cols-1 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
        <div className="flex items-start gap-2">
          <PlayCircle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-foreground-muted" />
          <div>
            <dt className="text-xs text-foreground-muted">{t('delivery.tracking.startedAt')}</dt>
            <dd className="text-foreground">{started ?? t('delivery.tracking.noStart')}</dd>
          </div>
        </div>
        <div className="flex items-start gap-2">
          <Flag aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-foreground-muted" />
          <div>
            <dt className="text-xs text-foreground-muted">{t('delivery.tracking.deadline')}</dt>
            <dd className="text-foreground">{deadline ?? t('delivery.tracking.noDeadline')}</dd>
          </div>
        </div>
        <div className="flex items-start gap-2">
          <CalendarClock aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-foreground-muted" />
          <div>
            <dt className="text-xs text-foreground-muted">{t('delivery.tracking.progress')}</dt>
            <dd className="text-foreground">
              {t('delivery.tracking.progressValue', {
                done: delivery.milestonesDone,
                total: delivery.milestonesTotal,
              })}
            </dd>
          </div>
        </div>
      </dl>

      {risk !== null && (
        <p className="rounded-lg bg-warning/10 px-3 py-2 text-sm text-foreground">
          <Badge variant="warning" className="mr-2">
            {t('delivery.tracking.attention')}
          </Badge>
          {t(`delivery.tracking.guidance.${risk}`)}
        </p>
      )}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-xs text-foreground-muted">
          {t('delivery.tracking.documentsHint')}
        </span>
        <Button
          size="sm"
          variant="secondary"
          disabled={downloading}
          onClick={(event) => {
            event.stopPropagation();
            onDownloadDocuments(delivery);
          }}
        >
          <Download aria-hidden="true" className="mr-2 size-4" />
          {downloading
            ? t('delivery.tracking.downloading')
            : t('delivery.tracking.downloadDocuments')}
        </Button>
      </div>
    </div>
  );
}

/**
 * Como o cronograma está em relação ao que o dono pediu. Sem prazo declarado
 * não há veredito — inventar atraso onde ninguém combinou data seria mentir.
 */
export function scheduleRisk(
  delivery: Pick<DeliverySummary, 'targetDeadline' | 'forecastDate'>,
): 'late' | 'tight' | null {
  if (!delivery.targetDeadline || !delivery.forecastDate) return null;
  const deadline = new Date(delivery.targetDeadline).getTime();
  const forecast = new Date(delivery.forecastDate).getTime();
  if (Number.isNaN(deadline) || Number.isNaN(forecast)) return null;
  if (forecast > deadline) return 'late';
  const week = 7 * 24 * 60 * 60 * 1000;
  return deadline - forecast <= week ? 'tight' : null;
}
