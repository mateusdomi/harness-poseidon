import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import type { Ulid } from '@/api';

interface ActiveProjectState {
  /**
   * Projeto ativo da sessão (cockpit, chat, quadro...). Persistido em
   * sessionStorage: sobrevive a reload, morre ao fechar a aba.
   * `null` = nenhum selecionado → telas escolhem o primeiro da lista.
   */
  activeProjectId: Ulid | null;
  setActiveProject: (projectId: Ulid) => void;
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
      activeProjectId: null,
      setActiveProject: (projectId) => set({ activeProjectId: projectId }),
      chatDraft: null,
      setChatDraft: (message) => set({ chatDraft: message }),
    }),
    {
      name: 'poseidon-active-project',
      storage: createJSONStorage(() => sessionStorage),
      partialize: (state) => ({ activeProjectId: state.activeProjectId }),
    },
  ),
);
