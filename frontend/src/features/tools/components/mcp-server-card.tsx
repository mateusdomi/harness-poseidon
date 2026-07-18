import { useTranslation } from 'react-i18next';

import type { McpServer } from '@/api';
import { Badge } from '@/design-system';
import { maskEndpoint } from '@/lib/secrets';
import { ComponentStateBadge } from '@/features/tools/components/component-state-badge';
import { ComponentToggle } from '@/features/tools/components/component-toggle';

/**
 * Servidor MCP do catálogo: nome, estado, transport, contagem de
 * ferramentas e endpoint SEMPRE mascarado (credenciais em URL/comando
 * nunca aparecem — regra inegociável de segredos).
 */
export function McpServerCard({ server }: { server: McpServer }) {
  const { t } = useTranslation();
  return (
    <li className="flex flex-col gap-2 rounded-xl border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="font-medium">{server.name}</h3>
        <ComponentStateBadge state={server.state} />
        <Badge variant="default">{t(`status.mcpTransport.${server.transport}`)}</Badge>
        <div className="ml-auto">
          <ComponentToggle
            resource="mcp-servers"
            id={server.id}
            name={server.name}
            state={server.state}
          />
        </div>
      </div>
      <p className="text-xs text-foreground-muted">
        {t('tools.fields.toolCount', { count: server.toolCount })}
      </p>
      <p className="text-xs text-foreground-muted">
        {t('tools.fields.endpoint')}:{' '}
        <code className="rounded bg-surface-elevated px-1 py-0.5 break-all">
          {maskEndpoint(server.endpoint)}
        </code>
      </p>
      <p className="text-xs text-foreground-muted">{t('common.secrets.maskedNote')}</p>
    </li>
  );
}
