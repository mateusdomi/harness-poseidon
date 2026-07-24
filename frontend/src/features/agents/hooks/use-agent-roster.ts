import { useQuery } from '@tanstack/react-query';

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
