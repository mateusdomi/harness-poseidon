import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';

import type { Agent, Ulid } from '@/api';
import { TaskDetail } from '@/features/board/components/task-detail';

const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface TaskDrawerProps {
  taskId: Ulid;
  agents: Agent[];
  showTechnicalDetails: boolean;
  onClose: () => void;
}

/**
 * Drawer lateral do detalhe da tarefa (desktop, lg+): role="dialog"
 * modal, foco preso no painel, Esc fecha, clique no backdrop fecha e o
 * foco volta para quem abriu. No mobile a página usa a view dedicada.
 */
export function TaskDrawer({
  taskId,
  agents,
  showTechnicalDetails,
  onClose,
}: TaskDrawerProps) {
  const { t } = useTranslation();
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    const previouslyFocused = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    (panel.querySelector<HTMLElement>(FOCUSABLE) ?? panel).focus();

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
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
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-40">
      <button
        type="button"
        tabIndex={-1}
        aria-label={t('board.detail.close')}
        onClick={onClose}
        className="absolute inset-0 cursor-default bg-background/70"
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={t('board.detail.dialogLabel')}
        tabIndex={-1}
        className="absolute bottom-0 right-0 top-16 flex w-full max-w-xl flex-col overflow-y-auto border-l border-t border-border bg-background p-4 shadow-2xl"
      >
        <TaskDetail
          taskId={taskId}
          agents={agents}
          showTechnicalDetails={showTechnicalDetails}
          onClose={onClose}
        />
      </div>
    </div>
  );
}
