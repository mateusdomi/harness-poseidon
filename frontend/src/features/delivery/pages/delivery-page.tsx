import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Card, CardContent, Field, Select, Skeleton } from '@/design-system';

import { DeliveryApiProvider } from '../api/delivery-provider';
import { useDeliveryAgentDirectory, useDeliveryRealtime, usePortfolio } from '../hooks/use-delivery';
import { PortfolioList } from '../components/portfolio-list';
import { DeliveryOverview } from '../components/delivery-overview';
import { ReportsCenter } from '../components/reports-center';
import { DailyCopilot } from '../components/daily-copilot';
import { DeliveryPlanningDialog } from '../components/delivery-planning-dialog';
import type { DeliverySummary } from '../api/types';

type Tab = 'overview' | 'reports' | 'daily';
const TABS: Tab[] = ['overview', 'reports', 'daily'];

/**
 * Central de Entregas (DEL-01..10). Uma única área com duas visões: o
 * Portfólio de Entregas e, ao abrir uma entrega, as abas Entrega 360,
 * Central de Relatórios e Daily Copilot. Consome `/api/v1/deliveries/*`.
 */
export function DeliveryCenter() {
  const { t } = useTranslation();
  // Contrato do backend: `GET /api/v1/deliveries?view=portfolio|attention`
  // (`portfolio` = todas as entregas). "all" não é um valor aceito (400).
  const [view, setView] = useState('portfolio');
  const [selected, setSelected] = useState<{ id: string; name: string } | null>(null);
  const [tab, setTab] = useState<Tab>('overview');
  const [planningDelivery, setPlanningDelivery] = useState<DeliverySummary | null>(null);

  const portfolioQuery = usePortfolio(view);
  const agentsQuery = useDeliveryAgentDirectory();
  const agentNames = useMemo(
    () => new Map((agentsQuery.data?.items ?? []).map((agent) => [agent.id, agent.name])),
    [agentsQuery.data],
  );

  // Tempo real: assina os projetos das entregas visíveis; qualquer evento que
  // mexa na agregação (tarefa/gate/aprovação/decisão) invalida a Central.
  const projectIds = useMemo(
    () => (portfolioQuery.data?.deliveries ?? []).map((d) => d.projectId),
    [portfolioQuery.data],
  );
  useDeliveryRealtime(projectIds);

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
                <option value="portfolio">{t('delivery.portfolio.views.all')}</option>
                <option value="attention">{t('delivery.portfolio.views.attention')}</option>
              </Select>
            </Field>
          </div>

          {portfolioQuery.isLoading ? (
            <Skeleton className="h-64 w-full" />
          ) : portfolioQuery.isError || !portfolioQuery.data ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-8 text-center text-sm text-error">
                <span>{t('delivery.error')}</span>
                <Button variant="outline" size="sm" onClick={() => void portfolioQuery.refetch()}>
                  {t('delivery.retry')}
                </Button>
              </CardContent>
            </Card>
          ) : (
            <>
              <p className="text-sm text-foreground-muted" data-testid="portfolio-count">
                {t('delivery.portfolio.count', { count: portfolioQuery.data.total })}
              </p>
              <PortfolioList
                deliveries={portfolioQuery.data.deliveries}
                onOpen={openDelivery}
                onConfigure={setPlanningDelivery}
                agentNames={agentNames}
              />
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
      {planningDelivery && (
        <DeliveryPlanningDialog
          delivery={planningDelivery}
          agents={agentsQuery.data?.items ?? []}
          onClose={() => setPlanningDelivery(null)}
          onSaved={() => {
            setPlanningDelivery(null);
            void portfolioQuery.refetch();
          }}
        />
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
