import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type Theme = 'dark' | 'light';
export type ThemePreference = Theme | 'system';

function resolveSystemTheme(): Theme {
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: light)').matches) {
    return 'light';
  }
  return 'dark';
}

function applyTheme(theme: Theme): void {
  if (typeof document !== 'undefined') {
    document.documentElement.dataset.theme = theme;
  }
}

interface ThemeState {
  /** Preferência do usuário: 'system' segue prefers-color-scheme. */
  preference: ThemePreference;
  /** Tema efetivamente aplicado. */
  resolved: Theme;
  setPreference: (preference: ThemePreference) => void;
  /** Alterna entre dark/light fixos (sai do modo system). */
  toggle: () => void;
}

export const useThemeStore = create<ThemeState>()(
  persist(
    (set, get) => ({
      preference: 'system',
      resolved: resolveSystemTheme(),
      setPreference: (preference) => {
        const resolved = preference === 'system' ? resolveSystemTheme() : preference;
        applyTheme(resolved);
        set({ preference, resolved });
      },
      toggle: () => {
        const next: Theme = get().resolved === 'dark' ? 'light' : 'dark';
        applyTheme(next);
        set({ preference: next, resolved: next });
      },
    }),
    {
      name: 'poseidon-theme',
      partialize: (state) => ({ preference: state.preference }),
      onRehydrateStorage: () => (state) => {
        const preference = state?.preference ?? 'system';
        const resolved = preference === 'system' ? resolveSystemTheme() : preference;
        applyTheme(resolved);
        state?.setPreference(preference);
      },
    },
  ),
);

// Reage a mudanças do SO quando a preferência é "system".
if (typeof window !== 'undefined' && window.matchMedia) {
  window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
    const { preference, setPreference } = useThemeStore.getState();
    if (preference === 'system') {
      setPreference('system');
    }
  });
}
