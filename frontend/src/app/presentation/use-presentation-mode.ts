import { useCallback, useMemo } from 'react';

import {
  PRESENTATION_MODES,
  resolvePresentationProjection,
  type PresentationMode,
  type PresentationProjection,
} from '@/app/presentation/presentation-mode';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

export interface PresentationModeResult extends PresentationProjection {
  setMode: (mode: PresentationMode) => void;
}

/**
 * Fronteira única do modo de apresentação (F1): toda tela que precisa decidir
 * "isto aparece para o cliente leigo?" chama este hook e lê `isBusiness` /
 * `showTechnicalDetails` / `showAdministrativeActions`. Nenhuma tela repete a
 * condição, e o padrão — sem perfil escolhido, sem preferência salva ou com
 * valor persistido corrompido — é sempre Negócio.
 *
 * Leitura síncrona e barata: lê a escolha persistida, sem consultar a API.
 * Quem precisa saber **quais modos o perfil pode escolher** (papel e
 * direitos) usa `usePresentationPolicy()`, que escreve no mesmo store — a
 * escolha do modo tem uma fonte só.
 */
export function usePresentationMode(): PresentationModeResult {
  const profileId = useSessionStore((state) => state.activeProfileId);
  const modeByProfile = usePresentationStore((state) => state.modeByProfile);
  const requestMode = usePresentationStore((state) => state.requestMode);

  const projection = useMemo(
    () =>
      resolvePresentationProjection(
        profileId ? modeByProfile[profileId] : undefined,
        PRESENTATION_MODES,
      ),
    [modeByProfile, profileId],
  );

  const setMode = useCallback(
    (mode: PresentationMode) => {
      if (profileId) requestMode(profileId, mode);
    },
    [profileId, requestMode],
  );

  return { ...projection, setMode };
}
