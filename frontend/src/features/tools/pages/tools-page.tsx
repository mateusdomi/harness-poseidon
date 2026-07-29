import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ListPlus, PackageOpen, Wrench } from 'lucide-react';

import { Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { usePresentationMode } from '@/app/presentation';
import { FeatureIntro } from '@/features/shared/components/feature-intro';
import { CatalogTabs } from '@/features/tools/components/catalog-tabs';
import { McpServerCard } from '@/features/tools/components/mcp-server-card';
import { PluginCard } from '@/features/tools/components/plugin-card';
import { SkillCard } from '@/features/tools/components/skill-card';
import { ToolCard } from '@/features/tools/components/tool-card';
import { useToolsCatalog, useToolsRealtime } from '@/features/tools/hooks/use-tools';
import {
  catalogPanelId,
  catalogTabId,
  sortByName,
  type CatalogTabId,
} from '@/features/tools/lib/tools-derive';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';

/**
 * Catálogo de ferramentas, skills, plugins e servidores MCP em abas
 * (Select no mobile). Cada item permite habilitar/desabilitar com
 * confirmação; `tool.statusChanged` no stream global atualiza a lista.
 */
export default function UtoolsPage() {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  if (!showTechnicalDetails) {
    return (
      <Card>
        <CardContent className="flex flex-col gap-2 p-6">
          <h1 className="font-heading text-xl font-semibold">{t('tools.access.title')}</h1>
          <p className="text-sm text-foreground-muted">{t('tools.access.body')}</p>
        </CardContent>
      </Card>
    );
  }
  return <ToolsTechnicalPage />;
}

function ToolsTechnicalPage() {
  const { t } = useTranslation();
  const { tools, skills, plugins, mcpServers, isPending, isError, refetch } = useToolsCatalog();
  useToolsRealtime();

  const [activeTab, setActiveTab] = useState<CatalogTabId>('tools');
  const registrationSteps = [
    t('tools.registration.step1'),
    t('tools.registration.step2'),
    t('tools.registration.step3'),
  ];

  const tabs = [
    { id: 'tools' as const, label: t('tools.tabs.tools'), count: tools.length },
    { id: 'skills' as const, label: t('tools.tabs.skills'), count: skills.length },
    { id: 'plugins' as const, label: t('tools.tabs.plugins'), count: plugins.length },
    { id: 'mcp' as const, label: t('tools.tabs.mcp'), count: mcpServers.length },
  ];

  const lists: Record<CatalogTabId, number> = {
    tools: tools.length,
    skills: skills.length,
    plugins: plugins.length,
    mcp: mcpServers.length,
  };

  // Paginação por aba: volta para a página 1 ao trocar de aba.
  const pagination = usePagination(lists[activeTab], { resetKey: activeTab });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.tools.title')}</h1>
        <p className="text-sm text-foreground-muted">{t('features.tools.description')}</p>
      </div>

      <FeatureIntro icon={Wrench} title={t('tools.intro.title')} note={t('tools.intro.note')}>
        {t('tools.intro.body')}
      </FeatureIntro>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <ListPlus aria-hidden="true" className="size-5" />
            {t('tools.registration.title')}
          </CardTitle>
          <p className="text-sm text-foreground-muted">{t('tools.registration.intro')}</p>
        </CardHeader>
        <CardContent>
          <ol className="grid gap-3 md:grid-cols-3">
            {registrationSteps.map((description, index) => (
              <li key={description} className="rounded-lg border border-border p-3 text-sm">
                <span className="mb-1 block font-semibold">
                  {t('tools.registration.stepLabel', { step: index + 1 })}
                </span>
                {description}
              </li>
            ))}
          </ol>
          <p className="mt-3 text-xs text-foreground-muted">{t('tools.registration.note')}</p>
        </CardContent>
      </Card>

      {isPending ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-11 w-full" />
          {Array.from({ length: 3 }, (_, index) => (
            <Skeleton key={index} className="h-28 w-full" />
          ))}
        </div>
      ) : isError ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={refetch}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <>
          <CatalogTabs tabs={tabs} activeId={activeTab} onChange={setActiveTab} />

          <div
            role="tabpanel"
            id={catalogPanelId(activeTab)}
            aria-labelledby={catalogTabId(activeTab)}
          >
            <p className="mb-3 text-sm text-foreground-muted">
              {t(`tools.tabs.hint.${activeTab}`)}
            </p>
            {lists[activeTab] === 0 ? (
              <Card>
                <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
                  <PackageOpen aria-hidden="true" className="size-8 text-foreground-muted" />
                  <h2 className="font-heading text-lg font-semibold">{t('tools.empty.title')}</h2>
                  <p className="text-sm text-foreground-muted">{t('tools.empty.body')}</p>
                </CardContent>
              </Card>
            ) : (
              <>
                <ul className="flex flex-col gap-3" aria-label={t(`tools.list.${activeTab}`)}>
                  {activeTab === 'tools' &&
                    pagination
                      .paginate(sortByName(tools))
                      .map((tool) => <ToolCard key={tool.id} tool={tool} plugins={plugins} />)}
                  {activeTab === 'skills' &&
                    pagination
                      .paginate(sortByName(skills))
                      .map((skill) => <SkillCard key={skill.id} skill={skill} />)}
                  {activeTab === 'plugins' &&
                    pagination
                      .paginate(sortByName(plugins))
                      .map((plugin) => (
                        <PluginCard key={plugin.id} plugin={plugin} tools={tools} />
                      ))}
                  {activeTab === 'mcp' &&
                    pagination
                      .paginate(sortByName(mcpServers))
                      .map((server) => <McpServerCard key={server.id} server={server} />)}
                </ul>
                <PaginationBar pagination={pagination} className="mt-3" />
              </>
            )}
          </div>
        </>
      )}
    </div>
  );
}
