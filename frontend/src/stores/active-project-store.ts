import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import type { Ulid } from '@/api';

export interface ProjectSelection {
  projectId: Ulid;
  selectedAt: string;
}

interface ActiveProjectState {
  /**
   * Seleção persistida isoladamente por perfil. O perfil faz parte da chave
   * para impedir que a troca de usuário reutilize contexto de outro usuário.
   */
  selectionsByProfile: Record<Ulid, ProjectSelection>;
  selectProject: (profileId: Ulid, projectId: Ulid, selectedAt?: string) => void;
  clearProject: (profileId: Ulid) => void;
  /**
   * Rascunho de mensagem para o chat (ex.: "Executar no chat" do cockpit).
   * NÃO persistido: consumido e limpo pela tela de chat.
   */
  chatDraft: string | null;
  setChatDraft: (message: string | null) => void;
}

export const useActiveProjectStore = create<ActiveProjectState>()(
  persist(
    (set) => ({
      selectionsByProfile: {},
      selectProject: (profileId, projectId, selectedAt = new Date().toISOString()) =>
        set((state) => ({
          selectionsByProfile: {
            ...state.selectionsByProfile,
            [profileId]: { projectId, selectedAt },
          },
        })),
      clearProject: (profileId) =>
        set((state) => {
          const selectionsByProfile = { ...state.selectionsByProfile };
          delete selectionsByProfile[profileId];
          return { selectionsByProfile };
        }),
      chatDraft: null,
      setChatDraft: (message) => set({ chatDraft: message }),
    }),
    {
      name: 'poseidon-active-project',
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({ selectionsByProfile: state.selectionsByProfile }),
      version: 2,
      migrate: () => ({ selectionsByProfile: {} }),
    },
  ),
);
