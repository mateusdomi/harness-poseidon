import type { Entitlement, Profile } from '@/api';

export type PresentationMode = 'business' | 'technical' | 'admin';

export const TECHNICAL_PRESENTATION_ENTITLEMENT = 'presentation.technical';

export interface PresentationPolicy {
  mode: PresentationMode;
  allowedModes: readonly PresentationMode[];
  showTechnicalDetails: boolean;
  showAdministrativeActions: boolean;
}

export function allowedPresentationModes(
  profile: Pick<Profile, 'role'>,
  entitlements: readonly Pick<Entitlement, 'key' | 'included'>[],
): readonly PresentationMode[] {
  const technicalEntitled = entitlements.some(
    (entitlement) =>
      entitlement.key === TECHNICAL_PRESENTATION_ENTITLEMENT && entitlement.included,
  );
  if (profile.role === 'admin') return ['business', 'technical', 'admin'];
  if (technicalEntitled) return ['business', 'technical'];
  return ['business'];
}

export function resolvePresentationPolicy(
  profile: Pick<Profile, 'role'>,
  entitlements: readonly Pick<Entitlement, 'key' | 'included'>[],
  requestedMode: PresentationMode | undefined,
): PresentationPolicy {
  const allowedModes = allowedPresentationModes(profile, entitlements);
  const mode =
    requestedMode && allowedModes.includes(requestedMode) ? requestedMode : 'business';
  return {
    mode,
    allowedModes,
    showTechnicalDetails: mode === 'technical' || mode === 'admin',
    showAdministrativeActions: mode === 'admin',
  };
}
