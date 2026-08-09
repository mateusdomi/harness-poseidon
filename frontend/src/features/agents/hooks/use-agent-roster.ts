import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { AgentAccountRoster } from '@/api';
import { useApi } from '@/app/api-context';

/** Query key do roster de identidades de execução (fleet). */
export const agentRosterKey = ['agents', 'roster'] as const;

/**
 * Roster de execução REDIGIDO: as identidades (contas de agent-run) que a fleet do
 * Chefe usa para executar trabalho. Só alias/provider/executor/papéis/estado — nunca
 * credencial. Distinto das personas/definições de agente.
 */
export function useAgentRoster() {
  const api = useApi();
  return useQuery({
    queryKey: agentRosterKey,
    queryFn: async (): Promise<AgentAccountRoster[]> => api.listAgentAccounts(),
  });
}

export function usePrepareAgentAccountAuth() {
  const api = useApi();
  return useMutation({
    mutationFn: (alias: string) => api.prepareAgentAccountAuth(alias),
  });
}

export function useChiefAssignment() {
  const api = useApi();
  return useQuery({
    queryKey: ['agents', 'chief-assignment'] as const,
    queryFn: () => api.getChiefAssignment(),
  });
}

export function useSetChiefPrimary() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (alias: string) => api.setChiefPrimary(alias),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: agentRosterKey });
      void queryClient.invalidateQueries({ queryKey: ['agents', 'chief-assignment'] });
    },
  });
}
