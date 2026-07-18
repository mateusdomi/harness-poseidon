import { useTranslation } from 'react-i18next';

import { Select } from '@/design-system';
import { cn } from '@/lib/utils';
import { catalogPanelId, catalogTabId, type CatalogTabId } from '@/features/tools/lib/tools-derive';

export interface CatalogTabsProps {
  tabs: readonly { id: CatalogTabId; label: string; count: number }[];
  activeId: CatalogTabId;
  onChange: (id: CatalogTabId) => void;
}

/**
 * Abas do catálogo com role=tablist acessível (setas esquerda/direita
 * navegam, roving tabindex). No mobile (<sm) viram um Select — os
 * painéis permanecem os mesmos.
 */
export function CatalogTabs({ tabs, activeId, onChange }: CatalogTabsProps) {
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
    document.getElementById(catalogTabId(tabs[nextIndex].id))?.focus();
  }

  return (
    <>
      {/* Mobile: Select ocupa a largura toda e tem alvo de toque ≥44px. */}
      <Select
        className="sm:hidden"
        aria-label={t('tools.tabs.label')}
        value={activeId}
        onChange={(event) => onChange(event.target.value as CatalogTabId)}
      >
        {tabs.map((tab) => (
          <option key={tab.id} value={tab.id}>
            {tab.label} ({tab.count})
          </option>
        ))}
      </Select>

      <div
        role="tablist"
        aria-label={t('tools.tabs.label')}
        className="hidden flex-wrap gap-1 sm:flex"
        onKeyDown={handleKeyDown}
      >
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            role="tab"
            id={catalogTabId(tab.id)}
            aria-selected={tab.id === activeId}
            aria-controls={catalogPanelId(tab.id)}
            tabIndex={tab.id === activeId ? 0 : -1}
            onClick={() => onChange(tab.id)}
            className={cn(
              'flex min-h-11 items-center gap-2 rounded-md px-4 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
              tab.id === activeId
                ? 'bg-surface-elevated text-foreground'
                : 'text-foreground-muted hover:bg-surface hover:text-foreground',
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
