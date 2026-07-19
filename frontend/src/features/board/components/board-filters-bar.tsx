import { useTranslation } from 'react-i18next';
import { Archive, Download, ListFilter, Route } from 'lucide-react';

import { PRIORITIES, TASK_STATES, type Agent, type Priority, type TaskState } from '@/api';
import { Button, Input, Select } from '@/design-system';
import {
  hasActiveBoardFilters,
  type BoardArchiveFilter,
  type BoardFilters,
  type BoardPeriod,
} from '@/features/board/lib/board-filters';

export interface BoardFiltersBarProps {
  filters: BoardFilters;
  /** Agentes do projeto (opções do filtro de responsável). */
  agents: Agent[];
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
 * Barra de filtros do quadro (FR-3, 7.1): busca por título/ID, coluna,
 * responsável, prioridade, período (última atividade) e arquivamento —
 * tudo refletido na URL. Também concentra as ações da página: exportar
 * CSV do conjunto filtrado, arquivar concluídas (em lote) e a explicação
 * do fluxo. Filtro por fase NÃO existe (lacuna de contrato, D-075).
 */
export function BoardFiltersBar({
  filters,
  agents,
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
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex min-w-48 flex-col gap-1">
          <label htmlFor="board-filter-q" className="text-xs font-medium">
            {t('board.filters.search')}
          </label>
          <Input
            id="board-filter-q"
            type="search"
            value={filters.query}
            placeholder={t('board.filters.searchPlaceholder')}
            onChange={(event) => patch({ query: event.target.value })}
          />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="board-filter-state" className="text-xs font-medium">
            {t('board.filters.state')}
          </label>
          <Select
            id="board-filter-state"
            value={filters.state}
            onChange={(event) => patch({ state: event.target.value as TaskState | '' })}
          >
            <option value="">{t('board.filters.all')}</option>
            {TASK_STATES.map((state) => (
              <option key={state} value={state}>
                {t(`status.taskState.${state}`)}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="board-filter-agent" className="text-xs font-medium">
            {t('board.filters.agent')}
          </label>
          <Select
            id="board-filter-agent"
            value={filters.agentId}
            onChange={(event) => patch({ agentId: event.target.value })}
          >
            <option value="">{t('board.filters.all')}</option>
            {agents.map((agent) => (
              <option key={agent.id} value={agent.id}>
                {agent.name}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="board-filter-priority" className="text-xs font-medium">
            {t('board.filters.priority')}
          </label>
          <Select
            id="board-filter-priority"
            value={filters.priority}
            onChange={(event) => patch({ priority: event.target.value as Priority | '' })}
          >
            <option value="">{t('board.filters.all')}</option>
            {PRIORITIES.map((priority) => (
              <option key={priority} value={priority}>
                {t(`status.priority.${priority}`)}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="board-filter-period" className="text-xs font-medium">
            {t('board.filters.period')}
          </label>
          <Select
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
        <div className="flex flex-col gap-1">
          <label htmlFor="board-filter-archive" className="text-xs font-medium">
            {t('board.filters.archive')}
          </label>
          <Select
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
        <div className="ml-auto flex flex-wrap gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onShowFlow}>
            <Route aria-hidden="true" />
            {t('board.flow.open')}
          </Button>
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
