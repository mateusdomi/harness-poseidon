import { useQuery } from '@tanstack/react-query';

import { useArchitectureApi } from '../api/architecture-context';

/**
 * Hooks de leitura do Architecture Hub (ARC-02/03/06/07/08/10). Todas as
 * rotas são somente-leitura; a mutação de arquitetura flui por propostas
 * (ARC-05) e pelo Studio (ARC-04). Chaves estáveis por projeto/entidade.
 */
export const hubKeys = {
  systems: (projectId: string) => ['architecture-hub', 'systems', projectId] as const,
  domains: (projectId: string) => ['architecture-hub', 'domains', projectId] as const,
  capabilities: (projectId: string) => ['architecture-hub', 'capabilities', projectId] as const,
  integration: (projectId: string) => ['architecture-hub', 'integration', projectId] as const,
  heatmap: (projectId: string) => ['architecture-hub', 'heatmap', projectId] as const,
  system360: (systemId: string) => ['architecture-hub', 'system360', systemId] as const,
  discoveries: (projectId: string) => ['architecture-hub', 'discoveries', projectId] as const,
  discoverySummary: (projectId: string) =>
    ['architecture-hub', 'discovery-summary', projectId] as const,
  insights: (projectId: string) => ['architecture-hub', 'insights', projectId] as const,
  patterns: (projectId: string, kind: string) =>
    ['architecture-hub', 'patterns', projectId, kind] as const,
  baselines: (projectId: string) => ['architecture-hub', 'baselines', projectId] as const,
  baselineComparison: (id: string) => ['architecture-hub', 'baseline-comparison', id] as const,
};

const KEY = (projectId: string | null) => projectId ?? 'none';

export function useSystemCatalog(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.systems(KEY(projectId)),
    queryFn: () => api.listSystems(projectId),
  });
}

export function useDomainMap(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.domains(KEY(projectId)),
    queryFn: () => api.getDomainMap(projectId),
  });
}

export function useCapabilityMap(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.capabilities(KEY(projectId)),
    queryFn: () => api.getCapabilityMap(projectId),
  });
}

export function useIntegrationGraph(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.integration(KEY(projectId)),
    queryFn: () => api.getIntegrationGraph(projectId),
  });
}

export function useHeatmap(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.heatmap(KEY(projectId)),
    queryFn: () => api.getHeatmap(projectId),
  });
}

export function useSystem360(systemId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.system360(systemId ?? 'none'),
    enabled: systemId !== null,
    queryFn: () => api.getSystemOverview(systemId!),
  });
}

export function useDiscoveries(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.discoveries(KEY(projectId)),
    queryFn: () => api.listDiscoveries(projectId),
  });
}

export function useDiscoverySummary(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.discoverySummary(KEY(projectId)),
    queryFn: () => api.getDiscoverySummary(projectId),
  });
}

export function useInsights(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.insights(KEY(projectId)),
    queryFn: () => api.getInsights(projectId),
  });
}

export function usePatterns(projectId: string | null, kind: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.patterns(KEY(projectId), kind ?? 'all'),
    queryFn: () => api.listPatterns(projectId, kind),
  });
}

export function useBaselines(projectId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.baselines(KEY(projectId)),
    queryFn: () => api.listBaselines(projectId),
  });
}

export function useBaselineComparison(baselineId: string | null) {
  const api = useArchitectureApi();
  return useQuery({
    queryKey: hubKeys.baselineComparison(baselineId ?? 'none'),
    enabled: baselineId !== null,
    queryFn: () => api.getBaselineComparison(baselineId!),
  });
}
