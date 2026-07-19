import { useTranslation } from 'react-i18next';
import { ClipboardCheck, ListChecks, OctagonAlert, Route, Sparkles } from 'lucide-react';

import type { QuickActionKey } from '@/features/chat/lib/chat-derive';

interface QuickActionsProps {
  actions: QuickActionKey[];
  disabled: boolean;
  onSelect: (actionKey: QuickActionKey) => void;
}

/** Ícone contextual de cada ação rápida (lucide). */
const ACTION_ICONS = {
  summarizeProgress: ListChecks,
  blockedStatus: OctagonAlert,
  approvalStatus: ClipboardCheck,
  planNewDemand: Route,
} as const;

/**
 * Ações rápidas sugeridas pelo chefe (derivadas do contexto do projeto).
 * Cada chip envia a mensagem estruturada correspondente ao chat.
 */
export function QuickActions({ actions, disabled, onSelect }: QuickActionsProps) {
  const { t } = useTranslation();
  if (actions.length === 0) return null;

  return (
    <div className="flex flex-col gap-2">
      <span className="flex items-center gap-1.5 text-xs text-foreground-muted">
        <Sparkles aria-hidden="true" className="size-3.5 text-brand-strong" />
        {t('chat.quickActions.title')}
      </span>
      <ul className="flex flex-wrap gap-2">
        {actions.map((actionKey) => {
          const Icon = ACTION_ICONS[actionKey];
          return (
            <li key={actionKey}>
              <button
                type="button"
                disabled={disabled}
                onClick={() => onSelect(actionKey)}
                className="inline-flex min-h-touch items-center gap-1.5 rounded-full border border-border bg-surface px-3 py-1.5 text-xs font-medium text-foreground motion-safe:transition-colors motion-safe:duration-fast hover:border-border-strong hover:bg-surface-elevated active:bg-background disabled:pointer-events-none disabled:opacity-50"
              >
                <Icon aria-hidden="true" className="size-3.5 text-brand-strong" />
                {t(`chat.quickActions.actions.${actionKey}.label`)}
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
