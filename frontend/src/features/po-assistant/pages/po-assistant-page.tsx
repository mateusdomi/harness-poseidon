import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Send, Sparkles, X } from 'lucide-react';

import type { SolicitationAnalysis } from '@/api';
import { Button, Card, CardContent, Select, Skeleton, Textarea } from '@/design-system';
import { FeatureIntro } from '@/features/shared/components/feature-intro';
import { AnalysisPanelCard } from '@/features/po-assistant/components/analysis-panel-card';
import {
  useAnalyzeSolicitation,
  useCreateStructuredDemand,
} from '@/features/po-assistant/hooks/use-po-assistant';
import {
  buildDemandFromPanels,
  toCuratedPanels,
  type AnalysisPanel,
  type CuratedItem,
} from '@/features/po-assistant/lib/po-assistant-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * PO Assistant (versão reduzida): texto livre + anexos → análise em painéis
 * (requisitos, ambiguidades, contradições, perguntas, critérios de aceite)
 * com curadoria humana; "criar demanda estruturada" envia ao chefe
 * (cria a demanda → demand.created → aparece no quadro).
 */
export default function UpoAssistantPage() {
  const { t } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

  const analyze = useAnalyzeSolicitation();
  const createDemand = useCreateStructuredDemand();

  const [text, setText] = useState('');
  const [attachments, setAttachments] = useState<string[]>([]);
  const [solicitationId, setSolicitationId] = useState<string | null>(null);
  const [panels, setPanels] = useState<AnalysisPanel[] | null>(null);
  const [createdDemand, setCreatedDemand] = useState<{ id: string; title: string } | null>(null);

  async function submit() {
    if (!projectId || text.trim() === '') return;
    const analysis: SolicitationAnalysis = await analyze.mutateAsync({
      projectId,
      text: text.trim(),
      attachmentNames: attachments.length > 0 ? attachments : undefined,
    });
    setSolicitationId(analysis.solicitationId);
    setPanels(toCuratedPanels(analysis));
    setCreatedDemand(null);
  }

  function updateItem(panelKey: AnalysisPanel['key'], updated: CuratedItem) {
    setPanels((current) =>
      current?.map((panel) =>
        panel.key === panelKey
          ? { ...panel, items: panel.items.map((item) => (item.id === updated.id ? updated : item)) }
          : panel,
      ) ?? null,
    );
  }

  function discardItem(panelKey: AnalysisPanel['key'], itemId: string) {
    setPanels((current) =>
      current?.map((panel) =>
        panel.key === panelKey
          ? { ...panel, items: panel.items.filter((item) => item.id !== itemId) }
          : panel,
      ) ?? null,
    );
  }

  async function createStructuredDemand() {
    if (!projectId || !panels || !solicitationId) return;
    const { title, description } = buildDemandFromPanels({
      panels,
      fallbackTitle: t('poAssistant.demand.fallbackTitle'),
      panelTitles: {
        requirements: t('poAssistant.panels.requirements'),
        ambiguities: t('poAssistant.panels.ambiguities'),
        contradictions: t('poAssistant.panels.contradictions'),
        questions: t('poAssistant.panels.questions'),
        acceptanceCriteria: t('poAssistant.panels.acceptanceCriteria'),
      },
    });
    const demand = await createDemand.mutateAsync({
      projectId,
      solicitationId,
      title,
      description,
      priority: 'medium',
    });
    setCreatedDemand({ id: demand.id, title: demand.title });
  }

  const loading = isPending;
  const errored = isError;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">
          {t('features.po-assistant.title')}
        </h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="po-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="po-project"
              className="w-auto min-w-48"
              value={activeProject?.id ?? ''}
              onChange={(event) => setActiveProject(event.target.value)}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

      <FeatureIntro
        icon={Sparkles}
        title={t('poAssistant.intro.title')}
        steps={[
          t('poAssistant.intro.step1'),
          t('poAssistant.intro.step2'),
          t('poAssistant.intro.step3'),
          t('poAssistant.intro.step4'),
        ]}
        stepsLabel={t('poAssistant.intro.stepsLabel')}
        note={t('poAssistant.intro.note')}
      >
        {t('poAssistant.intro.body')}
      </FeatureIntro>

      {loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-40 w-full" />
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={refetch}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('poAssistant.noProject.body')}</p>
          </CardContent>
        </Card>
      ) : (
        <>
          <Card>
            <CardContent className="flex flex-col gap-3 p-4">
              <div className="flex flex-col gap-1">
                <label htmlFor="po-input" className="text-sm font-medium">
                  {t('poAssistant.input.label')}
                </label>
                <Textarea
                  id="po-input"
                  rows={5}
                  value={text}
                  placeholder={t('poAssistant.input.placeholder')}
                  onChange={(event) => setText(event.target.value)}
                />
              </div>
              <div className="flex flex-col gap-2">
                <label htmlFor="po-attachments" className="text-sm font-medium">
                  {t('poAssistant.input.attachments')}
                </label>
                <input
                  id="po-attachments"
                  type="file"
                  multiple
                  className="text-sm"
                  onChange={(event) =>
                    setAttachments((current) => [
                      ...current,
                      ...[...(event.target.files ?? [])].map((file) => file.name),
                    ])
                  }
                />
                {attachments.length > 0 && (
                  <ul className="flex flex-wrap gap-2">
                    {attachments.map((name, index) => (
                      <li key={`${name}-${index}`}>
                        <span className="inline-flex items-center gap-1 rounded-full border border-border-strong bg-surface-elevated px-2.5 py-0.5 text-xs">
                          {name}
                          <button
                            type="button"
                            className="inline-flex min-h-touch items-center"
                            aria-label={t('poAssistant.input.removeAttachment', { name })}
                            onClick={() =>
                              setAttachments((current) => current.filter((_, i) => i !== index))
                            }
                          >
                            <X aria-hidden="true" className="size-3" />
                          </button>
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
              <div>
                <Button
                  type="button"
                  disabled={text.trim() === '' || analyze.isPending}
                  onClick={() => void submit()}
                >
                  <Sparkles aria-hidden="true" />
                  {analyze.isPending ? t('poAssistant.input.analyzing') : t('poAssistant.input.analyze')}
                </Button>
              </div>
              {analyze.isError && (
                <p role="alert" className="text-sm text-error">
                  {t('poAssistant.input.error')}
                </p>
              )}
            </CardContent>
          </Card>

          {panels && (
            <>
              <div className="grid gap-3 lg:grid-cols-2">
                {panels.map((panel) => (
                  <AnalysisPanelCard
                    key={panel.key}
                    panel={panel}
                    onUpdateItem={updateItem}
                    onDiscardItem={discardItem}
                  />
                ))}
              </div>

              {createdDemand ? (
                <Card>
                  <CardContent className="flex flex-col items-start gap-3 p-4">
                    <p role="status" className="text-sm">
                      {t('poAssistant.demand.created')}
                    </p>
                    <p className="text-sm font-medium">{createdDemand.title}</p>
                    <div className="flex gap-2">
                      <Button asChild variant="outline" size="sm">
                        <Link to="/board">{t('poAssistant.demand.openBoard')}</Link>
                      </Button>
                      <Button asChild variant="ghost" size="sm">
                        <Link to="/chat">{t('poAssistant.demand.openChat')}</Link>
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              ) : (
                <div>
                  <Button
                    type="button"
                    disabled={createDemand.isPending}
                    onClick={() => void createStructuredDemand()}
                  >
                    <Send aria-hidden="true" />
                    {createDemand.isPending
                      ? t('poAssistant.demand.creating')
                      : t('poAssistant.demand.create')}
                  </Button>
                  {createDemand.isError && (
                    <p role="alert" className="mt-2 text-sm text-error">
                      {t('poAssistant.demand.error')}
                    </p>
                  )}
                </div>
              )}
            </>
          )}
        </>
      )}
    </div>
  );
}
