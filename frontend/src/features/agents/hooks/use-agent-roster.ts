import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { AgentAccountRoster, V3AgentAccountUpsertInput } from '@/api';
import { useApi } from '@/app/api-context';

/** Query key do roster de identidades de execução (fleet). */
export const agentRosterKey = ['agents', 'roster'] as const;
export const v3AgentAccountsKey = ['agents', 'v3-accounts'] as const;

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

export function useV3AgentAccounts() {
  const api = useApi();
  return useQuery({
    queryKey: v3AgentAccountsKey,
    queryFn: () => api.listV3AgentAccounts(),
  });
}

function invalidateAgentAccounts(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: agentRosterKey });
  void queryClient.invalidateQueries({ queryKey: v3AgentAccountsKey });
  void queryClient.invalidateQueries({ queryKey: ['agents', 'chief-assignment'] });
}

export function usePrepareAgentAccountAuth() {
  const api = useApi();
  return useMutation({
    mutationFn: (alias: string) => api.prepareAgentAccountAuth(alias),
  });
}

export function useUpsertV3AgentAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: V3AgentAccountUpsertInput) => api.upsertV3AgentAccount(input),
    onSuccess: () => invalidateAgentAccounts(queryClient),
  });
}

export function useEnableV3AgentAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (alias: string) => api.enableV3AgentAccount(alias),
    onSuccess: () => invalidateAgentAccounts(queryClient),
  });
}

export function useDisableV3AgentAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (alias: string) => api.disableV3AgentAccount(alias),
    onSuccess: () => invalidateAgentAccounts(queryClient),
  });
}

export function useLogoutV3AgentAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (alias: string) => api.logoutV3AgentAccount(alias),
    onSuccess: () => invalidateAgentAccounts(queryClient),
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
      invalidateAgentAccounts(queryClient);
    },
  });
}
