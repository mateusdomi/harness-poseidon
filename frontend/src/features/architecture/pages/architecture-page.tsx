import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';

import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Field,
  Select,
  Skeleton,
} from '@/design-system';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

import { ArchitectureApiProvider } from '../api/architecture-provider';
import { SystemsMap } from '../components/hub/systems-map';
import { System360Panel } from '../components/hub/system-360';
import { DiscoveryPanel } from '../components/hub/discovery-panel';
import { InsightsPanel } from '../components/hub/insights-panel';
import { PatternsPanel } from '../components/hub/patterns-panel';
import { BaselinesPanel } from '../components/hub/baselines-panel';
import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';
import {
  useArchitectureElements,
  useArchitectureRelationships,
  useArchitectureView,
  useArchitectureViews,
  useCreateElement,
  useCreateView,
  useToggleElementLock,
} from '../hooks/use-architecture';
import { findPreset, projectModel } from '../model/classification';
import { ArchitectureCanvas } from '../components/architecture-canvas';
import { ElementInspector } from '../components/element-inspector';
import { ViewSelector } from '../components/view-selector';
import { AddElementForm } from '../components/add-element-form';

/**
 * ARC-04 — Architecture Studio. Canvas sobre o modelo estruturado
 * (elementos + propriedades + relacionamentos). As views C4 / ArchiMate /
 * extras são PROJEÇÕES do mesmo grafo (não imagens); views salvas vêm da
 * API. Consome `/api/v1/architecture/*`; não há backend novo.
 */
