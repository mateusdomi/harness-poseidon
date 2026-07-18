import { useEffect, useState } from 'react';

/**
 * Media query reativa (`window.matchMedia`). Em ambiente sem matchMedia
 * (jsdom) retorna `initial` — os testes de componente assumem mobile por
 * padrão e stubam matchMedia para exercitar o layout desktop.
 */
export function useMediaQuery(query: string, initial = false): boolean {
  const [matches, setMatches] = useState(() =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia(query).matches
      : initial,
  );

  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const media = window.matchMedia(query);
    setMatches(media.matches);
    const listener = (event: MediaQueryListEvent) => setMatches(event.matches);
    media.addEventListener('change', listener);
    return () => media.removeEventListener('change', listener);
  }, [query]);

  return matches;
}
