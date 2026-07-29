import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import {
  DEFAULT_PRESENTATION_MODE,
  isPresentationMode,
  presentationProfileKey,
  type PresentationMode,
} from '@/app/presentation/presentation-mode';
import type { Ulid } from '@/api';

interface PresentationModeState {
  /** Modo escolhido por perfil. Perfil sem escolha cai no padrão (negócio). */
  modeByProfile: Record<string, PresentationMode>;
  requestMode: (profileId: Ulid | null, mode: PresentationMode) => void;
  modeFor: (profileId: Ulid | null) => PresentationMode;
}

/**
 * Fonte única do modo de apresentação escolhido. Persistido em localStorage
 * (a preferência atravessa sessões, ao contrário do projeto ativo) e por
 * perfil, porque a mesma máquina atende dono leigo e operador técnico.
 *
 * Telas não leem este store direto — leem `usePresentationMode()`.
 */
export const usePresentationModeStore = create<PresentationModeState>()(
  persist(
    (set, get) => ({
      modeByProfile: {},
      requestMode: (profileId, mode) => {
        if (!isPresentationMode(mode)) return;
        set((state) => ({
          modeByProfile: {
            ...state.modeByProfile,
            [presentationProfileKey(profileId)]: mode,
          },
        }));
      },
      modeFor: (profileId) => {
        const stored = get().modeByProfile[presentationProfileKey(profileId)];
        return isPresentationMode(stored) ? stored : DEFAULT_PRESENTATION_MODE;
      },
    }),
    {
      name: 'poseidon-presentation-mode',
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({ modeByProfile: state.modeByProfile }),
      version: 1,
    },
  ),
);
