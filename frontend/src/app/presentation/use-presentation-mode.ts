import { useCallback, useMemo } from 'react';

import {
  PRESENTATION_MODES,
  presentationProfileKey,
  resolvePresentationProjection,
  type PresentationMode,
  type PresentationProjection,
} from '@/app/presentation/presentation-mode';
import { usePresentationModeStore } from '@/stores/presentation-mode-store';
import { useSessionStore } from '@/stores/session-store';

export interface PresentationModeResult extends PresentationProjection {
  setMode: (mode: PresentationMode) => void;
}

/**
 * Fronteira única do modo de apresentação (F1 da campanha): toda tela que
 * precisa decidir "isto aparece para o cliente leigo?" chama este hook e lê
 * `isBusiness` / `showTechnicalDetails` / `showAdministrativeActions`.
 * Nenhuma tela repete a condição, e o padrão — inclusive sem perfil e com
 * valor persistido corrompido — é sempre Negócio.
 */
export function usePresentationMode(): PresentationModeResult {
  const profileId = useSessionStore((state) => state.activeProfileId);
  const modeByProfile = usePresentationModeStore((state) => state.modeByProfile);
  const requestMode = usePresentationModeStore((state) => state.requestMode);

  const projection = useMemo(
    () =>
      resolvePresentationProjection(
        modeByProfile[presentationProfileKey(profileId)],
        PRESENTATION_MODES,
      ),
    [modeByProfile, profileId],
  );

  const setMode = useCallback(
    (mode: PresentationMode) => requestMode(profileId, mode),
    [profileId, requestMode],
  );

  return { ...projection, setMode };
}
