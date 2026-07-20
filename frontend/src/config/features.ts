/** Flags operacionais compiladas no frontend. Ausência ou valor inválido é fail-closed. */
export function isOperationalFeatureEnabled(value: string | undefined): boolean {
  return value === 'on';
}

export const featureFlags = {
  governanceContractUi: isOperationalFeatureEnabled(
    import.meta.env.VITE_GOVERNANCE_CONTRACT_UI,
  ),
} as const;
