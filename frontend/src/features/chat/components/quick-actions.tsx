import { useTranslation } from 'react-i18next';
import { ClipboardCheck, ListChecks, OctagonAlert, Play, Route, Sparkles } from 'lucide-react';

import { Button } from '@/design-system';
import type { QuickActionKey } from '@/features/chat/lib/chat-derive';
import { cn } from '@/lib/utils';

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
  resumeProject: Play,
  planNewDemand: Route,
} as const;

const ACTION_TONES: Record<QuickActionKey, string> = {
  summarizeProgress: 'text-info',
  blockedStatus: 'text-warning',
  approvalStatus: 'text-warning',
  resumeProject: 'text-success',
  planNewDemand: 'text-brand-strong',
};

/**
 * Ações rápidas sugeridas pelo chefe (derivadas do contexto do projeto).
 * Cada chip envia a mensagem estruturada correspondente ao chat.
 */
export function QuickActions({ actions, disabled, onSelect }: QuickActionsProps) {
  const { t } = useTranslation();
  if (actions.length === 0) return null;

  return (
    <ul
      className="flex flex-wrap items-center gap-x-3 gap-y-2"
      aria-label={t('chat.quickActions.title')}
    >
      <li
        aria-hidden="true"
        className="flex items-center gap-1.5 text-xs text-foreground-muted"
      >
        <Sparkles aria-hidden="true" className="size-3.5 text-brand-strong" />
        {t('chat.quickActions.title')}
      </li>
      {actions.map((actionKey) => {
        const Icon = ACTION_ICONS[actionKey];
        return (
          <li key={actionKey}>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={disabled}
              onClick={() => onSelect(actionKey)}
              className="rounded-full bg-surface"
            >
              <Icon
                aria-hidden="true"
                className={cn('size-3.5', ACTION_TONES[actionKey])}
              />
              {t(`chat.quickActions.actions.${actionKey}.label`)}
            </Button>
          </li>
        );
      })}
    </ul>
  );
}
