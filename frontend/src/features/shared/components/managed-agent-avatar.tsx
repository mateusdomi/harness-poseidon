import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';

import { apiMode } from '@/config/features';
import { AgentAvatar } from '@/features/shared/components/agent-avatar';
import {
  useAgentPhotoRevision,
  useLeadershipProfile,
} from '@/features/shared/hooks/use-leadership-profile';
import {
  canonicalPhotoAlias,
  isLeadershipAlias,
  versionedPhotoUrl,
} from '@/features/shared/lib/agent-photo';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { cn } from '@/lib/utils';

interface PhotoLightboxProps {
  name: string;
  roleLabel: string | null;
  summary?: string | null;
  leadership?: boolean;
  photoUrl: string | null;
  failed: boolean;
  onPhotoError: () => void;
  onClose: () => void;
}

function PhotoLightbox({
  name,
  roleLabel,
  summary,
  leadership = false,
  photoUrl,
  failed,
  onPhotoError,
  onClose,
}: PhotoLightboxProps) {
  const { t } = useTranslation();
  const panelRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    closeRef.current?.focus();

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopImmediatePropagation();
        onClose();
        return;
      }
      if (event.key !== 'Tab' || !panelRef.current) return;
      const focusable = [
        ...panelRef.current.querySelectorAll<HTMLElement>('button, [tabindex]'),
      ].filter((element) => element.tabIndex >= 0);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable.at(-1)!;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('keydown', onKeyDown, true);
      previous?.focus();
    };
  }, [onClose]);

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <button
        type="button"
        tabIndex={-1}
        aria-label={t('common.actions.cancel')}
        onClick={onClose}
        className="absolute inset-0 cursor-default bg-background/85 backdrop-blur-sm"
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={
          leadership ? t('chat.leadership.profileLabel') : t('photoEditor.profileLabel', { name })
        }
        tabIndex={-1}
        className="relative flex max-h-[90vh] w-full max-w-2xl flex-col items-center gap-4 overflow-y-auto rounded-xl border border-border bg-background p-5 text-center shadow-2xl md:p-7"
      >
        <button
          ref={closeRef}
          type="button"
          onClick={onClose}
          aria-label={t('common.actions.cancel')}
          className="absolute right-3 top-3 flex size-11 items-center justify-center rounded-md text-foreground-muted hover:bg-surface-elevated hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <X aria-hidden="true" className="size-5" />
        </button>
        <div className="flex min-h-64 w-full items-center justify-center pt-8">
          {photoUrl && !failed ? (
            <img
              src={photoUrl}
              alt={t('photoEditor.photoAlt', { name })}
              onError={onPhotoError}
              className="max-h-[62vh] w-auto max-w-full rounded-xl object-contain shadow-card"
            />
          ) : (
            <AgentAvatar name={name} size={176} />
          )}
        </div>
        <div>
          <h2 className="font-heading text-2xl font-semibold">{name}</h2>
          {roleLabel ? <p className="mt-1 text-sm text-foreground-muted">{roleLabel}</p> : null}
          {summary ? (
            <p className="mx-auto mt-3 max-w-lg text-sm text-foreground-muted">{summary}</p>
          ) : null}
        </div>
      </div>
    </div>,
    document.body,
  );
}

export interface ManagedAgentAvatarProps {
  alias: string;
  fallbackName?: string | null;
  roleLabel?: string | null;
  size?: number;
  className?: string;
}

/** Avatar gerenciado, ampliável e consistente entre persona e conta de execução. */
export function ManagedAgentAvatar({
  alias,
  fallbackName,
  roleLabel,
  size = 40,
  className,
}: ManagedAgentAvatarProps) {
  const { t } = useTranslation();
  const leadershipProfile = useLeadershipProfile().data;
  const photoAlias = canonicalPhotoAlias(alias);
  const photoRevision = useAgentPhotoRevision(photoAlias);
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  const identity = resolveAgentIdentity(alias, fallbackName);
  const leadership = isLeadershipAlias(alias);
  const name = leadership && leadershipProfile ? leadershipProfile.displayName : identity.humanName;
  const publicRole =
    leadership && leadershipProfile ? leadershipProfile.title : (roleLabel ?? identity.roleLabel);
  const photoUrl = useMemo(() => {
    if (leadership) {
      const base = leadershipProfile?.photoUrl ?? '/people/bruna-magalhaes.jpg';
      return versionedPhotoUrl(base, leadershipProfile?.version ?? 0);
    }
    if (apiMode !== 'http') return null;
    return `/api/v1/leadership-profile/agents/${encodeURIComponent(photoAlias)}/photo?v=${photoRevision}`;
  }, [
    leadership,
    leadershipProfile?.photoUrl,
    leadershipProfile?.version,
    photoAlias,
    photoRevision,
  ]);

  useEffect(() => setFailed(false), [photoUrl]);

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={leadership ? t('chat.leadership.openProfile') : t('photoEditor.open', { name })}
        className={cn(
          'shrink-0 rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-background',
          className,
        )}
      >
        {photoUrl && !failed ? (
          <img
            src={photoUrl}
            alt={t('photoEditor.photoAlt', { name })}
            width={size}
            height={size}
            onError={() => setFailed(true)}
            className="rounded-full object-cover"
            style={{ width: size, height: size }}
          />
        ) : (
          <AgentAvatar name={name} size={size} />
        )}
      </button>
      {open ? (
        <PhotoLightbox
          name={name}
          roleLabel={publicRole}
          summary={leadership ? leadershipProfile?.summary : null}
          leadership={leadership}
          photoUrl={photoUrl}
          failed={failed}
          onPhotoError={() => setFailed(true)}
          onClose={() => setOpen(false)}
        />
      ) : null}
    </>
  );
}
