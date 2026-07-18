import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type ComponentState,
  type McpServer,
  type Plugin,
  type Skill,
  type Tool,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys do catálogo de ferramentas/skills/plugins/MCP. */
export const catalogKeys = {
  tools: ['catalog', 'tools'] as const,
  skills: ['catalog', 'skills'] as const,
  plugins: ['catalog', 'plugins'] as const,
  mcpServers: ['catalog', 'mcp-servers'] as const,
};

const CATALOG_PREFIX = ['catalog'] as const;

/** Recursos do catálogo com alternância de estado via `update`. */
export type CatalogResource = 'tools' | 'skills' | 'plugins' | 'mcp-servers';

/**
 * Catálogo consolidado: ferramentas + skills + plugins + servidores MCP
 * (plugins e servidores dão contexto de origem às ferramentas).
 */
export function useToolsCatalog() {
  const api = useApi();

  const toolsQuery = useQuery({
    queryKey: catalogKeys.tools,
    queryFn: async (): Promise<Tool[]> => (await api.list('tools')).items,
  });

  const skillsQuery = useQuery({
    queryKey: catalogKeys.skills,
    queryFn: async (): Promise<Skill[]> => (await api.list('skills')).items,
  });

  const pluginsQuery = useQuery({
    queryKey: catalogKeys.plugins,
    queryFn: async (): Promise<Plugin[]> => (await api.list('plugins')).items,
  });

  const mcpServersQuery = useQuery({
    queryKey: catalogKeys.mcpServers,
    queryFn: async (): Promise<McpServer[]> => (await api.list('mcp-servers')).items,
  });

  return {
    tools: toolsQuery.data ?? [],
    skills: skillsQuery.data ?? [],
    plugins: pluginsQuery.data ?? [],
    mcpServers: mcpServersQuery.data ?? [],
    isPending:
      toolsQuery.isPending ||
      skillsQuery.isPending ||
      pluginsQuery.isPending ||
      mcpServersQuery.isPending,
    isError:
      toolsQuery.isError ||
      skillsQuery.isError ||
      pluginsQuery.isError ||
      mcpServersQuery.isError,
    refetch: () => {
      void toolsQuery.refetch();
      void skillsQuery.refetch();
      void pluginsQuery.refetch();
      void mcpServersQuery.refetch();
    },
  };
}

/**
 * Tempo real: `tool.statusChanged` chega pelo stream global e invalida o
 * catálogo. O evento só cobre ferramentas — skills/plugins/MCP atualizam
 * pela invalidação no sucesso da mutation ({@link useSetComponentState}).
 */
export function useToolsRealtime() {
  useRealtimeStream(streams.global(), {
    types: ['tool.statusChanged'],
    invalidate: [CATALOG_PREFIX],
  });
}

/** Habilita/desabilita um item do catálogo (state: enabled | disabled). */
export function useSetComponentState() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      resource,
      id,
      state,
    }: {
      resource: CatalogResource;
      id: Ulid;
      state: ComponentState;
    }) => api.update(resource, id, { state }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: CATALOG_PREFIX }),
  });
}
