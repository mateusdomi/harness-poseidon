import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import type { Ulid } from '@/api';
import type { PresentationMode } from '@/app/presentation/presentation-policy';

interface PresentationState {
  modeByProfile: Record<Ulid, PresentationMode>;
  requestMode: (profileId: Ulid, mode: PresentationMode) => void;
}

export const usePresentationStore = create<PresentationState>()(
  persist(
    (set) => ({
      modeByProfile: {},
      requestMode: (profileId, mode) =>
        set((state) => ({
          modeByProfile: { ...state.modeByProfile, [profileId]: mode },
        })),
    }),
    {
      name: 'poseidon-presentation',
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({ modeByProfile: state.modeByProfile }),
      version: 1,
    },
  ),
);
