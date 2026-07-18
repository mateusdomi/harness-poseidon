import { useTranslation } from 'react-i18next';
import { WifiOff } from 'lucide-react';

import { cn } from '@/lib/utils';
import { useConnectionState } from '@/features/shared/hooks/use-connection-state';

/**
 * Banner global de reconexão: visível sempre que o realtime não está
 * `connected`. Renderizado uma única vez no AppShell (todas as telas).
 * Não é dismissível — some sozinho ao reconectar.
 */
export function ReconnectionBanner({ className }: { className?: string }) {
  const { t } = useTranslation();
  const state = useConnectionState();

  if (state === 'connected') return null;

  return (
    <div
      role="status"
      className={cn(
        'flex min-h-touch items-center justify-center gap-2 border-b px-4 py-2 text-sm font-medium',
        state === 'reconnecting'
          ? 'border-border bg-warning/15 text-warning'
          : 'border-border bg-error/15 text-error',
        className,
      )}
    >
      <WifiOff aria-hidden="true" className="size-4 shrink-0" />
      <span>{t(`shared.reconnection.${state}`)}</span>
    </div>
  );
}
