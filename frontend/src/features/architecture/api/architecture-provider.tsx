import { useMemo, type ReactNode } from 'react';

import { createArchitectureApi, type ArchitectureApi } from './architecture-api';
import { ArchitectureApiContext } from './architecture-context';

/**
 * Provider da camada de dados do Architecture Hub. Cria o cliente uma vez
 * (mock ou http, conforme `VITE_API_MODE`) ou aceita um cliente injetado —
 * usado pelos testes para semear um modelo determinístico.
 */
export function ArchitectureApiProvider({
  client,
  children,
}: {
  client?: ArchitectureApi;
  children: ReactNode;
}) {
  const value = useMemo(() => client ?? createArchitectureApi(), [client]);
  return (
    <ArchitectureApiContext.Provider value={value}>{children}</ArchitectureApiContext.Provider>
  );
}
