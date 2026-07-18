import { createContext, useContext } from 'react';

import type { ApiBundle, ApiClient, RealtimeClient } from '@/api';

/**
 * Contexto da camada de dados. O bundle é criado uma única vez em
 * `AppProviders` (via `createApi()`); as features consomem `useApi()` /
 * `useRealtime()` sem saber se o modo é mock ou http.
 */
export const ApiContext = createContext<ApiBundle | null>(null);

export function useApiBundle(): ApiBundle {
  const bundle = useContext(ApiContext);
  if (!bundle) throw new Error('useApiBundle fora de <AppProviders>.');
  return bundle;
}

export function useApi(): ApiClient {
  return useApiBundle().api;
}

export function useRealtime(): RealtimeClient {
  return useApiBundle().realtime;
}
