import { useTranslation } from 'react-i18next';

import type { Plugin, Tool } from '@/api';
import { maskSecrets } from '@/lib/secrets';
import { ComponentStateBadge } from '@/features/tools/components/component-state-badge';
import { ComponentToggle } from '@/features/tools/components/component-toggle';
import { providedToolNames } from '@/features/tools/lib/tools-derive';

export interface PluginCardProps {
  plugin: Plugin;
  /** Ferramentas do catálogo (resolve `providesToolIds` em nomes). */
  tools: Tool[];
}

/**
 * Plugin do catálogo: nome, descrição (sempre mascarada), estado, versão
 * e ferramentas providas (nomes resolvidos via `providesToolIds`).
 */
export function PluginCard({ plugin, tools }: PluginCardProps) {
  const { t } = useTranslation();
  const provided = providedToolNames(plugin, tools);
  return (
    <li className="flex flex-col gap-2 rounded-xl border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="font-medium">{plugin.name}</h3>
        <ComponentStateBadge state={plugin.state} />
        <span className="text-xs text-foreground-muted">
          {t('tools.fields.version', { version: plugin.version })}
        </span>
        <div className="ml-auto">
          <ComponentToggle
            resource="plugins"
            id={plugin.id}
            name={plugin.name}
            state={plugin.state}
          />
        </div>
      </div>
      <p className="text-sm text-foreground-muted">{maskSecrets(plugin.description)}</p>
      <p className="text-xs text-foreground-muted">
        {t('tools.fields.provides')}:{' '}
        {provided.length > 0 ? provided.join(', ') : t('tools.fields.providesEmpty')}
      </p>
    </li>
  );
}
