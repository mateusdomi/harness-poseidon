import { useTranslation } from 'react-i18next';

import { Badge, Button, Card, CardContent } from '@/design-system';

import { formatDate } from '../lib/format';
import { attentionSignalLabel } from '../lib/attention-signal';
import type { DeliverySummary } from '../api/types';
import { SourceDisclosure } from './source-disclosure';
import { HealthBadge, PredictabilityBadge } from './status-badges';

/**
 * Portfólio de Entregas (DEL-01): cada entrega como um cartão clicável com
 * saúde, previsibilidade, dono, datas e sinais de atenção. A ação abre a
 * Entrega 360.
 */
export function PortfolioList({
  deliveries,
  onOpen,
  onConfigure,
  agentNames,
}: {
  deliveries: DeliverySummary[];
  onOpen: (deliveryId: string) => void;
  onConfigure: (delivery: DeliverySummary) => void;
  agentNames: ReadonlyMap<string, string>;
}) {
  const { t, i18n } = useTranslation();

  if (deliveries.length === 0) {
    return (
      <Card>
        <CardContent className="py-8 text-center text-sm text-foreground-muted">
          {t('delivery.portfolio.empty')}
        </CardContent>
      </Card>
    );
  }

  return (
    <ul className="flex flex-col gap-3" aria-label={t('delivery.portfolio.title')}>
      {deliveries.map((d) => {
        const committed = formatDate(d.committedDate, i18n.language);
        const forecast = formatDate(d.forecastDate, i18n.language);
        const owner = d.owner ? (agentNames.get(d.owner) ?? d.owner) : null;
        const missing = [
          ...(owner ? [] : [t('delivery.sources.ownerMissing')]),
          ...(d.committedDate ? [] : [t('delivery.sources.dateMissing')]),
          ...(d.forecastDate ? [] : [t('delivery.sources.forecastMissing')]),
        ];
        return (
          <li key={d.deliveryId}>
            <Card>
              <CardContent className="flex flex-col gap-3 py-4">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <button
                    type="button"
                    className="text-left text-base font-semibold text-foreground hover:underline focus-visible:underline focus-visible:outline-none"
                    onClick={() => onOpen(d.deliveryId)}
                    aria-label={t('delivery.portfolio.open', { name: d.name })}
                  >
                    {d.name}
                    <span className="ml-2 text-xs font-normal text-foreground-muted">{d.key}</span>
                  </button>
                  <div className="flex items-center gap-2">
                    <HealthBadge value={d.health} />
                    <PredictabilityBadge value={d.predictability} />
                  </div>
                </div>

                <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-4">
                  <div>
                    <dt className="text-xs text-foreground-muted">{t('delivery.portfolio.columns.owner')}</dt>
                    <dd className="text-foreground">{owner ?? t('delivery.portfolio.noOwner')}</dd>
                  </div>
                  <div>
                    <dt className="text-xs text-foreground-muted">{t('delivery.portfolio.columns.milestones')}</dt>
                    <dd className="text-foreground">
                      {d.milestonesDone}/{d.milestonesTotal}
                    </dd>
                  </div>
                  <div>
                    <dt className="text-xs text-foreground-muted">{t('delivery.portfolio.columns.committed')}</dt>
                    <dd className="text-foreground">{committed ?? t('delivery.portfolio.noDate')}</dd>
                  </div>
                  <div>
                    <dt className="text-xs text-foreground-muted">{t('delivery.portfolio.columns.forecast')}</dt>
                    <dd className="text-foreground">{forecast ?? t('delivery.portfolio.noDate')}</dd>
                  </div>
                </dl>

                {d.attentionSignals.length > 0 && (
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant="warning">
                      {t('delivery.portfolio.signalsCount', { count: d.attentionSignals.length })}
                    </Badge>
                    <span className="text-xs text-foreground-muted">
                      {attentionSignalLabel(d.attentionSignals[0]!.code, t)}
                    </span>
                  </div>
                )}

                <SourceDisclosure
                  source={t('delivery.sources.portfolioSource')}
                  updatedAt={d.lastActivityAt}
                  calculation={t('delivery.sources.portfolioCalculation')}
                  confidence={
                    d.forecastConfidence
                      ? t(`delivery.confidence.${d.forecastConfidence}`)
                      : t('delivery.sources.unavailable')
                  }
                  missing={missing}
                  technical={d.attentionSignals.map((signal) => `${signal.code}: ${signal.detail}`).join(' · ')}
                />

                <div className="flex flex-wrap items-center justify-between gap-2">
                  {(!owner || !d.committedDate) && (
                    <div className="flex flex-wrap items-center gap-2">
                      <p className="text-xs text-foreground-muted">
                        {t('delivery.portfolio.configureHint')}
                      </p>
                      <Button size="sm" variant="ghost" onClick={() => onConfigure(d)}>
                        {t('delivery.portfolio.configure')}
                      </Button>
                    </div>
                  )}
                  <Button size="sm" variant="outline" onClick={() => onOpen(d.deliveryId)}>
                    {t('delivery.portfolio.openAction')}
                  </Button>
                </div>
              </CardContent>
            </Card>
          </li>
        );
      })}
    </ul>
  );
}
