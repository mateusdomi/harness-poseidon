import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { AgentAvatar } from '@/features/shared/components/agent-avatar';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { cn } from '@/lib/utils';

const BRUNA_NAME = 'Bruna Magalhães';
const BRUNA_PHOTO = '/people/bruna-magalhaes.jpg';

export function BrunaProfileAvatar({
  size = 40,
  className,
}: {
  size?: number;
  className?: string;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [failed, setFailed] = useState(false);

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={t('chat.leadership.openProfile')}
        className={cn(
          'shrink-0 rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-background',
          className,
        )}
      >
        {failed ? (
          <AgentAvatar name={BRUNA_NAME} size={size} />
        ) : (
          <img
            src={BRUNA_PHOTO}
            alt={t('chat.leadership.photoAlt')}
            width={size}
            height={size}
            onError={() => setFailed(true)}
            className="rounded-full object-cover"
            style={{ width: size, height: size }}
          />
        )}
      </button>
      {open && (
        <ModalDialog
          label={t('chat.leadership.profileLabel')}
          onClose={() => setOpen(false)}
          className="max-w-xl"
        >
          <div className="flex flex-col items-center gap-4 pt-8 text-center">
            {failed ? (
              <AgentAvatar name={BRUNA_NAME} size={128} />
            ) : (
              <img
                src={BRUNA_PHOTO}
                alt={t('chat.leadership.photoAlt')}
                width={682}
                height={1024}
                onError={() => setFailed(true)}
                className="max-h-[60vh] w-auto max-w-full rounded-xl object-contain"
              />
            )}
            <div>
              <h2 className="font-heading text-2xl font-semibold">{BRUNA_NAME}</h2>
              <p className="mt-1 text-sm text-foreground-muted">
                {t('chat.leadership.title')}
              </p>
            </div>
          </div>
        </ModalDialog>
      )}
    </>
  );
}
