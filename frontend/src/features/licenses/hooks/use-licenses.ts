import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import type { ActivateLicenseInput, Entitlement, License } from '@/api';
import { useApi } from '@/app/api-context';

/** Query keys da feature de licença. */
export const licenseKeys = {
  license: ['licenses', 'license'] as const,
  entitlements: ['licenses', 'entitlements'] as const,
};

const LICENSES_PREFIX = ['licenses'] as const;

/** Licença do dispositivo (a primeira — modo pessoal, um dispositivo). */
export function useLicense() {
  const api = useApi();
  return useQuery({
    queryKey: licenseKeys.license,
    queryFn: async (): Promise<License | null> => (await api.list('licenses')).items[0] ?? null,
  });
}

/** Direitos concedidos pela licença vigente. */
export function useEntitlements() {
  const api = useApi();
  return useQuery({
    queryKey: licenseKeys.entitlements,
    queryFn: async (): Promise<Entitlement[]> => (await api.list('entitlements')).items,
  });
}

/** Ativação por chave (formato XXXX-XXXX-XXXX-XXXX — validado no contrato). */
export function useActivateLicense() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: ActivateLicenseInput) => api.activateLicense(input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: LICENSES_PREFIX }),
  });
}
