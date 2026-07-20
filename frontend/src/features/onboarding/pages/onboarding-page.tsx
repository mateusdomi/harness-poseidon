import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import type { Profile } from '@/api';
import { Button, Skeleton } from '@/design-system';
import { product } from '@/config/product';
import { useProfiles } from '@/features/shared/hooks/use-profiles';
import { useSessionStore } from '@/stores/session-store';
import { OnboardingWizard } from '@/features/onboarding/components/onboarding-wizard';
import { ProfilePicker } from '@/features/onboarding/components/profile-picker';
import logoUrl from '@/assets/logo.png';

/**
 * Porta de entrada do app (rota `/onboarding`, fora do AppShell):
 * - Sem perfis locais → wizard de primeiro uso direto.
 * - Com perfis → seleção de perfil; "criar novo" abre o wizard.
 * Ao concluir, define o perfil ativo da sessão e volta à rota de origem.
 */
export default function OnboardingPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const setActiveProfile = useSessionStore((s) => s.setActiveProfile);
  const profilesQuery = useProfiles();
  const [creating, setCreating] = useState(false);

  const from = (location.state as { from?: string } | null)?.from ?? '/cockpit';

  function handleProfileReady(profile: Profile) {
    setActiveProfile(profile.id);
    navigate(from, { replace: true });
  }

  let content: React.ReactNode;
  if (profilesQuery.isLoading) {
    content = (
      <div
        className="flex w-full max-w-xl flex-col gap-3"
        role="status"
        aria-label={t('common.states.loading')}
      >
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-20 w-full" />
        <Skeleton className="h-20 w-full" />
      </div>
    );
  } else if (profilesQuery.isError) {
    content = (
      <div className="flex w-full max-w-xl flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={() => void profilesQuery.refetch()}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  } else if (creating || (profilesQuery.data ?? []).length === 0) {
    content = (
      <OnboardingWizard
        onCompleted={handleProfileReady}
        onCancel={(profilesQuery.data ?? []).length > 0 ? () => setCreating(false) : undefined}
      />
    );
  } else {
    content = (
      <ProfilePicker
        profiles={profilesQuery.data ?? []}
        onSelect={handleProfileReady}
        onCreateNew={() => setCreating(true)}
      />
    );
  }

  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-6 bg-background p-4">
      <img
        src={logoUrl}
        alt={product.name}
        className="h-auto w-60 object-contain dark:brightness-125 md:w-80"
      />
      {content}
    </main>
  );
}
