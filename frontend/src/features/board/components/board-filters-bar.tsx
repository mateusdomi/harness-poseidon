import { useTranslation } from 'react-i18next';
import { Archive, Download, ListFilter, Route } from 'lucide-react';

import { TASK_STATES, type TaskState } from '@/api';
import { Button, Select } from '@/design-system';
import {
  hasActiveBoardFilters,
  type BoardArchiveFilter,
  type BoardFilters,
  type BoardPeriod,
} from '@/features/board/lib/board-filters';
import { taskStateLabelKey } from '@/features/board/lib/board-presentation';

export interface BoardFiltersBarProps {
  filters: BoardFilters;
  showTechnicalDetails: boolean;
  phases: string[];
  /** Resultado do conjunto filtrado vs. total do projeto. */
  filteredCount: number;
  totalCount: number;
  /** Concluídas ainda ativas (elegíveis ao arquivamento em lote). */
  archivableCount: number;
  exportPending: boolean;
  onFiltersChange: (filters: BoardFilters) => void;
  onClear: () => void;
  onExport: () => void;
  onArchiveCompleted: () => void;
  onShowFlow: () => void;
}

/**
 * Barra enxuta do quadro: Coluna, Etapa, Última atividade, Arquivamento
 * e Limpar filtros. Tudo é refletido na URL. As ações técnicas continuam
 * separadas dos filtros.
 */
export function BoardFiltersBar({
  filters,
  showTechnicalDetails,
  phases,
  filteredCount,
  totalCount,
  archivableCount,
  exportPending,
  onFiltersChange,
  onClear,
  onExport,
  onArchiveCompleted,
  onShowFlow,
}: BoardFiltersBarProps) {
  const { t } = useTranslation();
  const patch = (partial: Partial<BoardFilters>) => onFiltersChange({ ...filters, ...partial });

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-3">
      <div className="grid grid-cols-1 items-end gap-3 md:grid-cols-2 lg:grid-cols-5">
        <div className="flex min-w-0 flex-col gap-1">
          <label htmlFor="board-filter-state" className="text-xs font-medium">
            {t('board.filters.state')}
          </label>
          <Select
            className="w-full"
            id="board-filter-state"
            value={filters.state}
            onChange={(event) => patch({ state: event.target.value as TaskState | '' })}
          >
            <option value="">{t('board.filters.all')}</option>
            {TASK_STATES.map((state) => (
              <option key={state} value={state}>
                {t(taskStateLabelKey(state, showTechnicalDetails))}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex min-w-0 flex-col gap-1">
          <label htmlFor="board-filter-phase" className="text-xs font-medium">
            {t('board.filters.phase')}
          </label>
          <Select
            className="w-full"
            id="board-filter-phase"
            value={filters.phase}
            onChange={(event) => patch({ phase: event.target.value })}
          >
            <option value="">{t('board.filters.phaseAll')}</option>
            {phases.map((phase) => (
              <option key={phase} value={phase}>
                {phase}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex min-w-0 flex-col gap-1">
          <label htmlFor="board-filter-period" className="text-xs font-medium">
            {t('board.filters.period')}
          </label>
          <Select
            className="w-full"
            id="board-filter-period"
            value={filters.period}
            onChange={(event) => patch({ period: event.target.value as BoardPeriod })}
          >
            <option value="all">{t('board.filters.periodAll')}</option>
            <option value="today">{t('board.filters.periodToday')}</option>
            <option value="7d">{t('board.filters.period7d')}</option>
            <option value="30d">{t('board.filters.period30d')}</option>
          </Select>
        </div>
        <div className="flex min-w-0 flex-col gap-1">
          <label htmlFor="board-filter-archive" className="text-xs font-medium">
            {t('board.filters.archive')}
          </label>
          <Select
            className="w-full"
            id="board-filter-archive"
            value={filters.archive}
            onChange={(event) => patch({ archive: event.target.value as BoardArchiveFilter })}
          >
            <option value="active">{t('board.filters.archiveActive')}</option>
            <option value="archived">{t('board.filters.archiveArchived')}</option>
            <option value="all">{t('board.filters.archiveAll')}</option>
          </Select>
        </div>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="w-full md:w-auto"
          disabled={!hasActiveBoardFilters(filters)}
          onClick={onClear}
        >
          <ListFilter aria-hidden="true" />
          {t('board.filters.clear')}
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <p className="text-xs text-foreground-muted" role="status">
          {t('board.filters.results', { count: filteredCount, total: totalCount })}
        </p>
        <div className="flex w-full flex-wrap gap-2 md:ml-auto md:w-auto">
          <Button type="button" variant="outline" size="sm" onClick={onShowFlow}>
            <Route aria-hidden="true" />
            {t('board.flow.open')}
          </Button>
          {showTechnicalDetails && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={filteredCount === 0 || exportPending}
              onClick={onExport}
            >
              <Download aria-hidden="true" />
              {t('board.export.button')}
            </Button>
          )}
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={archivableCount === 0}
            onClick={onArchiveCompleted}
          >
            <Archive aria-hidden="true" />
            {t('board.archive.batchButton', { count: archivableCount })}
          </Button>
        </div>
      </div>
    </div>
  );
}
