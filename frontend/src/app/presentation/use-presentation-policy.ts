import { useCallback, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';

import { useApi } from '@/app/api-context';
import {
  resolvePresentationPolicy,
  type PresentationMode,
  type PresentationPolicy,
} from '@/app/presentation/presentation-policy';
import { profileKeys } from '@/features/shared/hooks/use-profiles';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

export interface PresentationPolicyResult extends PresentationPolicy {
  isPending: boolean;
  setMode: (mode: PresentationMode) => void;
}

export const presentationKeys = {
  entitlements: ['presentation', 'entitlements'] as const,
};

/**
 * Única fronteira para decidir projeção de negócio/técnica/admin.
 * Componentes consomem flags sem reimplementar condições de perfil.
 */
export function usePresentationPolicy(): PresentationPolicyResult {
  const api = useApi();
  const expectedProfileId = useSessionStore((state) => state.activeProfileId);
  const modeByProfile = usePresentationStore((state) => state.modeByProfile);
  const requestMode = usePresentationStore((state) => state.requestMode);

  const profileQuery = useQuery({
    queryKey: profileKeys.current(expectedProfileId ?? 'current'),
    queryFn: () => api.getCurrentProfile(),
  });
  const entitlementsQuery = useQuery({
    queryKey: presentationKeys.entitlements,
    queryFn: async () => (await api.list('entitlements')).items,
    enabled: profileQuery.data !== undefined,
  });
  const profile = profileQuery.data;
  const requestedMode = profile ? modeByProfile[profile.id] : undefined;
  const policy = useMemo(
    () =>
      profile
        ? resolvePresentationPolicy(profile, entitlementsQuery.data ?? [], requestedMode)
        : {
            mode: 'business' as const,
            allowedModes: ['business'] as const,
            showTechnicalDetails: false,
            showAdministrativeActions: false,
          },
    [entitlementsQuery.data, profile, requestedMode],
  );
  const setMode = useCallback(
    (mode: PresentationMode) => {
      if (profile && policy.allowedModes.includes(mode)) requestMode(profile.id, mode);
    },
    [policy.allowedModes, profile, requestMode],
  );

  return {
    ...policy,
    isPending: profileQuery.isLoading || entitlementsQuery.isLoading,
    setMode,
  };
}
