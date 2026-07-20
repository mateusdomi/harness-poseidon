import { useEffect, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Navigate, useLocation } from 'react-router-dom';

import { ApiError } from '@/api';
import { useApi } from '@/app/api-context';
import { SESSION_INVALID_EVENT } from '@/app/api-error-events';
import { PermissionDenied } from '@/app/components/permission-denied';
import { RouteSkeleton } from '@/app/components/route-skeleton';
import { Button, Card, CardContent } from '@/design-system';
import { profileKeys } from '@/features/shared/hooks/use-profiles';
import { useSessionStore } from '@/stores/session-store';

/**
 * Guard de sessão local: sem perfil ativo, todo o app redireciona para
 * `/onboarding` (seleção de perfil ou wizard de primeiro uso). A rota
 * `/onboarding` fica FORA deste guard, sem AppShell.
 */
export function RequireProfile({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const activeProfileId = useSessionStore((s) => s.activeProfileId);
  const setActiveProfile = useSessionStore((s) => s.setActiveProfile);
  const location = useLocation();
  const api = useApi();
  const profileQuery = useQuery({
    queryKey: profileKeys.current(activeProfileId ?? 'none'),
    queryFn: () => api.getCurrentProfile(),
    enabled: activeProfileId !== null,
  });

  useEffect(() => {
    const invalidateSession = () => setActiveProfile(null);
    window.addEventListener(SESSION_INVALID_EVENT, invalidateSession);
    return () => window.removeEventListener(SESSION_INVALID_EVENT, invalidateSession);
  }, [setActiveProfile]);

  const invalidSession = activeProfileId !== null && (
    (profileQuery.error instanceof ApiError && [401, 404].includes(profileQuery.error.problem.status))
    || (profileQuery.data !== undefined && profileQuery.data.id !== activeProfileId)
  );
  useEffect(() => {
    if (invalidSession) setActiveProfile(null);
  }, [invalidSession, setActiveProfile]);

  if (!activeProfileId || invalidSession) {
    return <Navigate to="/onboarding" replace state={{ from: location.pathname }} />;
  }
  if (profileQuery.isLoading) return <RouteSkeleton />;
  if (profileQuery.error instanceof ApiError && profileQuery.error.problem.status === 403) {
    return <PermissionDenied onRetry={() => void profileQuery.refetch()} />;
  }
  if (profileQuery.isError) {
    return (
      <Card>
        <CardContent className="flex flex-col items-start gap-3 p-6">
          <p role="alert" className="text-sm text-error">{t('common.session.validationError')}</p>
          <Button type="button" variant="outline" onClick={() => void profileQuery.refetch()}>
            {t('common.actions.retry')}
          </Button>
        </CardContent>
      </Card>
    );
  }
  return <>{children}</>;
}
