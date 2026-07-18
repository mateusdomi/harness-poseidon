import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { PackageOpen } from 'lucide-react';

import { Button, Card, CardContent, Skeleton } from '@/design-system';
import {
  CatalogTabs,
} from '@/features/tools/components/catalog-tabs';
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

/**
 * Catálogo de ferramentas, skills, plugins e servidores MCP em abas
 * (Select no mobile). Cada item permite habilitar/desabilitar com
 * confirmação; `tool.statusChanged` no stream global atualiza a lista.
 */
export default function UtoolsPage() {
  const { t } = useTranslation();
  const { tools, skills, plugins, mcpServers, isPending, isError, refetch } = useToolsCatalog();
  useToolsRealtime();

  const [activeTab, setActiveTab] = useState<CatalogTabId>('tools');

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

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.tools.title')}</h1>
        <p className="text-sm text-foreground-muted">{t('features.tools.description')}</p>
      </div>

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
            {lists[activeTab] === 0 ? (
              <Card>
                <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
                  <PackageOpen aria-hidden="true" className="size-8 text-foreground-muted" />
                  <h2 className="font-heading text-lg font-semibold">
                    {t('tools.empty.title')}
                  </h2>
                  <p className="text-sm text-foreground-muted">{t('tools.empty.body')}</p>
                </CardContent>
              </Card>
            ) : (
              <ul className="flex flex-col gap-3" aria-label={t(`tools.list.${activeTab}`)}>
                {activeTab === 'tools' &&
                  sortByName(tools).map((tool) => (
                    <ToolCard key={tool.id} tool={tool} plugins={plugins} />
                  ))}
                {activeTab === 'skills' &&
                  sortByName(skills).map((skill) => <SkillCard key={skill.id} skill={skill} />)}
                {activeTab === 'plugins' &&
                  sortByName(plugins).map((plugin) => (
                    <PluginCard key={plugin.id} plugin={plugin} tools={tools} />
                  ))}
                {activeTab === 'mcp' &&
                  sortByName(mcpServers).map((server) => (
                    <McpServerCard key={server.id} server={server} />
                  ))}
              </ul>
            )}
          </div>
        </>
      )}
    </div>
  );
}
