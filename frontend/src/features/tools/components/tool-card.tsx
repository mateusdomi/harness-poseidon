import { useTranslation } from 'react-i18next';

import type { Plugin, Tool } from '@/api';
import { Badge } from '@/design-system';
import { maskSecrets } from '@/lib/secrets';
import { ComponentStateBadge } from '@/features/tools/components/component-state-badge';
import { ComponentToggle } from '@/features/tools/components/component-toggle';
import { toolOrigin } from '@/features/tools/lib/tools-derive';

export interface ToolCardProps {
  tool: Tool;
  /** Plugins do catálogo (origem das ferramentas `plugin`). */
  plugins: Plugin[];
}

/**
 * Ferramenta do catálogo: nome, descrição (sempre mascarada), estado,
 * kind e origem — interna, plugin que a provê (via `providesToolIds`)
 * ou servidor MCP. O contrato não liga ferramenta ↔ servidor MCP nem
 * traz checksum/permissões: esses vínculos aparecem como indisponíveis.
 */
export function ToolCard({ tool, plugins }: ToolCardProps) {
  const { t } = useTranslation();
  const origin = toolOrigin(tool, plugins);

  const originText =
    origin.type === 'builtin'
      ? t('tools.origin.builtin')
      : origin.type === 'mcp'
        ? `${t('tools.origin.mcp')} · ${t('tools.unavailable')}`
        : origin.plugin !== null
          ? t('tools.origin.plugin', { name: origin.plugin.name })
          : `${t('status.toolKind.plugin')} · ${t('tools.unavailable')}`;

  return (
    <li className="flex flex-col gap-2 rounded-xl border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="font-medium">{tool.name}</h3>
        <ComponentStateBadge state={tool.state} />
        <Badge variant="default">{t(`status.toolKind.${tool.kind}`)}</Badge>
        <div className="ml-auto">
          <ComponentToggle resource="tools" id={tool.id} name={tool.name} state={tool.state} />
        </div>
      </div>
      <p className="text-sm text-foreground-muted">{maskSecrets(tool.description)}</p>
      <p className="text-xs text-foreground-muted">
        {t('tools.origin.label')}: {originText}
      </p>
    </li>
  );
}
