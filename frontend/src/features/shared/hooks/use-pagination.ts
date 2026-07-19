import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';

/** Opções de itens por página (padrão: 15). */
export const PAGE_SIZE_OPTIONS = [15, 30, 50] as const;
export const DEFAULT_PAGE_SIZE: (typeof PAGE_SIZE_OPTIONS)[number] = 15;

export interface UsePaginationOptions {
  /** Tamanho inicial da página (padrão 15). */
  pageSize?: number;
  /**
   * Quando muda (filtros, busca, projeto, aba…), a paginação volta para a
   * página 1. Compare por valor/identidade estável (ex.: JSON dos filtros).
   */
  resetKey?: unknown;
}

export interface Pagination {
  /** Página atual (1-based, sempre dentro de 1..pageCount). */
  page: number;
  pageSize: number;
  pageCount: number;
  total: number;
  /** Primeiro item exibido (1-based; 0 quando a coleção está vazia). */
  rangeStart: number;
  /** Último item exibido (1-based; 0 quando vazia). */
  rangeEnd: number;
  setPage: (page: number) => void;
  setPageSize: (pageSize: number) => void;
  /** Fatia client-side da coleção já filtrada/ordenada. */
  paginate: <T>(items: T[]) => T[];
}

function clampPage(page: number, pageCount: number): number {
  return Math.min(Math.max(1, page), pageCount);
}

function buildPagination(
  total: number,
  page: number,
  pageSize: number,
  setPage: (page: number) => void,
  setPageSize: (pageSize: number) => void,
): Pagination {
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  return {
    page,
    pageSize,
    pageCount,
    total,
    rangeStart: total === 0 ? 0 : (page - 1) * pageSize + 1,
    rangeEnd: Math.min(total, page * pageSize),
    setPage,
    setPageSize,
    paginate: <T,>(items: T[]): T[] => items.slice((page - 1) * pageSize, page * pageSize),
  };
}

/**
 * Paginação client-side sobre coleções já carregadas (mock; o contrato
 * cursor existe na camada api, mas os hooks de dados NÃO mudam — D-063).
 * Reset automático para a página 1 quando `resetKey` muda e clamp quando
 * o total encolhe (ex.: filtro/realtime). NÃO depende de Router — para
 * sincronizar `?page=`/`?pageSize=` na URL, use `useUrlPagination`.
 */
export function usePagination(total: number, options: UsePaginationOptions = {}): Pagination {
  const { pageSize: initialPageSize = DEFAULT_PAGE_SIZE, resetKey } = options;

  const [state, setState] = useState({ page: 1, pageSize: initialPageSize });

  const pageCount = Math.max(1, Math.ceil(total / state.pageSize));
  const page = clampPage(state.page, pageCount);

  // Reset para a página 1 ao alterar filtro/busca/aba (resetKey).
  useEffect(() => {
    setState((previous) => (previous.page === 1 ? previous : { ...previous, page: 1 }));
  }, [resetKey]);

  // Total encolheu (filtro/realtime): clampa a página corrente.
  useEffect(() => {
    setState((previous) =>
      previous.page > pageCount ? { ...previous, page: pageCount } : previous,
    );
  }, [pageCount]);

  const setPage = useCallback((nextPage: number) => {
    setState((previous) => ({ ...previous, page: Math.max(1, nextPage) }));
  }, []);

  const setPageSize = useCallback((nextPageSize: number) => {
    setState({ page: 1, pageSize: nextPageSize });
  }, []);

  return useMemo(
    () => buildPagination(total, page, state.pageSize, setPage, setPageSize),
    [total, page, state.pageSize, setPage, setPageSize],
  );
}

/**
 * Variante do `usePagination` com estado na URL (`?page=`/`?pageSize=`),
 * preservando os demais params (ex.: `?doc=` em documentos). Requer
 * contexto de Router. Valores no padrão (1/15) são omitidos da URL.
 */
export function useUrlPagination(total: number, options: UsePaginationOptions = {}): Pagination {
  const { pageSize: initialPageSize = DEFAULT_PAGE_SIZE, resetKey } = options;

  const [searchParams, setSearchParams] = useSearchParams();

  const pageSize = Number(searchParams.get('pageSize')) || initialPageSize;
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const page = clampPage(Number(searchParams.get('page')) || 1, pageCount);

  const write = useCallback(
    (next: { page: number; pageSize: number }) => {
      setSearchParams(
        (previous) => {
          const params = new URLSearchParams(previous);
          if (next.page > 1) params.set('page', String(next.page));
          else params.delete('page');
          if (next.pageSize !== DEFAULT_PAGE_SIZE) params.set('pageSize', String(next.pageSize));
          else params.delete('pageSize');
          return params;
        },
        { preventScrollReset: true },
      );
    },
    [setSearchParams],
  );

  // Reset para a página 1 ao alterar filtro/busca (resetKey) ou quando o
  // total encolhe e a página corrente deixa de existir.
  useEffect(() => {
    if (page !== 1) write({ page: 1, pageSize });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);

  useEffect(() => {
    const requested = Number(searchParams.get('page')) || 1;
    if (requested > pageCount) write({ page: pageCount, pageSize });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pageCount]);

  const setPage = useCallback(
    (nextPage: number) => write({ page: Math.max(1, nextPage), pageSize }),
    [write, pageSize],
  );

  const setPageSize = useCallback(
    (nextPageSize: number) => write({ page: 1, pageSize: nextPageSize }),
    [write],
  );

  return useMemo(
    () => buildPagination(total, page, pageSize, setPage, setPageSize),
    [total, page, pageSize, setPage, setPageSize],
  );
}
