import { useTranslation } from 'react-i18next';
import { UserRound } from 'lucide-react';

import type { Profile } from '@/api';
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';

export interface ProfilePickerProps {
  profiles: Profile[];
  onSelect: (profile: Profile) => void;
  onCreateNew: () => void;
}

/**
 * Retorno de usuário: seleção de perfil local. Criar novo perfil leva ao
 * wizard de primeiro uso.
 */
export function ProfilePicker({ profiles, onSelect, onCreateNew }: ProfilePickerProps) {
  const { t } = useTranslation();

  return (
    <Card className="w-full max-w-xl">
      <CardHeader>
        <CardTitle>{t('onboarding.selectProfile.title')}</CardTitle>
        <CardDescription>{t('onboarding.selectProfile.description')}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <ul className="flex flex-col gap-2">
          {profiles.map((profile) => (
            <li key={profile.id}>
              <button
                type="button"
                onClick={() => onSelect(profile)}
                className="flex min-h-touch w-full items-center gap-3 rounded-md border border-border bg-surface-elevated p-4 text-start transition-colors hover:border-border-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                <span
                  aria-hidden="true"
                  className="flex size-10 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground"
                >
                  <UserRound className="size-5" aria-hidden="true" />
                </span>
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="truncate font-medium">{profile.displayName}</span>
                  <span className="truncate text-sm text-foreground-muted">
                    {profile.email ??
                      t('onboarding.selectProfile.lastActive', {
                        time: formatRelativeTime(profile.lastActiveAt),
                      })}
                  </span>
                </span>
                <span className="shrink-0 text-sm font-medium text-brand-strong">
                  {t('onboarding.selectProfile.use')}
                </span>
              </button>
            </li>
          ))}
        </ul>
        <Button type="button" variant="outline" onClick={onCreateNew} className="self-start">
          {t('onboarding.selectProfile.createNew')}
        </Button>
      </CardContent>
    </Card>
  );
}
