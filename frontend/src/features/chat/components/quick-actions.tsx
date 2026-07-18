import { useTranslation } from 'react-i18next';
import { Sparkles } from 'lucide-react';

import { Button } from '@/design-system';
import type { QuickActionKey } from '@/features/chat/lib/chat-derive';

interface QuickActionsProps {
  actions: QuickActionKey[];
  disabled: boolean;
  onSelect: (actionKey: QuickActionKey) => void;
}

/**
 * Ações rápidas sugeridas pelo chefe (derivadas do contexto do projeto).
 * Cada botão envia a mensagem estruturada correspondente ao chat.
 */
export function QuickActions({ actions, disabled, onSelect }: QuickActionsProps) {
  const { t } = useTranslation();
  if (actions.length === 0) return null;

  return (
    <div className="flex flex-col gap-2">
      <span className="flex items-center gap-1.5 text-xs text-foreground-muted">
        <Sparkles aria-hidden="true" className="size-3.5" />
        {t('chat.quickActions.title')}
      </span>
      <ul className="flex flex-wrap gap-2">
        {actions.map((actionKey) => (
          <li key={actionKey}>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={disabled}
              onClick={() => onSelect(actionKey)}
            >
              {t(`chat.quickActions.actions.${actionKey}.label`)}
            </Button>
          </li>
        ))}
      </ul>
    </div>
  );
}
