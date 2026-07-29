import { useTranslation } from 'react-i18next';
import { Archive, Download, ListFilter, Route } from 'lucide-react';

import { PRIORITIES, TASK_STATES, type Agent, type Priority, type TaskState } from '@/api';
import { Button, Input, Select } from '@/design-system';
import {
  hasActiveBoardFilters,
  BOARD_CARD_TYPES,
  type BoardArchiveFilter,
  type BoardCardType,
  type BoardFilters,
  type BoardPeriod,
} from '@/features/board/lib/board-filters';
import { taskStateLabelKey } from '@/features/board/lib/board-presentation';

export interface BoardSignatureOption {
  value: string;
  label: string;
}

export interface BoardFiltersBarProps {
  filters: BoardFilters;
  showTechnicalDetails: boolean;
  /**
   * Responsáveis REAIS (agentes com ao menos um card) — opções do filtro
   * "Responsável". Sem opções mortas: quem não recebe cards não aparece.
   */
  agents: Agent[];
  signatures: BoardSignatureOption[];
  specialties: string[];
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
 * Barra de filtros do quadro (FR-3, 7.1): busca por título/ID, coluna,
 * responsável, assinatura, especialidade, tipo, fase, prioridade, período
 * (última atividade) e arquivamento —
 * tudo refletido na URL. Também concentra as ações da página: exportar
 * CSV do conjunto filtrado, arquivar concluídas (em lote) e a explicação
 * do fluxo.
 */
export function BoardFiltersBar({
  filters,
  showTechnicalDetails,
  agents,
  signatures,
  specialties,
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
      <div className="flex flex-wrap items-end gap-3">
        {showTechnicalDetails && (
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
        )}
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
                {t(taskStateLabelKey(state, showTechnicalDetails))}
              </option>
            ))}
          </Select>
        </div>
        {showTechnicalDetails && (
          <div className="flex flex-col gap-1">
            <label htmlFor="board-filter-agent" className="text-xs font-medium">
              {t('board.filters.agent')}
            </label>
            <Select
              id="board-filter-agent"
              value={filters.agentId}
              onChange={(event) => patch({ agentId: event.target.value })}
            >
              <option value="">{t('board.filters.agentAll')}</option>
              {agents.map((agent) => (
                <option key={agent.id} value={agent.id}>
                  {agent.name}
                </option>
              ))}
            </Select>
          </div>
        )}
        {showTechnicalDetails && (
          <div className="flex flex-col gap-1">
            <label htmlFor="board-filter-signature" className="text-xs font-medium">
              {t('board.filters.signature')}
            </label>
            <Select
              id="board-filter-signature"
              value={filters.signature}
              onChange={(event) => patch({ signature: event.target.value })}
            >
              <option value="">{t('board.filters.signatureAll')}</option>
              {signatures.map((signature) => (
                <option key={signature.value} value={signature.value}>
                  {signature.label}
                </option>
              ))}
            </Select>
          </div>
        )}
        {showTechnicalDetails && (
          <div className="flex flex-col gap-1">
            <label htmlFor="board-filter-specialty" className="text-xs font-medium">
              {t('board.filters.specialty')}
            </label>
            <Select
              id="board-filter-specialty"
              value={filters.specialty}
              onChange={(event) => patch({ specialty: event.target.value })}
            >
              <option value="">{t('board.filters.specialtyAll')}</option>
              {specialties.map((specialty) => (
                <option key={specialty} value={specialty}>
                  {specialty}
                </option>
              ))}
            </Select>
          </div>
        )}
        {showTechnicalDetails && (
          <div className="flex flex-col gap-1">
            <label htmlFor="board-filter-type" className="text-xs font-medium">
              {t('board.filters.type')}
            </label>
            <Select
              id="board-filter-type"
              value={filters.cardType}
              onChange={(event) => patch({ cardType: event.target.value as BoardCardType | '' })}
            >
              <option value="">{t('board.filters.all')}</option>
              {BOARD_CARD_TYPES.map((cardType) => (
                <option key={cardType} value={cardType}>
                  {t(`board.card.types.${cardType}`)}
                </option>
              ))}
            </Select>
          </div>
        )}
        <div className="flex flex-col gap-1">
          <label htmlFor="board-filter-phase" className="text-xs font-medium">
            {t('board.filters.phase')}
          </label>
          <Select
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
        {showTechnicalDetails && (
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
        )}
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
