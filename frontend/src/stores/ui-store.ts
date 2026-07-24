import { create } from 'zustand';
import { persist } from 'zustand/middleware';

interface UiState {
  /** Sidebar do desktop: colapsada (só ícones) ou expandida. */
  sidebarCollapsed: boolean;
  /** Drawer de navegação mobile. */
  mobileNavOpen: boolean;
  /** Painel lateral de workflow do chat (desktop, lg+) — persistido (D-071). */
  chatWorkflowPanelOpen: boolean;
  /**
   * Grupos da navegação lateral recolhidos (G-MENUS). Mapa `chave do grupo →
   * true` — só os grupos recolhidos ficam registrados; ausência = expandido.
   * Assim o usuário deixa aberto só o que lhe interessa, reduzindo poluição
   * visual. Persistido em localStorage.
   */
  collapsedNavGroups: Record<string, boolean>;
  toggleSidebar: () => void;
  setMobileNavOpen: (open: boolean) => void;
  toggleChatWorkflowPanel: () => void;
  /** Alterna (recolhe/expande) um grupo da navegação lateral pela sua chave. */
  toggleNavGroup: (groupKey: string) => void;
}

export const useUiStore = create<UiState>()(
  persist(
    (set) => ({
      sidebarCollapsed: false,
      mobileNavOpen: false,
      chatWorkflowPanelOpen: true,
      collapsedNavGroups: {},
      toggleSidebar: () => set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),
      setMobileNavOpen: (open) => set({ mobileNavOpen: open }),
      toggleChatWorkflowPanel: () =>
        set((s) => ({ chatWorkflowPanelOpen: !s.chatWorkflowPanelOpen })),
      toggleNavGroup: (groupKey) =>
        set((s) => {
          const next = { ...s.collapsedNavGroups };
          if (next[groupKey]) {
            delete next[groupKey];
          } else {
            next[groupKey] = true;
          }
          return { collapsedNavGroups: next };
        }),
    }),
    {
      name: 'poseidon-ui',
      partialize: (state) => ({
        sidebarCollapsed: state.sidebarCollapsed,
        chatWorkflowPanelOpen: state.chatWorkflowPanelOpen,
        collapsedNavGroups: state.collapsedNavGroups,
      }),
    },
  ),
);
