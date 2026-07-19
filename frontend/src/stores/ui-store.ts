import { create } from 'zustand';
import { persist } from 'zustand/middleware';

interface UiState {
  /** Sidebar do desktop: colapsada (só ícones) ou expandida. */
  sidebarCollapsed: boolean;
  /** Drawer de navegação mobile. */
  mobileNavOpen: boolean;
  /** Painel lateral de workflow do chat (desktop, lg+) — persistido (D-071). */
  chatWorkflowPanelOpen: boolean;
  toggleSidebar: () => void;
  setMobileNavOpen: (open: boolean) => void;
  toggleChatWorkflowPanel: () => void;
}

export const useUiStore = create<UiState>()(
  persist(
    (set) => ({
      sidebarCollapsed: false,
      mobileNavOpen: false,
      chatWorkflowPanelOpen: true,
      toggleSidebar: () => set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),
      setMobileNavOpen: (open) => set({ mobileNavOpen: open }),
      toggleChatWorkflowPanel: () =>
        set((s) => ({ chatWorkflowPanelOpen: !s.chatWorkflowPanelOpen })),
    }),
    {
      name: 'poseidon-ui',
      partialize: (state) => ({
        sidebarCollapsed: state.sidebarCollapsed,
        chatWorkflowPanelOpen: state.chatWorkflowPanelOpen,
      }),
    },
  ),
);
