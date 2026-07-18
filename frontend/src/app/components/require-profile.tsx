import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';

import { useSessionStore } from '@/stores/session-store';

/**
 * Guard de sessão local: sem perfil ativo, todo o app redireciona para
 * `/onboarding` (seleção de perfil ou wizard de primeiro uso). A rota
 * `/onboarding` fica FORA deste guard, sem AppShell.
 */
export function RequireProfile({ children }: { children: ReactNode }) {
  const activeProfileId = useSessionStore((s) => s.activeProfileId);
  const location = useLocation();

  if (!activeProfileId) {
    return <Navigate to="/onboarding" replace state={{ from: location.pathname }} />;
  }
  return <>{children}</>;
}
