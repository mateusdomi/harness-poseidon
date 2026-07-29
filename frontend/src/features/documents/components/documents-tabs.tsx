import { useTranslation } from 'react-i18next';

import { Select } from '@/design-system';
import { cn } from '@/lib/utils';
import {
  documentPanelId,
  documentTabId,
  type DocumentTabId,
} from '@/features/documents/lib/documents-tabs';

export interface DocumentsTabsProps {
  tabs: readonly { id: DocumentTabId; label: string; count: number }[];
  activeId: DocumentTabId;
  onChange: (id: DocumentTabId) => void;
}

/**
 * Abas da tela de Documentos com `role=tablist` acessível (setas navegam,
 * roving tabindex). Abaixo de `md` viram um Select — os painéis são os mesmos.
 *
 * O prefixo é `md` de propósito: este tema declara apenas md/lg/xl, então `sm:`
 * não gera CSS nenhum. É o defeito que mantém a lista de abas do catálogo de
 * Ferramentas invisível em QUALQUER largura (registrado no quadro para a F11).
 */
export function DocumentsTabs({ tabs, activeId, onChange }: DocumentsTabsProps) {
  const { t } = useTranslation();
  const activeIndex = tabs.findIndex((tab) => tab.id === activeId);

  function handleKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    let nextIndex: number | null = null;
    if (event.key === 'ArrowRight') nextIndex = (activeIndex + 1) % tabs.length;
    else if (event.key === 'ArrowLeft') nextIndex = (activeIndex - 1 + tabs.length) % tabs.length;
    else if (event.key === 'Home') nextIndex = 0;
    else if (event.key === 'End') nextIndex = tabs.length - 1;
    if (nextIndex === null) return;
    event.preventDefault();
    onChange(tabs[nextIndex].id);
    document.getElementById(documentTabId(tabs[nextIndex].id))?.focus();
  }

  return (
    <>
      <Select
        className="md:hidden"
        aria-label={t('documents.tabs.label')}
        value={activeId}
        onChange={(event) => onChange(event.target.value as DocumentTabId)}
      >
        {tabs.map((tab) => (
          <option key={tab.id} value={tab.id}>
            {tab.label} ({tab.count})
          </option>
        ))}
      </Select>

      <div
        role="tablist"
        aria-label={t('documents.tabs.label')}
        className="hidden flex-wrap gap-1 md:flex"
        onKeyDown={handleKeyDown}
      >
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            role="tab"
            id={documentTabId(tab.id)}
            aria-selected={tab.id === activeId}
            aria-controls={documentPanelId(tab.id)}
            tabIndex={tab.id === activeId ? 0 : -1}
            onClick={() => onChange(tab.id)}
            className={cn(
              'flex min-h-11 items-center gap-2 rounded-md px-4 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
              tab.id === activeId
                ? 'bg-surface-elevated font-semibold text-foreground shadow-sm ring-1 ring-inset ring-border-strong'
                : 'font-medium text-foreground-muted hover:bg-surface hover:text-foreground',
            )}
          >
            {tab.label}
            <span className="text-xs text-foreground-muted">{tab.count}</span>
          </button>
        ))}
      </div>
    </>
  );
}
