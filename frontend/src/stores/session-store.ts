import { create } from 'zustand';
import { persist } from 'zustand/middleware';

import type { Ulid } from '@/api';

interface SessionState {
  /**
   * Perfil local ativo da sessão (modo pessoal). `null` = ninguém escolheu
   * perfil ainda → o router redireciona tudo para `/onboarding`.
   */
  activeProfileId: Ulid | null;
  setActiveProfile: (profileId: Ulid | null) => void;
}

export const useSessionStore = create<SessionState>()(
  persist(
    (set) => ({
      activeProfileId: null,
      setActiveProfile: (profileId) => set({ activeProfileId: profileId }),
    }),
    { name: 'poseidon-session' },
  ),
);
