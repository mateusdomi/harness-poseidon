import { useEffect, useId, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ChevronsUpDown, LogOut, UserRoundCog } from 'lucide-react';

import type { Profile } from '@/api';
import { useApi } from '@/app/api-context';
import { avatarColorsFor, initialsFor } from '@/lib/agent-persona';
import { profileKeys } from '@/features/shared/hooks/use-profiles';
import { useSessionStore } from '@/stores/session-store';
import { cn } from '@/lib/utils';

/**
 * Avatar do perfil ativo: usa a foto (`avatarUrl`) quando existe; caso
 * contrário, cai para iniciais determinísticas (mesma cor HSL estável por
 * hash do nome usada em `AgentAvatar`). 100% local, sem rede, respeita o CSP.
 */
function ProfileAvatar({ profile, size = 36 }: { profile: Profile; size?: number }) {
  const name = profile.displayName;
  if (profile.avatarUrl) {
    return (
      <img
        src={profile.avatarUrl}
        alt=""
        aria-hidden="true"
        className="shrink-0 rounded-full object-cover"
        style={{ width: size, height: size }}
      />
    );
  }
  const colors = avatarColorsFor(name);
  return (
    <span
      aria-hidden="true"
      className="inline-flex shrink-0 select-none items-center justify-center rounded-full font-heading font-semibold leading-none"
      style={{
        width: size,
        height: size,
        backgroundColor: colors.background,
        color: colors.foreground,
        fontSize: Math.round(size * 0.4),
      }}
    >
      {initialsFor(name)}
    </span>
  );
}

export interface ProfileMenuProps {
  /**
   * `collapsed`: só o avatar (sidebar recolhida). `expanded`: avatar + nome.
   * O menu suspenso é idêntico nos dois modos.
   */
  variant?: 'expanded' | 'collapsed';
  /** Lado em que o menu abre em relação ao gatilho. */
  placement?: 'top' | 'bottom';
  className?: string;
}

/**
 * G-PROFILE — perfil logado no shell. Resolve o perfil ativo da sessão local
 * (`getCurrentProfile` / `LocalProfileSession`) e o exibe com avatar + nome.
 * O menu oferece os dados básicos, trocar de perfil e sair.
 */
export function ProfileMenu({
  variant = 'expanded',
  placement = 'top',
  className,
}: ProfileMenuProps) {
  const { t } = useTranslation();
  const api = useApi();
  const navigate = useNavigate();
  const activeProfileId = useSessionStore((s) => s.activeProfileId);
  const setActiveProfile = useSessionStore((s) => s.setActiveProfile);

  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const menuId = useId();

  const profileQuery = useQuery({
    queryKey: profileKeys.current(activeProfileId ?? 'none'),
    queryFn: () => api.getCurrentProfile(),
  });
  const profile = profileQuery.data;

  // Fecha ao clicar fora ou apertar Escape.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  if (!profile) {
    // Enquanto carrega (ou sem sessão): placeholder discreto, sem quebrar o layout.
    return (
      <div
        className={cn(
          'flex items-center gap-3 rounded-md px-2 py-2',
          variant === 'collapsed' && 'justify-center px-0',
          className,
        )}
        aria-hidden="true"
      >
        <span className="size-9 shrink-0 animate-pulse rounded-full bg-surface-elevated" />
        {variant === 'expanded' && (
          <span className="h-4 w-24 animate-pulse rounded bg-surface-elevated" />
        )}
      </div>
    );
  }

  const collapsed = variant === 'collapsed';

  function switchProfile() {
    setOpen(false);
    navigate('/onboarding');
  }

  function signOut() {
    setOpen(false);
    setActiveProfile(null);
    navigate('/onboarding', { replace: true });
  }

  return (
    <div ref={containerRef} className={cn('relative', className)}>
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={menuId}
        aria-label={t('shell.profile.menuLabel', { name: profile.displayName })}
        className={cn(
          'flex min-h-touch w-full items-center gap-3 rounded-md px-2 py-2 text-start',
          'text-foreground motion-safe:transition-colors motion-safe:duration-fast',
          'hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
          collapsed && 'justify-center px-0',
        )}
      >
        <ProfileAvatar profile={profile} size={36} />
        {!collapsed && (
          <>
            <span className="flex min-w-0 flex-1 flex-col">
              <span className="truncate text-sm font-medium">{profile.displayName}</span>
              {profile.email && (
                <span className="truncate text-xs text-foreground-muted">{profile.email}</span>
              )}
            </span>
            <ChevronsUpDown className="size-4 shrink-0 text-foreground-muted" aria-hidden="true" />
          </>
        )}
      </button>

      {open && (
        <div
          id={menuId}
          role="menu"
          aria-label={t('shell.profile.menuLabel', { name: profile.displayName })}
          className={cn(
            'absolute z-50 min-w-56 rounded-md border border-border bg-surface p-1 shadow-lg',
            collapsed ? 'left-0' : 'inset-x-0',
            placement === 'top' ? 'bottom-full mb-2' : 'top-full mt-2',
          )}
        >
          <div className="flex items-center gap-3 px-2 py-2">
            <ProfileAvatar profile={profile} size={40} />
            <span className="flex min-w-0 flex-col">
              <span className="truncate text-sm font-medium">{profile.displayName}</span>
              <span className="truncate text-xs text-foreground-muted">
                {profile.email ?? t('shell.profile.noEmail')}
              </span>
            </span>
          </div>
          <div className="my-1 h-px bg-border" aria-hidden="true" />
          <button
            type="button"
            role="menuitem"
            onClick={switchProfile}
            className="flex min-h-touch w-full items-center gap-3 rounded-md px-2 py-2 text-start text-sm text-foreground hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            <UserRoundCog className="size-4 shrink-0 text-foreground-muted" aria-hidden="true" />
            {t('shell.profile.switch')}
          </button>
          <button
            type="button"
            role="menuitem"
            onClick={signOut}
            className="flex min-h-touch w-full items-center gap-3 rounded-md px-2 py-2 text-start text-sm text-error hover:bg-error/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            <LogOut className="size-4 shrink-0" aria-hidden="true" />
            {t('shell.profile.signOut')}
          </button>
        </div>
      )}
    </div>
  );
}
