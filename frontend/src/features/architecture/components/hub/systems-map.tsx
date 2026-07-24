import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent, CardHeader, CardTitle, Field } from '@/design-system';

import {
  useCapabilityMap,
  useDomainMap,
  useHeatmap,
  useIntegrationGraph,
  useSystemCatalog,
} from '../../hooks/use-architecture-hub';
import { HubError, HubLoading } from './hub-states';

type CriticalityVariant = 'error' | 'warning' | 'info' | 'outline';

function criticalityVariant(value: string): CriticalityVariant {
  if (value === 'high') return 'error';
  if (value === 'medium') return 'warning';
  if (value === 'low') return 'info';
  return 'outline';
}

function heatVariant(score: number): 'error' | 'warning' | 'success' {
  if (score >= 70) return 'error';
  if (score >= 35) return 'warning';
  return 'success';
}

/**
 * ARC-02 — Mapa Corporativo de Sistemas. Catálogo pesquisável de sistemas
 * (criticidade, domínio, dono, calor) com lentes por domínio, capacidade,
 * integrações e heatmap de risco. Selecionar um sistema abre o Sistema 360.
 */
export function SystemsMap({ projectId, onSelectSystem }: {
  projectId: string | null;
  onSelectSystem: (systemId: string) => void;
}) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [lens, setLens] = useState<'catalog' | 'domains' | 'capabilities' | 'integration' | 'heatmap'>(
    'catalog',
  );

  const catalog = useSystemCatalog(projectId);
  const domains = useDomainMap(projectId);
  const capabilities = useCapabilityMap(projectId);
  const integration = useIntegrationGraph(projectId);
  const heatmap = useHeatmap(projectId);

  const systems = useMemo(() => catalog.data?.systems ?? [], [catalog.data]);
  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return systems;
    return systems.filter(
      (s) =>
        s.name.toLowerCase().includes(q) ||
        (s.domain ?? '').toLowerCase().includes(q) ||
        s.capabilities.some((c) => c.toLowerCase().includes(q)),
    );
  }, [systems, search]);

  if (catalog.isLoading) return <HubLoading />;
  if (catalog.isError) return <HubError onRetry={() => void catalog.refetch()} />;

  const lenses = ['catalog', 'domains', 'capabilities', 'integration', 'heatmap'] as const;

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div
          role="tablist"
          aria-label={t('architecture.hub.systems.lensLabel')}
          className="flex flex-wrap gap-1"
        >
          {lenses.map((item) => (
            <button
              key={item}
              type="button"
              role="tab"
              aria-selected={lens === item}
              onClick={() => setLens(item)}
              className={
                lens === item
                  ? 'min-h-touch rounded-md border border-brand px-3 py-2 text-sm font-medium text-brand-strong'
                  : 'min-h-touch rounded-md border border-border px-3 py-2 text-sm text-foreground-muted hover:text-foreground'
              }
            >
              {t(`architecture.hub.systems.lens.${item}`)}
            </button>
          ))}
        </div>
        {lens === 'catalog' ? (
          <Field htmlFor="systems-search" label={t('architecture.hub.systems.search')} className="md:w-72">
            <input
              id="systems-search"
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder={t('architecture.hub.systems.searchPlaceholder')}
              className="min-h-touch w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-foreground"
            />
          </Field>
        ) : null}
      </div>

      {lens === 'catalog' ? (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" data-testid="systems-catalog">
          {filtered.map((system) => (
            <button
              key={system.id}
              type="button"
              onClick={() => onSelectSystem(system.id)}
              className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4 text-left transition-colors hover:border-brand"
            >
              <div className="flex items-start justify-between gap-2">
                <span className="font-medium text-foreground">{system.name}</span>
                <Badge variant={heatVariant(system.heatScore)}>
                  {t('architecture.hub.systems.heat', { score: system.heatScore })}
                </Badge>
              </div>
              <p className="line-clamp-2 text-xs text-foreground-muted">{system.description}</p>
              <div className="flex flex-wrap gap-1.5">
                <Badge variant={criticalityVariant(system.criticality)}>
                  {t(`architecture.hub.criticality.${system.criticality}`, {
                    defaultValue: system.criticality,
                  })}
                </Badge>
                {system.domain ? <Badge variant="outline">{system.domain}</Badge> : null}
                {system.lifecycleStatus ? (
                  <Badge variant="outline">
                    {t(`architecture.hub.lifecycle.${system.lifecycleStatus}`, {
                      defaultValue: system.lifecycleStatus,
                    })}
                  </Badge>
                ) : null}
              </div>
            </button>
          ))}
          {filtered.length === 0 ? (
            <p className="text-sm text-foreground-muted">{t('architecture.hub.systems.empty')}</p>
          ) : null}
        </div>
      ) : null}

      {lens === 'domains' ? (
        <Card>
          <CardHeader>
            <CardTitle>{t('architecture.hub.systems.lens.domains')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-2">
            {(domains.data?.domains ?? []).map((node) => (
              <div key={node.domain} className="flex items-center justify-between gap-2 border-b border-border py-2 last:border-0">
                <span className="text-sm text-foreground">{node.domain}</span>
                <Badge variant="info">
                  {t('architecture.hub.systems.systemCount', { count: node.systemCount })}
                </Badge>
              </div>
            ))}
          </CardContent>
        </Card>
      ) : null}

      {lens === 'capabilities' ? (
        <Card>
          <CardHeader>
            <CardTitle>{t('architecture.hub.systems.lens.capabilities')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-2">
            {(capabilities.data?.capabilities ?? []).map((node) => (
              <div key={node.capability} className="flex items-center justify-between gap-2 border-b border-border py-2 last:border-0">
                <span className="text-sm text-foreground">{node.capability}</span>
                <Badge variant={node.systemCount > 1 ? 'warning' : 'info'}>
                  {t('architecture.hub.systems.systemCount', { count: node.systemCount })}
                </Badge>
              </div>
            ))}
          </CardContent>
        </Card>
      ) : null}

      {lens === 'integration' ? (
        <Card>
          <CardHeader>
            <CardTitle>{t('architecture.hub.systems.lens.integration')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-2">
            <p className="text-xs text-foreground-muted">
              {t('architecture.hub.systems.integrationSummary', {
                systems: integration.data?.systemCount ?? 0,
                edges: integration.data?.edgeCount ?? 0,
              })}
            </p>
            {(integration.data?.edges ?? []).map((edge) => (
              <div key={`${edge.sourceId}-${edge.targetId}-${edge.kind}`} className="flex flex-wrap items-center gap-2 border-b border-border py-2 text-sm last:border-0">
                <span className="font-medium text-foreground">{edge.sourceName}</span>
                <Badge variant="outline">{edge.kind}</Badge>
                <span aria-hidden="true" className="text-foreground-muted">→</span>
                <span className="font-medium text-foreground">{edge.targetName}</span>
              </div>
            ))}
          </CardContent>
        </Card>
      ) : null}

      {lens === 'heatmap' ? (
        <Card>
          <CardHeader>
            <CardTitle>{t('architecture.hub.systems.lens.heatmap')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3" data-testid="systems-heatmap">
            {(heatmap.data?.systems ?? []).map((system) => (
              <button
                key={system.id}
                type="button"
                onClick={() => onSelectSystem(system.id)}
                className="flex flex-col gap-2 rounded-md border border-border p-3 text-left hover:border-brand"
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium text-foreground">{system.name}</span>
                  <Badge variant={heatVariant(system.score)}>
                    {t('architecture.hub.systems.heat', { score: system.score })}
                  </Badge>
                </div>
                <div className="flex flex-wrap gap-1.5">
                  {system.signals.map((signal) => (
                    <Badge key={signal.code} variant="warning" title={signal.detail}>
                      {t(`architecture.hub.heatSignal.${signal.code}`, { defaultValue: signal.code })}
                    </Badge>
                  ))}
                </div>
              </button>
            ))}
            {(heatmap.data?.systems.length ?? 0) === 0 ? (
              <p className="text-sm text-foreground-muted">{t('architecture.hub.systems.heatmapEmpty')}</p>
            ) : null}
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}
