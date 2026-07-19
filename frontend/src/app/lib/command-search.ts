import type { NavItem } from '@/app/navigation';

/** Normalização da busca: minúsculas + remoção de acentos (NFD). */
export function normalizeSearchText(text: string): string {
  return text
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLocaleLowerCase();
}

export interface PaletteEntry {
  item: NavItem;
  groupKey: string;
  label: string;
}

export interface SearchablePaletteEntry extends PaletteEntry {
  /** Nome da tela normalizado (para ranking). */
  name: string;
  /** Nome + palavras-chave normalizados (para o match). */
  haystack: string;
}

/**
 * Fuzzy search local, sem dependência: casa TODOS os tokens da consulta
 * como substring do nome da tela + palavras-chave i18n (normalizados).
 * Ranking: prefixo do nome > substring do nome > palavra-chave.
 */
export function matchPaletteEntries(
  entries: SearchablePaletteEntry[],
  query: string,
): PaletteEntry[] {
  const tokens = normalizeSearchText(query).split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return entries;

  const ranked = entries
    .filter((entry) => tokens.every((token) => entry.haystack.includes(token)))
    .map((entry) => {
      const first = tokens[0];
      const rank = entry.name.startsWith(first) ? 0 : entry.name.includes(first) ? 1 : 2;
      return { entry, rank };
    });

  return ranked
    .sort((a, b) => a.rank - b.rank)
    .map(({ entry }) => ({ item: entry.item, groupKey: entry.groupKey, label: entry.label }));
}
