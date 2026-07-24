import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Card, CardContent, Field, Select, Skeleton } from '@/design-system';

import { DeliveryApiProvider } from '../api/delivery-provider';
import { usePortfolio } from '../hooks/use-delivery';
import { PortfolioList } from '../components/portfolio-list';
import { DeliveryOverview } from '../components/delivery-overview';
import { ReportsCenter } from '../components/reports-center';
import { DailyCopilot } from '../components/daily-copilot';

type Tab = 'overview' | 'reports' | 'daily';
const TABS: Tab[] = ['overview', 'reports', 'daily'];

/**
 * Central de Entregas (DEL-01..10). Uma única área com duas visões: o
 * Portfólio de Entregas e, ao abrir uma entrega, as abas Entrega 360,
 * Central de Relatórios e Daily Copilot. Consome `/api/v1/deliveries/*`.
 */
export function DeliveryCenter() {
  const { t } = useTranslation();
  const [view, setView] = useState('all');
  const [selected, setSelected] = useState<{ id: string; name: string } | null>(null);
  const [tab, setTab] = useState<Tab>('overview');

  const portfolioQuery = usePortfolio(view);

  function openDelivery(deliveryId: string) {
    const summary = portfolioQuery.data?.deliveries.find((d) => d.deliveryId === deliveryId);
    setSelected({ id: deliveryId, name: summary?.name ?? deliveryId });
    setTab('overview');
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold text-foreground">{t('delivery.title')}</h1>
        <p className="text-sm text-foreground-muted">{t('delivery.subtitle')}</p>
      </header>

      {selected === null ? (
        <>
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h2 className="text-lg font-semibold text-foreground">{t('delivery.portfolio.title')}</h2>
              <p className="text-sm text-foreground-muted">{t('delivery.portfolio.subtitle')}</p>
            </div>
            <Field htmlFor="portfolio-view" label={t('delivery.portfolio.viewLabel')} className="w-64">
              <Select id="portfolio-view" value={view} onChange={(e) => setView(e.target.value)}>
                <option value="all">{t('delivery.portfolio.views.all')}</option>
                <option value="attention">{t('delivery.portfolio.views.attention')}</option>
              </Select>
            </Field>
          </div>

          {portfolioQuery.isLoading ? (
            <Skeleton className="h-64 w-full" />
          ) : portfolioQuery.isError || !portfolioQuery.data ? (
            <Card>
              <CardContent className="py-8 text-center text-sm text-error">{t('delivery.error')}</CardContent>
            </Card>
          ) : (
            <>
              <p className="text-sm text-foreground-muted" data-testid="portfolio-count">
                {t('delivery.portfolio.count', { count: portfolioQuery.data.total })}
              </p>
              <PortfolioList deliveries={portfolioQuery.data.deliveries} onOpen={openDelivery} />
            </>
          )}
        </>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center gap-3">
            <Button variant="ghost" size="sm" onClick={() => setSelected(null)}>
              {t('delivery.back')}
            </Button>
            <h2 className="text-lg font-semibold text-foreground">{selected.name}</h2>
          </div>

          <div role="tablist" aria-label={selected.name} className="flex flex-wrap gap-2 border-b border-border-strong">
            {TABS.map((current) => (
              <button
                key={current}
                type="button"
                role="tab"
                aria-selected={tab === current}
                className={
                  tab === current
                    ? 'border-b-2 border-primary px-3 py-2 text-sm font-medium text-foreground'
                    : 'border-b-2 border-transparent px-3 py-2 text-sm text-foreground-muted hover:text-foreground'
                }
                onClick={() => setTab(current)}
              >
                {t(`delivery.tabs.${current}`)}
              </button>
            ))}
          </div>

          {tab === 'overview' && <DeliveryOverview deliveryId={selected.id} />}
          {tab === 'reports' && <ReportsCenter deliveryId={selected.id} />}
          {tab === 'daily' && <DailyCopilot deliveryId={selected.id} />}
        </div>
      )}
    </div>
  );
}

export default function DeliveryPage() {
  return (
    <DeliveryApiProvider>
      <DeliveryCenter />
    </DeliveryApiProvider>
  );
}
