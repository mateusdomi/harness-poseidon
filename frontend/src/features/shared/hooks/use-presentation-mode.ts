import { useEffect, useState } from 'react';

export type PresentationMode = 'business' | 'technical' | 'admin';

const STORAGE_KEY = 'poseidon_presentation_mode';

export function usePresentationMode(): {
  mode: PresentationMode;
  setMode: (mode: PresentationMode) => void;
  isBusiness: boolean;
  isTechnical: boolean;
  isAdmin: boolean;
} {
  const [mode, setModeState] = useState<PresentationMode>(() => {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'technical' || stored === 'admin' || stored === 'business') {
      return stored;
    }
    return 'business';
  });

  const setMode = (newMode: PresentationMode) => {
    localStorage.setItem(STORAGE_KEY, newMode);
    setModeState(newMode);
    window.dispatchEvent(new CustomEvent('presentation_mode_changed', { detail: newMode }));
  };

  useEffect(() => {
    const handleCustom = (e: Event) => {
      const customEvent = e as CustomEvent<PresentationMode>;
      if (customEvent.detail) setModeState(customEvent.detail);
    };
    window.addEventListener('presentation_mode_changed', handleCustom);
    return () => window.removeEventListener('presentation_mode_changed', handleCustom);
  }, []);

  return {
    mode,
    setMode,
    isBusiness: mode === 'business',
    isTechnical: mode === 'technical',
    isAdmin: mode === 'admin',
  };
}
