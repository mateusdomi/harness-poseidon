import { useEffect, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';

import { cn } from '@/lib/utils';

const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface ModalDialogProps {
  /** Título acessível do dialog (aria-label). */
  label: string;
  onClose: () => void;
  children: ReactNode;
  className?: string;
}

/**
 * Dialog modal centrado compartilhado: role="dialog" modal, foco preso
 * no painel (Tab faz loop), Esc fecha, backdrop fecha e o foco retorna
 * a quem abriu. Mesmo contrato de a11y do TaskDrawer (D-022).
 */
export function ModalDialog({ label, onClose, children, className }: ModalDialogProps) {
  const { t } = useTranslation();
  const panelRef = useRef<HTMLDivElement>(null);
  // onClose em ref: o efeito roda UMA vez na montagem — se dependesse da
  // identidade do callback, cada re-render (ex.: digitar no formulário)
  // roubaria o foco de volta para o gatilho.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    const previouslyFocused =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    (panel.querySelector<HTMLElement>(FOCUSABLE) ?? panel).focus();

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== 'Tab' || !panel) return;
      const focusable = [...panel.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
        (element) => element.offsetParent !== null,
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center p-4">
      <button
        type="button"
        tabIndex={-1}
        aria-hidden="true"
        aria-label={t('common.actions.cancel')}
        onClick={onClose}
        className="absolute inset-0 cursor-default bg-background/70"
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        tabIndex={-1}
        className={cn(
          'relative flex max-h-[85vh] w-full max-w-lg flex-col gap-4 overflow-y-auto rounded-xl border border-border bg-background p-4 shadow-2xl sm:p-6',
          className,
        )}
      >
        <button
          type="button"
          onClick={onClose}
          aria-label={t('common.actions.cancel')}
          className="absolute right-3 top-3 flex size-11 items-center justify-center rounded-md text-foreground-muted transition-colors hover:bg-surface-elevated hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <X aria-hidden="true" className="size-5" />
        </button>
        {children}
      </div>
    </div>
  );
}
