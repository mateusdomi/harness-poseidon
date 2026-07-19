import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Search } from 'lucide-react';

import { NAV_GROUPS } from '@/app/navigation';
import {
  matchPaletteEntries,
  normalizeSearchText,
  type PaletteEntry,
} from '@/app/lib/command-search';
import { Button } from '@/design-system';
import { cn } from '@/lib/utils';
import { ModalDialog } from '@/features/shared/components/modal-dialog';

/** Detecta macOS/iOS para exibir o hint do atalho (⌘K vs Ctrl+K). */
function useIsMacPlatform(): boolean {
  return useMemo(() => {
    if (typeof navigator === 'undefined') return false;
    return /mac|iphone|ipad|ipod/i.test(navigator.platform || navigator.userAgent);
  }, []);
}

/**
 * Busca global de TELAS (command palette): abre com ⌘K (macOS) / Ctrl+K
 * (demais) ou pelo botão de lupa no header. Apenas módulos/telas — nenhum
 * conteúdo sensível é indexado. Teclado: setas movem, Enter navega, Esc fecha.
 */
export function CommandPalette() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const isMac = useIsMacPlatform();
  const shortcutHint = isMac ? '⌘K' : 'Ctrl+K';

  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [activeIndex, setActiveIndex] = useState(0);
  const listRef = useRef<HTMLUListElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Foco no campo de busca ao abrir (roda depois do efeito do ModalDialog,
  // que foca o primeiro elemento focável do painel).
  useEffect(() => {
    if (open) inputRef.current?.focus();
  }, [open]);

  // Atalho global: ⌘K / Ctrl+K alterna a paleta.
  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLocaleLowerCase() === 'k') {
        event.preventDefault();
        setOpen((current) => !current);
      }
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, []);

  // Entradas com metadados i18n (nome + palavras-chave), reconstruídas ao trocar o idioma.
  const entries = useMemo(
    () =>
      NAV_GROUPS.flatMap((group) =>
        group.items.map((item) => {
          const label = t(`nav.${item.key}`);
          const name = normalizeSearchText(label);
          const keywords = normalizeSearchText(t(`nav.keywords.${item.key}`));
          return { item, groupKey: group.key, label, name, haystack: `${name} ${keywords}` };
        }),
      ),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [t, i18n.language],
  );

  const results = useMemo(() => matchPaletteEntries(entries, query), [entries, query]);

  // Mantém o item ativo visível no scroll da lista.
  useEffect(() => {
    const selected = listRef.current?.querySelector('[aria-selected="true"]');
    selected?.scrollIntoView?.({ block: 'nearest' });
  }, [activeIndex]);

  function close() {
    setOpen(false);
    setQuery('');
    setActiveIndex(0);
  }

  function select(entry: PaletteEntry) {
    close();
    navigate(entry.item.path);
  }

  function handleInputKeyDown(event: React.KeyboardEvent) {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActiveIndex((index) => (results.length === 0 ? 0 : (index + 1) % results.length));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActiveIndex((index) =>
        results.length === 0 ? 0 : (index - 1 + results.length) % results.length,
      );
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const entry = results[activeIndex] ?? results[0];
      if (entry) select(entry);
    }
  }

  // Agrupa os resultados mantendo a ordem dos grupos do menu.
  const grouped = useMemo(() => {
    const byGroup = new Map<string, { entry: PaletteEntry; index: number }[]>();
    results.forEach((entry, index) => {
      const list = byGroup.get(entry.groupKey) ?? [];
      list.push({ entry, index });
      byGroup.set(entry.groupKey, list);
    });
    return [...byGroup.entries()];
  }, [results]);

  return (
    <>
      <Button
        type="button"
        variant="ghost"
        size="icon"
        onClick={() => setOpen(true)}
        aria-label={t('shell.search.open', { shortcut: shortcutHint })}
        title={t('shell.search.open', { shortcut: shortcutHint })}
      >
        <Search aria-hidden="true" />
      </Button>

      {open && (
        <ModalDialog
          label={t('shell.search.label')}
          onClose={close}
          className="max-w-xl gap-3 self-start mt-[12vh] p-4 sm:p-4"
        >
          <div className="flex items-center gap-2">
            <Search aria-hidden="true" className="size-4 shrink-0 text-foreground-muted" />
            <label htmlFor="command-palette-input" className="sr-only">
              {t('shell.search.label')}
            </label>
            <input
              id="command-palette-input"
              ref={inputRef}
              type="search"
              role="combobox"
              aria-expanded="true"
              aria-controls="command-palette-listbox"
              aria-activedescendant={
                results.length > 0 ? `command-palette-option-${activeIndex}` : undefined
              }
              value={query}
              placeholder={t('shell.search.placeholder')}
              onChange={(event) => {
                setQuery(event.target.value);
                setActiveIndex(0);
              }}
              onKeyDown={handleInputKeyDown}
              autoComplete="off"
              className="h-11 w-full rounded-md border border-border-strong bg-surface px-3 text-sm text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
            />
            <kbd className="hidden shrink-0 rounded border border-border bg-surface-elevated px-1.5 py-0.5 text-xs text-foreground-muted sm:inline">
              {shortcutHint}
            </kbd>
          </div>

          {results.length === 0 ? (
            <p className="px-1 py-4 text-sm text-foreground-muted">
              {t('shell.search.noResults', { query })}
            </p>
          ) : (
            <ul
              id="command-palette-listbox"
              ref={listRef}
              role="listbox"
              aria-label={t('shell.search.label')}
              className="flex max-h-[50vh] flex-col gap-1 overflow-y-auto"
            >
              {grouped.map(([groupKey, items]) => (
                <li key={groupKey} role="presentation">
                  <span
                    aria-hidden="true"
                    className="block px-3 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wider text-foreground-muted"
                  >
                    {t(`nav.groups.${groupKey}`)}
                  </span>
                  <ul role="presentation" className="flex flex-col gap-1">
                    {items.map(({ entry, index }) => (
                      <li
                        key={entry.item.key}
                        id={`command-palette-option-${index}`}
                        role="option"
                        aria-selected={index === activeIndex}
                        className={cn(
                          'flex min-h-11 cursor-pointer items-center gap-3 rounded-md px-3 text-sm',
                          index === activeIndex
                            ? 'bg-brand/10 font-semibold text-brand-strong'
                            : 'text-foreground hover:bg-surface-elevated',
                        )}
                        onMouseEnter={() => setActiveIndex(index)}
                        onClick={() => select(entry)}
                      >
                        <entry.item.icon aria-hidden="true" className="size-4 shrink-0" />
                        {entry.label}
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          )}
        </ModalDialog>
      )}
    </>
  );
}
