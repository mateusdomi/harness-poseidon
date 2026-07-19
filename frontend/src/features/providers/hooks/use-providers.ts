import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type Account,
  type Budget,
  type CreateAccountInput,
  type Model,
  type Provider,
  type RoutingPolicy,
  type Ulid,
  type UpdateAccountInput,
} from '@/api';
import { useApi } from '@/app/api-context';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

/** Query keys da feature de providers. */
export const providerKeys = {
  providers: ['providers', 'providers'] as const,
  accounts: ['providers', 'accounts'] as const,
  models: ['providers', 'models'] as const,
  budgets: ['providers', 'budgets'] as const,
  routingPolicies: ['providers', 'routing-policies'] as const,
  projects: ['providers', 'projects'] as const,
};

const PROVIDERS_PREFIX = ['providers'] as const;

export function useProviders() {
  const api = useApi();
  return useQuery({
    queryKey: providerKeys.providers,
    queryFn: async (): Promise<Provider[]> => (await api.list('providers')).items,
  });
}

export function useAccounts() {
  const api = useApi();
  return useQuery({
    queryKey: providerKeys.accounts,
    queryFn: async (): Promise<Account[]> => (await api.list('accounts')).items,
  });
}

export function useModels() {
  const api = useApi();
  return useQuery({
    queryKey: providerKeys.models,
    queryFn: async (): Promise<Model[]> => (await api.list('models')).items,
  });
}

export function useBudgets() {
  const api = useApi();
  return useQuery({
    queryKey: providerKeys.budgets,
    queryFn: async (): Promise<Budget[]> => (await api.list('budgets')).items,
  });
}

export function useRoutingPolicies() {
  const api = useApi();
  return useQuery({
    queryKey: providerKeys.routingPolicies,
    queryFn: async (): Promise<RoutingPolicy[]> => (await api.list('routing-policies')).items,
  });
}

/** Sincroniza o catálogo de modelos do provider (comando mock). */
export function useSyncProviderCatalog() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (providerId: Ulid) => api.syncProviderCatalog(providerId),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/** Cria uma conta de provider (nasce `active`). */
export function useCreateAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateAccountInput) => api.createAccount(input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/** Edita apelido/e-mail/plano da conta. */
export function useUpdateAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: Ulid; input: UpdateAccountInput }) =>
      api.updateAccount(id, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/** Habilita a conta (`state: active`). */
export function useEnableAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: Ulid) => api.enableAccount(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/** Desabilita a conta (`state: disabled`). */
export function useDisableAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: Ulid) => api.disableAccount(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/**
 * Remove a conta — a API responde 409 quando há budget ou definição de
 * agente referenciando-a; a UI exibe o detalhe do problema (ApiError).
 */
export function useDeleteAccount() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: Ulid) => api.deleteAccount(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/** Edição da política de roteamento (PATCH com confirmação na UI). */
export function useUpdateRoutingPolicy() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, rules }: { id: Ulid; rules: RoutingPolicy['rules'] }) =>
      api.update('routing-policies', id, { rules }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROVIDERS_PREFIX }),
  });
}

/**
 * Tempo real: `quota.updated` (stream global) atualiza a cota da conta no
 * cache e invalida budgets (o evento pode referenciar budgetId).
 */
export function useProvidersRealtime() {
  const queryClient = useQueryClient();
  useRealtimeStream(streams.global(), {
    types: ['quota.updated'],
    onEvent: (event) => {
      if (event.type !== 'quota.updated') return;
      const { accountId, usedUsd } = event.payload;
      if (accountId) {
        queryClient.setQueryData<Account[]>(providerKeys.accounts, (current) =>
          current?.map((account) =>
            account.id === accountId ? { ...account, quotaUsedUsd: usedUsd } : account,
          ),
        );
      }
    },
    invalidate: [providerKeys.budgets],
  });
}
