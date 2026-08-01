import { useQuery } from '@tanstack/react-query';

import type { ProjectReliability } from '@/api';
import { useApi } from '@/app/api-context';

/** Query key da confiabilidade/produtividade de um projeto. */
export const reliabilityKey = (projectId: string | null, k: number) =>
  ['reliability', projectId, k] as const;

/**
 * Confiabilidade e produtividade do projeto ativo (B1+B12/F16).
 *
 * Sem projeto ativo a query fica desabilitada — não há o que medir, e disparar a chamada só para
 * receber erro faria a tela piscar um estado de falha que não é falha.
 */
export function useReliability(projectId: string | null, k: number) {
  const api = useApi();
  return useQuery({
    queryKey: reliabilityKey(projectId, k),
    enabled: projectId !== null,
    queryFn: async (): Promise<ProjectReliability> => api.getProjectReliability(projectId!, k),
  });
}