export function ArchitectureStudio() {
  const { t } = useTranslation();
  const {
    projects,
    activeProject,
    setActiveProject,
    isPending: projectsPending,
  } = useActiveProject();
  const projectId = activeProject?.id ?? null;

  const [presetId, setPresetId] = useState('model');
  const [savedViewId, setSavedViewId] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const elementsQuery = useArchitectureElements(projectId);
  const relationshipsQuery = useArchitectureRelationships(projectId);
  const viewsQuery = useArchitectureViews(projectId);
  const savedViewQuery = useArchitectureView(savedViewId);

  const lockMutation = useToggleElementLock(projectId ?? '');
  const createElementMutation = useCreateElement(projectId ?? '');
  const createViewMutation = useCreateView(projectId ?? '');

  const elements = useMemo(() => elementsQuery.data ?? [], [elementsQuery.data]);
  const relationships = useMemo(() => relationshipsQuery.data ?? [], [relationshipsQuery.data]);

  const projected = useMemo<{
    elements: ArchitectureElement[];
    relationships: ArchitectureRelationship[];
  }>(() => {
    if (savedViewId && savedViewQuery.data) {
      return {
        elements: savedViewQuery.data.elements,
        relationships: savedViewQuery.data.relationships,
      };
    }
    return projectModel(elements, relationships, presetId);
  }, [savedViewId, savedViewQuery.data, elements, relationships, presetId]);

  const selectedElement = elements.find((el) => el.id === selectedId) ?? null;
  const isLoading = elementsQuery.isLoading || relationshipsQuery.isLoading;

  function handleSaveView() {
    if (!projectId || projected.elements.length === 0) return;
    const preset = findPreset(presetId);
    const notation =
      preset.group === 'c4' ? 'c4' : preset.group === 'archimate' ? 'archimate' : presetId;
    createViewMutation.mutate({
      projectId,
      name: t('architecture.views.savedName', { view: t(`architecture.views.${presetId}`) }),
      description: '',
      notation,
      elementIds: projected.elements.map((el) => el.id),
      relationshipIds: projected.relationships.map((r) => r.id),
      filterKinds: [],
      filterTags: [],
    });
  }

  return (
    <div className="flex min-w-0 flex-col gap-6">
      <header className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-foreground">{t('architecture.title')}</h1>
          <p className="text-sm text-foreground-muted">{t('architecture.subtitle')}</p>
        </div>
        <Field
          htmlFor="architecture-project"
          label={t('architecture.projectLabel')}
          className="md:w-72"
        >
          <Select
            id="architecture-project"
            value={activeProject?.id ?? ''}
            disabled={projectsPending || projects.length === 0}
            onChange={(event) => {
              setActiveProject(event.target.value);
              setSelectedId(null);
              setSavedViewId(null);
            }}
          >
            {projects.map((project) => (
              <option key={project.id} value={project.id}>
                {project.name}
              </option>
            ))}
          </Select>
        </Field>
      </header>

      <div className="grid min-w-0 gap-6 lg:grid-cols-[280px_minmax(0,1fr)_320px]">
        <div className="flex min-w-0 flex-col gap-6">
          <Card>
            <CardHeader>
              <CardTitle>{t('architecture.selector.title')}</CardTitle>
            </CardHeader>
            <CardContent>
              <ViewSelector
                presetId={presetId}
                onPresetChange={setPresetId}
                savedViews={viewsQuery.data ?? []}
                activeSavedViewId={savedViewId}
                onSelectSavedView={(id) => {
                  setSavedViewId(id);
                  setSelectedId(null);
                }}
                onClearSavedView={() => setSavedViewId(null)}
              />
              <div className="mt-4 border-t border-border-strong pt-4">
                <Button
                  variant="outline"
                  size="sm"
                  onClick={handleSaveView}
                  disabled={
                    !projectId ||
                    projected.elements.length === 0 ||
                    savedViewId !== null ||
                    createViewMutation.isPending
                  }
                >
                  {t('architecture.selector.saveView')}
                </Button>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t('architecture.add.title')}</CardTitle>
            </CardHeader>
            <CardContent>
              {projectId ? (
                <AddElementForm
                  projectId={projectId}
                  pending={createElementMutation.isPending}
                  onSubmit={(input) => createElementMutation.mutate(input)}
                />
              ) : (
                <p className="text-xs text-foreground-muted">{t('architecture.noProject')}</p>
              )}
            </CardContent>
          </Card>
        </div>

        <div className="flex min-w-0 flex-col gap-2">
          <p className="text-sm text-foreground-muted" data-testid="architecture-view-summary">
            {t('architecture.canvas.summary', {
              elements: projected.elements.length,
              relationships: projected.relationships.length,
            })}
          </p>
          {isLoading ? (
            <Skeleton className="h-96 w-full" />
          ) : (
            <ArchitectureCanvas
              elements={projected.elements}
              relationships={projected.relationships}
              selectedId={selectedId}
              onSelect={setSelectedId}
            />
          )}
        </div>

        <div className="min-w-0">
          {selectedElement ? (
            <ElementInspector
              element={selectedElement}
              elements={elements}
              relationships={relationships}
              lockPending={lockMutation.isPending}
              onToggleLock={(locked) =>
                lockMutation.mutate({ id: selectedElement.id, input: { locked } })
              }
            />
          ) : (
            <Card>
              <CardContent className="py-8 text-center text-sm text-foreground-muted">
                {t('architecture.inspector.empty')}
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}

/* ================================================================== */
/* Architecture Hub — casca de abas sobre o Studio + superfícies       */
/* corporativas (ARC-02/03/06/07/08/10).                               */
/* ================================================================== */

const HUB_TABS = ['studio', 'systems', 'discovery', 'insights', 'patterns', 'baselines'] as const;
type HubTab = (typeof HUB_TABS)[number];

function isHubTab(value: string | null): value is HubTab {
  return value !== null && (HUB_TABS as readonly string[]).includes(value);
}

/**
 * Architecture Hub. Reúne, em abas, o Studio (ARC-04, canvas do modelo) e as
 * superfícies corporativas do backend: Mapa de Sistemas + heatmaps (ARC-02),
 * Sistema 360 (ARC-03, drill-in do mapa), Discovery (ARC-06), Insights &
 * Racionalização (ARC-07), Padrões/ADRs (ARC-08) e Baselines/Conformidade
 * (ARC-10). Estado de aba/sistema fica na URL (`?tab=…&system=…`) para
 * deep-link. Tudo consome `/api/v1/architecture/*` — não há backend novo.
 */
export function ArchitectureHub() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { activeProject } = useActiveProject();
  const projectId = activeProject?.id ?? null;

  const tabParam = searchParams.get('tab');
  const activeTab: HubTab = isHubTab(tabParam) ? tabParam : 'studio';
  const selectedSystem = searchParams.get('system');

  function selectTab(tab: HubTab) {
    const next = new URLSearchParams(searchParams);
    if (tab === 'studio') next.delete('tab');
    else next.set('tab', tab);
    next.delete('system');
    setSearchParams(next);
  }

  function selectSystem(systemId: string | null) {
    const next = new URLSearchParams(searchParams);
    next.set('tab', 'systems');
    if (systemId) next.set('system', systemId);
    else next.delete('system');
    setSearchParams(next);
  }

  return (
    <div className="flex min-w-0 flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold text-foreground">{t('architecture.hub.title')}</h1>
        <p className="text-sm text-foreground-muted">{t('architecture.hub.subtitle')}</p>
      </div>

      <div
        role="tablist"
        aria-label={t('architecture.hub.tabsLabel')}
        className="flex flex-wrap gap-1"
      >
        {HUB_TABS.map((tab) => (
          <button
            key={tab}
            type="button"
            role="tab"
            id={`architecture-tab-${tab}`}
            aria-selected={activeTab === tab}
            onClick={() => selectTab(tab)}
            className={
              activeTab === tab
                ? 'min-h-touch rounded-md border border-brand px-3 py-2 text-sm font-medium text-brand-strong'
                : 'min-h-touch rounded-md border border-border px-3 py-2 text-sm text-foreground-muted hover:text-foreground'
            }
          >
            {t(`architecture.hub.tabs.${tab}`)}
          </button>
        ))}
      </div>

      <div role="tabpanel" aria-labelledby={`architecture-tab-${activeTab}`} className="min-w-0">
        {activeTab === 'studio' ? <ArchitectureStudio /> : null}
        {activeTab === 'systems' ? (
          selectedSystem ? (
            <System360Panel systemId={selectedSystem} onBack={() => selectSystem(null)} />
          ) : (
            <SystemsMap projectId={projectId} onSelectSystem={(id) => selectSystem(id)} />
          )
        ) : null}
        {activeTab === 'discovery' ? <DiscoveryPanel projectId={projectId} /> : null}
        {activeTab === 'insights' ? <InsightsPanel projectId={projectId} /> : null}
        {activeTab === 'patterns' ? <PatternsPanel projectId={projectId} /> : null}
        {activeTab === 'baselines' ? <BaselinesPanel projectId={projectId} /> : null}
      </div>
    </div>
  );
}

export default function ArchitecturePage() {
  return (
    <ArchitectureApiProvider>
      <ArchitectureHub />
    </ArchitectureApiProvider>
  );
}
