/** Flags operacionais compiladas no frontend. Ausência ou valor inválido é fail-closed. */
export function isOperationalFeatureEnabled(value: string | undefined): boolean {
  return value === 'on';
}

export const featureFlags = {
  governanceContractUi: isOperationalFeatureEnabled(
    import.meta.env.VITE_GOVERNANCE_CONTRACT_UI,
  ),
} as const;

/**
 * Modo da camada de dados. `http` fala com um backend real; qualquer outro
 * valor (default `mock`) usa fixtures determinísticas — ou seja, "modo
 * simulado". A UI deve sinalizar isso explicitamente onde apresenta prontidão
 * operacional (Cockpit/Orquestrador), para nunca passar dado simulado como real.
 */
export const apiMode: string = import.meta.env.VITE_API_MODE ?? 'mock';

/** `true` quando a aplicação roda sobre fixtures (não sobre um backend real). */
export const isSimulatedMode: boolean = apiMode !== 'http';
