import { createContext, useContext } from 'react';

import type { ArchitectureApi } from './architecture-api';

/**
 * Contexto da camada de dados do Architecture Hub. O provider
 * (`ArchitectureApiProvider`) cria o cliente uma vez (mock ou http) ou
 * aceita um injetado nos testes; as features consomem via `useArchitectureApi`.
 */
export const ArchitectureApiContext = createContext<ArchitectureApi | null>(null);

export function useArchitectureApi(): ArchitectureApi {
  const client = useContext(ArchitectureApiContext);
  if (!client) throw new Error('useArchitectureApi fora de <ArchitectureApiProvider>.');
  return client;
}
