import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { Button, Select } from '@/design-system';
import { cn } from '@/lib/utils';
import { PAGE_SIZE_OPTIONS, type Pagination } from '@/features/shared/hooks/use-pagination';

export interface PaginationBarProps {
  pagination: Pagination;
  className?: string;
}

/** Janela máxima de páginas numeradas exibidas de uma vez. */
const MAX_NUMBERED_PAGES = 7;

/**
 * Barra de paginação padrão (D-063): intervalo atual ("1–15 de 42"),
 * seletor de itens por página (15/30/50) e controles anterior/próxima
 * (com páginas numeradas quando cabem). Só renderiza quando a coleção
 * excede o menor tamanho de página — listas curtas não ganham chrome.
 */
export function PaginationBar({ pagination, className }: PaginationBarProps) {
  const { t } = useTranslation();
  const perPageId = useId();
  const { page, pageCount, pageSize, total, rangeStart, rangeEnd, setPage, setPageSize } =
    pagination;

  if (total <= PAGE_SIZE_OPTIONS[0]) return null;

  const numbered = pageCount <= MAX_NUMBERED_PAGES;
  const pages = numbered ? Array.from({ length: pageCount }, (_, index) => index + 1) : [];

  return (
    <nav
      aria-label={t('common.pagination.label')}
      className={cn('flex flex-wrap items-center justify-between gap-3', className)}
    >
      <p className="text-sm text-foreground-muted" aria-live="polite">
        {t('common.pagination.range', { from: rangeStart, to: rangeEnd, total })}
      </p>

      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <label htmlFor={perPageId} className="text-xs text-foreground-muted">
            {t('common.pagination.perPage')}
          </label>
          <Select
            id={perPageId}
            className="w-auto"
            value={String(pageSize)}
            onChange={(event) => setPageSize(Number(event.target.value))}
          >
            {PAGE_SIZE_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </Select>
        </div>

        <div className="flex items-center gap-1">
          <Button
            type="button"
            variant="outline"
            size="icon"
            disabled={page <= 1}
            onClick={() => setPage(page - 1)}
            aria-label={t('common.pagination.previous')}
          >
            <ChevronLeft aria-hidden="true" />
          </Button>
          {numbered ? (
            <ul className="flex items-center gap-1">
              {pages.map((pageNumber) => (
                <li key={pageNumber}>
                  <Button
                    type="button"
                    variant={pageNumber === page ? 'primary' : 'ghost'}
                    size="icon"
                    onClick={() => setPage(pageNumber)}
                    aria-label={t('common.pagination.page', { page: pageNumber })}
                    aria-current={pageNumber === page ? 'page' : undefined}
                  >
                    {pageNumber}
                  </Button>
                </li>
              ))}
            </ul>
          ) : (
            <span className="px-2 text-sm text-foreground-muted">
              {t('common.pagination.pageOf', { page, total: pageCount })}
            </span>
          )}
          <Button
            type="button"
            variant="outline"
            size="icon"
            disabled={page >= pageCount}
            onClick={() => setPage(page + 1)}
            aria-label={t('common.pagination.next')}
          >
            <ChevronRight aria-hidden="true" />
          </Button>
        </div>
      </div>
    </nav>
  );
}
